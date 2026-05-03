using System.Text;
using System.Text.RegularExpressions;
using GenQAServer.Options;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Infrastructure.Services;

public class MarkdownService : IMarkdownService
{
    private readonly ITokenCountService _tokenCountService;
    private readonly DocumentProcessOption _documentProcessOption;
    private readonly MarkdownPipeline _pipeline;
    private readonly SystemPrompts _systemPrompts;
    private readonly ILogger<MarkdownService> _logger;
    private readonly IS3Service _s3Service;
    private readonly MinioOptions _minioOptions;
    private readonly LlmService _llmService;

    public MarkdownService(
        ITokenCountService tokenCountService,
        IOptions<DocumentProcessOption> documentProcessOption,
        LlmService llmService,
        SystemPrompts systemPrompts,
        ILogger<MarkdownService> logger,
        IS3Service s3Service,
        IOptions<MinioOptions> minioOptions)
    {
        _tokenCountService = tokenCountService;
        _documentProcessOption = documentProcessOption.Value ?? throw new ArgumentNullException("DocumentProcessOption is missing");
        _llmService = llmService;
        _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        _systemPrompts = systemPrompts;
        _logger = logger;
        _s3Service = s3Service;
        _minioOptions = minioOptions.Value ?? throw new ArgumentNullException("MinioOptions is missing");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  IMarkdownService — CreateChunkAsync
    //  Logic chunking thuần túy theo header/token. Không liên quan đến table.
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<List<ChunkInfo>> CreateChunkAsync(string source, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting CreateChunkAsync");

        if (string.IsNullOrWhiteSpace(source)) return new();

        // 1. Chia nhỏ văn bản thành các ngữ cảnh xử lý (Rất nhanh, không bị block bởi AI)
        var contexts = await SplitTextByHeaderAsync(source, 1, new Stack<KeyValuePair<int, string>>(), cancellationToken);

        // 2. Chạy song song tìm bảng cho từng ngữ cảnh
        var tableTasks = contexts.Select(async ctx =>
        {
            ctx.Chunk.TableChunks = await CreateChunkTableAsync(ctx.RawContent, ctx.HierarchyStack, cancellationToken);
        });

        await Task.WhenAll(tableTasks);

        return contexts.Select(c => c.Chunk).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  IMarkdownService — CreateChunkTableAsync  (luồng mới, hoàn toàn độc lập)
    //
    //  Luồng:
    //    1. Parse source → lấy tất cả HeadingBlock.
    //    2. Với mỗi khoảng content giữa các header, snapshot hierarchy tại thời điểm đó.
    //    3. Mỗi (content, hierarchy) → 1 Task riêng gọi GetTableChunksAsync (AI merge table).
    //    4. Task.WhenAll → flatten kết quả.
    //  Không cần wrapper concurrency vì ChatChoice rất nhanh.
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<List<ChunkInfo>> CreateChunkTableAsync(string source, Stack<KeyValuePair<int, string>>? parentHierarchy = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting CreateChunkTableAsync");

        if (string.IsNullOrWhiteSpace(source)) return new();

        var blocks = MarkdownServiceHelper.GetAllBlock(source, _pipeline);
        var headers = blocks.OfType<HeadingBlock>().ToList();

        // Danh sách segment: mỗi phần tử là (content, hierarchyPath đã resolve tại thời điểm đó)
        var segments = new List<(string Content, string HierarchyPath)>();

        var hierarchy = parentHierarchy != null ? CloneStack(parentHierarchy) : new Stack<KeyValuePair<int, string>>();
        int lastPos = 0;

        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var headerTitle = source.Substring(header.Span.Start, header.Span.Length);

            // Content từ lastPos đến ngay trước header hiện tại
            var contentBefore = source.Substring(lastPos, header.Span.Start - lastPos);
            if (!string.IsNullOrWhiteSpace(contentBefore))
            {
                // Snapshot hierarchy TRƯỚC khi cập nhật header hiện tại → resolve thành string ngay
                segments.Add((contentBefore, GetHierarchyPath(hierarchy)));
            }

            // Cập nhật hierarchy VỚI header hiện tại
            UpdateHierarchyStack(hierarchy, new KeyValuePair<int, string>(header.Level, headerTitle));
            lastPos = header.Span.End + 1;
        }

        // Content còn lại phía dưới header cuối cùng
        var contentTail = source.Substring(lastPos);
        if (!string.IsNullOrWhiteSpace(contentTail))
        {
            segments.Add((contentTail, GetHierarchyPath(hierarchy)));
        }

        // Mỗi segment → 1 task độc lập → gọi AI để merge table bên trong
        var tasks = segments
            .Select(seg => GetTableChunksAsync(seg.Content, seg.HierarchyPath, cancellationToken))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var allTableChunks = results.SelectMany(r => r).ToList();

        // New Batch Token Count Logic
        if (allTableChunks.Count > 0)
        {
            _logger.LogInformation("Counting tokens for {Count} table chunks in batch", allTableChunks.Count);
            var batchRequest = new BatchCountRequest
            {
                Items = allTableChunks.Select((c, i) => new BatchItemRequest
                {
                    Id = i.ToString(),
                    Text = c.Content
                }).ToList(),
                ReturnTokens = true
            };

            try
            {
                var batchResponse = await _tokenCountService.BatchCountAsync(batchRequest, cancellationToken);
                var resultDict = batchResponse.Results.Where(r => r.Id != null).ToDictionary(r => r.Id!, r => r.TokenCount);

                for (int i = 0; i < allTableChunks.Count; i++)
                {
                    if (resultDict.TryGetValue(i.ToString(), out int tc))
                    {
                        allTableChunks[i].TokensCount = tc;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch token count failed for table chunks. Falling back to 0.");
            }
        }

        return allTableChunks;
    }

    /// <summary>
    /// Chuyển đổi các image references trong markdown sang MinIO public links với thông tin pages từ OCR response.
    /// Xử lý từng page riêng lẻ để tránh xung đột khi các page có image path trùng nhau.
    /// </summary>
    /// <param name="markdownContent">Nội dung markdown hoàn chỉnh (đã nối từ các pages) - không sử dụng, sẽ tự nối từ pages</param>
    /// <param name="documentId">Document ID</param>
    /// <param name="pages">Danh sách pages từ OCR response</param>
    /// <param name="uploadSemaphore">Optional semaphore to limit concurrent uploads</param>
    /// <returns>Markdown với tất cả images đã được thay thế bằng MinIO public links</returns>
    /// <inheritdoc/>
    public async Task<string> TransformPagesImagesToMinioLinkAsync(string markdownContent, string documentId, List<PageOcrResult> pages, SemaphoreSlim? uploadSemaphore = null)
    {
        _logger.LogInformation("Transforming images from {PageCount} pages to MinIO links for document {DocumentId}", pages.Count, documentId);

        // Xử lý từng page riêng lẻ
        var processedPagesTasks = pages.Select(async page =>
        {
            var pageMarkdown = page.Markdown;

            // Xử lý từng image trong dictionary
            foreach (var kvp in page.Images)
            {
                var imageKey = kvp.Key;      // e.g., "120_340_580_720.jpg"
                var base64Data = kvp.Value;  // Base64-encoded image data

                if (string.IsNullOrEmpty(base64Data))
                {
                    _logger.LogWarning("Image key {ImageKey} has empty base64 data for page {PageIndex}", imageKey, page.PageIndex);
                    continue;
                }

                try
                {
                    // Wait for semaphore if provided (to limit concurrency)
                    if (uploadSemaphore != null)
                    {
                        await uploadSemaphore.WaitAsync();
                    }

                    var imageBytes = Convert.FromBase64String(base64Data);

                    // Extract extension from imageKey
                    var ext = Path.GetExtension(imageKey).TrimStart('.').ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = "jpg";

                    // Object key format: image/{docId}/page{pageIndex}_{imageKey}
                    var objectKey = $"image/{documentId}/page{page.PageIndex}_{imageKey}";

                    using var stream = new MemoryStream(imageBytes);
                    await _s3Service.UploadFileAsync(stream, objectKey, S3BucketName.PublicImages, $"image/{ext}");

                    var minioUrl = $"{_minioOptions.PublicEndpoint}/{S3BucketName.PublicImages}/{objectKey}";

                    // Replace image references trong markdown - hỗ trợ cả 2 format:
                    // Case 1: Markdown syntax: ![alt](imageKey)
                    // Case 2: HTML syntax: <img src="imageKey" /> hoặc <img alt="..." src="imageKey" ...>
                    var mdPattern = $@"!\[([^\]]*)\]\({Regex.Escape(imageKey)}\)";
                    pageMarkdown = Regex.Replace(pageMarkdown, mdPattern, $"![$1]({minioUrl})");

                    // Pattern mới: cho phép src ở bất kỳ vị trí nào trong thẻ img, với các attribute khác xen kẽ
                    var htmlPattern = $@"(<img\b[^>]*?)\bsrc=""{Regex.Escape(imageKey)}""([^>]*?>)";
                    pageMarkdown = Regex.Replace(pageMarkdown, htmlPattern, $"$1src=\"{minioUrl}\"$2", RegexOptions.IgnoreCase);

                    _logger.LogDebug("Replaced image key {ImageKey} with MinIO URL for page {PageIndex}", imageKey, page.PageIndex);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upload image {ImageKey} (Page {PageIndex})", imageKey, page.PageIndex);
                }
                finally
                {
                    uploadSemaphore?.Release();
                }
            }

            return pageMarkdown;
        });

        var processedPageMarkdowns = await Task.WhenAll(processedPagesTasks);

        // Nối markdown từ các pages
        var sb = new StringBuilder();
        for (int i = 0; i < processedPageMarkdowns.Length; i++)
        {
            sb.AppendLine(processedPageMarkdowns[i]);
            sb.AppendLine($"Page {pages[i].PageIndex + 1}");
            sb.AppendLine();
            sb.AppendLine("---");
        }

        _logger.LogInformation("Transformed {ImageCount} images to MinIO links for document {DocumentId}",
            pages.Sum(p => p.Images.Count), documentId);
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — chunk splitting (dùng bởi CreateChunkAsync)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<ChunkProcessingContext>> SplitTextByHeaderAsync(string source, int level, Stack<KeyValuePair<int, string>> hierarchy, CancellationToken cancellationToken = default, Guid? workerId = null)
    {
        if (string.IsNullOrWhiteSpace(source)) return new();

        var tokens = (await _tokenCountService.CountAsync(new() { Text = source })).TokenCount;
        if (tokens < _documentProcessOption.MaxChunkSize)
        {
            var chunk = CreateChunk(source, tokens, TypeChunk.Text, GetHierarchyPath(hierarchy));
            return new List<ChunkProcessingContext>
            {
                new ChunkProcessingContext
                {
                    Chunk = chunk,
                    RawContent = source,
                    HierarchyStack = CloneStack(hierarchy)
                }
            };
        }

        if (level > _documentProcessOption.MaxHeaderDepth)
        {
            var summarizedContent = await SummarizeContentAsync(source, cancellationToken, workerId);
            var chunk = CreateChunk(source, tokens, TypeChunk.Summary, GetHierarchyPath(hierarchy), contentSummary: summarizedContent);
            return new List<ChunkProcessingContext>
            {
                new ChunkProcessingContext
                {
                    Chunk = chunk,
                    RawContent = source,
                    HierarchyStack = CloneStack(hierarchy)
                }
            };
        }

        var blocks = MarkdownServiceHelper.GetAllBlock(source, _pipeline);
        var headers = blocks.OfType<HeadingBlock>().Where(h => h.Level == level).ToList();

        if (!headers.Any()) return await SplitTextByHeaderAsync(source, level + 1, hierarchy, cancellationToken, workerId);

        var result = new List<ChunkProcessingContext>();
        var lastPos = 0;

        for (int i = 0; i < headers.Count; i++)
        {
            var currentHeader = headers[i];
            var nextHeaderStart = (i + 1 < headers.Count) ? headers[i + 1].Span.Start : source.Length;

            // Content TRƯỚC header hiện tại
            var subContent = source.Substring(lastPos, currentHeader.Span.Start - lastPos);
            if (!string.IsNullOrWhiteSpace(subContent))
                result.AddRange(await SplitTextByHeaderAsync(subContent, level + 1, CloneStack(hierarchy), cancellationToken, workerId));

            // Content CỦA header hiện tại → hết phần của nó
            var headerTitle = source.Substring(currentHeader.Span.Start, currentHeader.Span.Length);
            var headerContent = source.Substring(currentHeader.Span.Start, nextHeaderStart - currentHeader.Span.Start);

            UpdateHierarchyStack(hierarchy, new KeyValuePair<int, string>(level, headerTitle));
            result.AddRange(await SplitTextByHeaderAsync(headerContent, level + 1, CloneStack(hierarchy), cancellationToken, workerId));

            lastPos = nextHeaderStart;
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — table chunk processing (dùng bởi CreateChunkTableAsync)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<ChunkInfo>> GetTableChunksAsync(string source, string hierarchyPath = "", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) return new();

        var blocks = MarkdownServiceHelper.GetAllBlock(source, _pipeline);
        var resultChunks = new List<ChunkInfo>();
        var tableSegments = new List<Block>();
        string titleTable = string.Empty;

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            // Chỉ xử lý table block (segment đầu vào không có header)
            bool isTable = block is Table || (block is HtmlBlock hb && hb.Lines.ToString().TrimStart().StartsWith("<table"));
            if (!isTable) continue;

            // Lấy title của table (block ngay trước nếu là title block)
            if (string.IsNullOrEmpty(titleTable) && i > 0 && IsTitleBlock(blocks[i - 1], source))
                titleTable = source.Substring(blocks[i - 1].Span.Start, blocks[i - 1].Span.Length);

            if (tableSegments.Any())
            {
                // Hỏi AI: hai table này có phải là cùng 1 bảng không?
                var t1 = Extract(source, tableSegments, block);
                var t2 = source.Substring(block.Span.Start, block.Span.Length);
                var choice = await _llmService.ChatChoiceAsync(
                    LlmChatHelper.CreateChatMessageChoice(t1, t2, _systemPrompts.Choice),
                    new() { "Yes", "No" },
                    cancellationToken);

                if (choice.Trim().ToLower().StartsWith("y"))
                {
                    // Merge vào segment hiện tại
                    tableSegments.Add(block);
                }
                else
                {
                    // Chốt segment cũ, bắt đầu segment mới
                    resultChunks.Add(CreateTableChunk(source, tableSegments, hierarchyPath, titleTable));
                    tableSegments = new() { block };
                    titleTable = (i > 0 && IsTitleBlock(blocks[i - 1], source))
                        ? source.Substring(blocks[i - 1].Span.Start, blocks[i - 1].Span.Length)
                        : string.Empty;
                }
            }
            else
            {
                tableSegments.Add(block);
            }
        }

        // Chốt segment cuối
        if (tableSegments.Any())
        {
            resultChunks.Add(CreateTableChunk(source, tableSegments, hierarchyPath, titleTable));
        }

        // Fallback title: dùng phần cuối của hierarchyPath nếu chunk chưa có title
        var titleFallback = hierarchyPath.Contains(" --> ")
            ? hierarchyPath[(hierarchyPath.LastIndexOf(" --> ") + 5)..]
            : hierarchyPath;
        foreach (var chunk in resultChunks)
        {
            if (string.IsNullOrEmpty(chunk.Title))
                chunk.Title = titleFallback;
        }

        return resultChunks;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — misc helpers
    // ─────────────────────────────────────────────────────────────────────────

    private ChunkInfo CreateChunk(string content, int tokens, TypeChunk type, string hierarchy, string title = "", string? contentSummary = null)
        => new()
        {
            Content = content,
            TokensCount = tokens,
            Type = type,
            TittleHirarchy = hierarchy,
            Title = title,
            ContentSummary = contentSummary
        };

    private void UpdateHierarchyStack(Stack<KeyValuePair<int, string>> hierarchy, KeyValuePair<int, string> header)
    {
        if (string.IsNullOrEmpty(header.Value)) return;
        if (hierarchy.Count == 0)
        {
            hierarchy.Push(header);
            return;
        }

        if (hierarchy.Peek().Key < header.Key)
        {
            hierarchy.Push(header);
            return;
        }

        while (hierarchy.Count > 0 && hierarchy.Peek().Key >= header.Key)
            hierarchy.Pop();

        hierarchy.Push(header);
    }

    private string GetHierarchyPath(Stack<KeyValuePair<int, string>> hierarchy)
    {
        if (hierarchy != null && hierarchy.Count > 0)
        {
            var pathElements = hierarchy.ToArray().Reverse();
            return string.Join(" --> ", pathElements.Select(x => x.Value));
        }
        return string.Empty;
    }

    private Stack<KeyValuePair<int, string>> CloneStack(Stack<KeyValuePair<int, string>> source)
        => new Stack<KeyValuePair<int, string>>(source.Reverse());

    private bool IsTitleBlock(Block b, string src)
    {
        if (b is not ParagraphBlock) return b is HeadingBlock || b is ListItemBlock || b is ListBlock;
        var content = src.Substring(b.Span.Start, b.Span.Length);
        return !content.Contains("page", StringComparison.OrdinalIgnoreCase)
            && !content.Contains("trang", StringComparison.OrdinalIgnoreCase);
    }

    private string Extract(string src, List<Block> segments, Block table2)
        => src.Substring(segments[0].Span.Start, table2.Span.End - segments[0].Span.Start + 1);

    private ChunkInfo CreateTableChunk(string src, List<Block> segments, string hierarchy, string title)
    {
        var content = src.Substring(segments[0].Span.Start, segments.Last().Span.End - segments[0].Span.Start + 1);
        return CreateChunk(content, 0, TypeChunk.Table, hierarchy, title);
    }

    private async Task<string> SummarizeContentAsync(string content, CancellationToken cancellationToken, Guid? workerId)
    {
        _logger.LogInformation("Summarizing large chunk as fallback using LlmService");
        return await _llmService.GenSummaryAsync(content, null, _systemPrompts, cancellationToken);
    }

    private class ChunkProcessingContext
    {
        public required ChunkInfo Chunk { get; set; }
        public required string RawContent { get; set; }
        public required Stack<KeyValuePair<int, string>> HierarchyStack { get; set; }
    }
}
