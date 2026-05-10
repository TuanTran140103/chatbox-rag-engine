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

        var contexts = await SplitTextByHeaderAsync(source, 1, new Stack<KeyValuePair<int, string>>(), cancellationToken);
        _logger.LogInformation($"Done CreateChunkText: {contexts.Count} chunks.");

        var chunks = contexts.Select(c => c.Chunk).ToList();
        for (int i = 0; i < chunks.Count; i++)
            chunks[i].Index = i + 1;
        return chunks;
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

        var tables = MarkdownServiceHelper.GetTableBlocks(source, _pipeline);
        _logger.LogInformation("Found {Count} table(s) in source", tables.Count);
        if (tables.Count == 0) return new();

        foreach (var (b, info, idx) in tables)
        {
            _logger.LogInformation(
                "  Table[blockIndex={Index}]: hasHeader={HasHeader}, {ColCount} cols, {RowCount} rows, headers=[{Headers}]",
                idx, info.HasHeader, info.ColumnCount, info.RowCount,
                info.HeaderCells != null ? string.Join(" | ", info.HeaderCells) : "(none)");
        }

        var hierarchy = parentHierarchy != null ? CloneStack(parentHierarchy) : new Stack<KeyValuePair<int, string>>();

        var segments = new List<List<(MarkdownServiceHelper.TableContinuationInfo Info, int Index)>>();
        var current = new List<(MarkdownServiceHelper.TableContinuationInfo Info, int Index)>();
        int heuristicDecisions = 0;
        int aiCalls = 0;

        for (int ti = 0; ti < tables.Count; ti++)
        {
            var (block, info, idx) = tables[ti];

            if (current.Count == 0)
            {
                current.Add((info, idx));
                _logger.LogInformation("[Segment start] Table blockIndex={Idx} added as first segment", idx);
                continue;
            }

            _logger.LogInformation(
                "[Decision #{Ti}/{Total}] Comparing candidate table blockIndex={Idx} (hasHeader={HasHeader}, {ColCount} cols) vs segment of {SegCount} table(s)",
                ti + 1, tables.Count, idx, info.HasHeader, info.ColumnCount, current.Count);

            var headerRefs = current.Where(x => x.Info.HasHeader).ToList();
            _logger.LogInformation("  Header references in segment: {Count} table(s)", headerRefs.Count);

            bool isContinuation;
            string method;

            if (headerRefs.Count == 0)
            {
                _logger.LogInformation("  -> No header references, falling back to AI...");
                isContinuation = await CallAiFallbackForTableAsync(source, tables, current, block, idx, cancellationToken);
                method = isContinuation ? "AI (no header ref, yes)" : "AI (no header ref, no)";
                aiCalls++;
            }
            else
            {
                var scores = new List<double>();
                foreach (var h in headerRefs)
                {
                    double score = MarkdownServiceHelper.CalculateSimilarity(h.Info, info);
                    scores.Add(score);
                    _logger.LogInformation("  Pair (header table idx={HIdx}) similarity: {Score:F4}", h.Index, score);
                }

                var (minScore, maxScore) = MarkdownServiceHelper.GetScoreRange(scores);
                _logger.LogInformation("  Range: min={Min:F4}, max={Max:F4}", minScore, maxScore);

                var decision = MarkdownServiceHelper.HeuristicDecisionByRange(minScore, maxScore);
                if (decision.HasValue)
                {
                    isContinuation = decision.Value;
                    method = isContinuation
                        ? $"Heuristic (min={minScore:F4} >= 0.70)"
                        : $"Heuristic (max={maxScore:F4} <= 0.25)";
                    heuristicDecisions++;
                    _logger.LogInformation("  -> {Method}: {Result}", method,
                        isContinuation ? "CONTINUE" : "NOT CONTINUE");
                }
                else
                {
                    _logger.LogInformation("  -> Grey zone (min={Min:F4}, max={Max:F4}), falling back to AI...", minScore, maxScore);
                    isContinuation = await CallAiFallbackForTableAsync(source, tables, current, block, idx, cancellationToken);
                    method = $"AI (grey zone min={minScore:F4}, max={maxScore:F4})";
                    aiCalls++;
                }
            }

            if (isContinuation)
            {
                current.Add((info, idx));
                _logger.LogInformation("  => MERGED into current segment (now {Count} tables)", current.Count);
            }
            else
            {
                segments.Add(current);
                _logger.LogInformation("  => FINALIZED segment with {Count} tables, starting new segment", current.Count);
                current = new() { (info, idx) };
            }
        }

        if (current.Count > 0)
            segments.Add(current);

        _logger.LogInformation(
            "=== DONE: {Tables} tables → {Chunks} chunks | Heuristic={H}, AI={Ai} ===",
            tables.Count, segments.Count, heuristicDecisions, aiCalls);

        var hierarchyPath = GetHierarchyPath(hierarchy);
        var titleFallback = hierarchyPath.Contains(" --> ")
            ? hierarchyPath[(hierarchyPath.LastIndexOf(" --> ") + 5)..]
            : hierarchyPath;

        var chunks = segments.Select((seg, ci) =>
        {
            var firstBlock = tables.First(t => t.index == seg[0].Index).block;
            var lastBlock = tables.First(t => t.index == seg[^1].Index).block;
            var content = source.Substring(firstBlock.Span.Start, lastBlock.Span.End - firstBlock.Span.Start + 1);
            return new ChunkInfo
            {
                Content = content,
                TokensCount = 0,
                Type = TypeChunk.Table,
                TittleHirarchy = hierarchyPath,
                Title = titleFallback
            };
        }).ToList();

        if (chunks.Count > 0)
        {
            var batchRequest = new BatchCountRequest
            {
                Items = chunks.Select((c, i) => new BatchItemRequest { Id = i.ToString(), Text = c.Content }).ToList(),
                ReturnTokens = true
            };
            try
            {
                var batchResponse = await _tokenCountService.BatchCountAsync(batchRequest, cancellationToken);
                var resultDict = batchResponse.Results.Where(r => r.Id != null).ToDictionary(r => r.Id!, r => r.TokenCount);
                for (int i = 0; i < chunks.Count; i++)
                    if (resultDict.TryGetValue(i.ToString(), out int tc))
                        chunks[i].TokensCount = tc;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch token count failed for table chunks.");
                throw;
            }
        }

        _logger.LogInformation("CreateChunkTableAsync done: {Count} table chunks", chunks.Count);
        return chunks;
    }

    private async Task<bool> CallAiFallbackForTableAsync(
        string source,
        List<(Block block, MarkdownServiceHelper.TableContinuationInfo Info, int index)> allTables,
        List<(MarkdownServiceHelper.TableContinuationInfo Info, int Index)> segment,
        Block candidateBlock,
        int candidateIndex,
        CancellationToken ct)
    {
        var firstBlock = allTables.First(t => t.index == segment[0].Index).block;
        var t1 = source.Substring(firstBlock.Span.Start, candidateBlock.Span.End - firstBlock.Span.Start + 1);
        var t2 = MarkdownServiceHelper.GetBlockText(source, candidateBlock);

        _logger.LogInformation(
            "    [AI Call] Segment [idx {SegFirst}..{SegLast}] vs candidate idx={CandIdx}, context={CtxLen}ch, target={TgtLen}ch",
            segment[0].Index, segment[^1].Index, candidateIndex, t1.Length, t2.Length);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var choice = await _llmService.ChatChoiceAsync(
            LlmChatHelper.CreateChatMessageChoice(t1, t2, _systemPrompts.Choice),
            new() { "Yes", "No" },
            ct);
        sw.Stop();

        var result = !string.IsNullOrEmpty(choice) && choice.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation(
            "    [AI Call] Done: choice='{Choice}', parsed={Result}, elapsed={Elapsed:F2}s",
            choice ?? "(null)", result, sw.Elapsed.TotalSeconds);
        return result;
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

    /// <inheritdoc/>
    public async Task<List<SummaryChunk>> SplitDocumentForSummaryAsync(string source, int maxTokensPerChunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) return new();

        var tokens = (await _tokenCountService.CountAsync(new() { Text = source }, cancellationToken)).TokenCount;
        if (tokens <= maxTokensPerChunk)
        {
            return new List<SummaryChunk>
            {
                new SummaryChunk { Content = source, HierarchyPath = string.Empty, Title = string.Empty, TokensCount = tokens }
            };
        }

        return await SplitSummaryByHeaderAsync(source, 1, new Stack<KeyValuePair<int, string>>(), maxTokensPerChunk, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<SummaryChunk>> SplitDocumentTopLevelAsync(string source, int maxTokensPerChunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) return new();

        var blocks = MarkdownServiceHelper.GetAllBlock(source, _pipeline);
        var h1Headers = blocks.OfType<HeadingBlock>().Where(h => h.Level == 1).ToList();
        var targetLevel = h1Headers.Count > 0 ? 1 : 2;

        var headers = targetLevel == 1
            ? h1Headers
            : blocks.OfType<HeadingBlock>().Where(h => h.Level == 2).ToList();

        if (headers.Count == 0)
        {
            return new List<SummaryChunk>
            {
                new SummaryChunk { Content = source, HierarchyPath = string.Empty, Title = string.Empty }
            };
        }

        var result = new List<SummaryChunk>();
        var hierarchy = new Stack<KeyValuePair<int, string>>();

        for (int i = 0; i < headers.Count; i++)
        {
            var currentHeader = headers[i];
            var nextStart = (i + 1 < headers.Count) ? headers[i + 1].Span.Start : source.Length;
            var headerTitle = source.Substring(currentHeader.Span.Start, currentHeader.Span.Length);
            var chunkContent = source.Substring(currentHeader.Span.Start, nextStart - currentHeader.Span.Start);

            var subHierarchy = CloneStack(hierarchy);
            subHierarchy.Push(new KeyValuePair<int, string>(currentHeader.Level, headerTitle));
            var hierarchyPath = GetHierarchyPath(subHierarchy);

            result.Add(new SummaryChunk
            {
                Content = chunkContent,
                HierarchyPath = hierarchyPath,
                Title = headerTitle
            });
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — summary splitting helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<SummaryChunk>> SplitSummaryByHeaderAsync(
        string source,
        int level,
        Stack<KeyValuePair<int, string>> hierarchy,
        int maxTokensPerChunk,
        CancellationToken cancellationToken)
    {
        var tokens = (await _tokenCountService.CountAsync(new() { Text = source }, cancellationToken)).TokenCount;
        if (tokens <= maxTokensPerChunk)
        {
            return new List<SummaryChunk>
            {
                new SummaryChunk
                {
                    Content = source,
                    HierarchyPath = GetHierarchyPath(hierarchy),
                    Title = hierarchy.Count > 0 ? hierarchy.Peek().Value : string.Empty,
                    TokensCount = tokens
                }
            };
        }

        if (level > _documentProcessOption.MaxHeaderDepth)
        {
            return new List<SummaryChunk>
            {
                new SummaryChunk
                {
                    Content = source,
                    HierarchyPath = GetHierarchyPath(hierarchy),
                    Title = hierarchy.Count > 0 ? hierarchy.Peek().Value : string.Empty,
                    TokensCount = tokens
                }
            };
        }

        var blocks = MarkdownServiceHelper.GetAllBlock(source, _pipeline);
        var headers = blocks.OfType<HeadingBlock>().Where(h => h.Level == level).ToList();

        if (headers.Count == 0)
            return await SplitSummaryByHeaderAsync(source, level + 1, hierarchy, maxTokensPerChunk, cancellationToken);

        var result = new List<SummaryChunk>();

        for (int i = 0; i < headers.Count; i++)
        {
            var currentHeader = headers[i];
            var nextStart = (i + 1 < headers.Count) ? headers[i + 1].Span.Start : source.Length;
            var headerTitle = source.Substring(currentHeader.Span.Start, currentHeader.Span.Length);
            var chunkContent = source.Substring(currentHeader.Span.Start, nextStart - currentHeader.Span.Start);

            var subHierarchy = CloneStack(hierarchy);
            subHierarchy.Push(new KeyValuePair<int, string>(currentHeader.Level, headerTitle));
            var subChunks = await SplitSummaryByHeaderAsync(chunkContent, level + 1, subHierarchy, maxTokensPerChunk, cancellationToken);
            result.AddRange(subChunks);
        }

        return result;
    }

    private Stack<KeyValuePair<int, string>> CloneStack(Stack<KeyValuePair<int, string>> source)
        => new Stack<KeyValuePair<int, string>>(source.Reverse());

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

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — chunk splitting (dùng bởi CreateChunkAsync)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<ChunkProcessingContext>> SplitTextByHeaderAsync(string source, int level, Stack<KeyValuePair<int, string>> hierarchy, CancellationToken cancellationToken = default, Guid? workerId = null)
    {
        if (string.IsNullOrWhiteSpace(source)) return new();

        var tokens = (await _tokenCountService.CountAsync(new() { Text = source }, cancellationToken)).TokenCount;
        if (tokens < _documentProcessOption.MaxChunkSize)
        {
            if (IsTrivialContent(source))
                return new List<ChunkProcessingContext>();

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
            _logger.LogWarning("Header level too deep: {level}", level);
            _logger.LogWarning("Tokens: {tokens}", tokens);
            _logger.LogWarning("Source: {source}", source[..50]);
            var chunk = CreateChunk(source, tokens, TypeChunk.Summary, GetHierarchyPath(hierarchy));
            chunk.NeedsSummary = true;
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
        string? pendingHeaders = null;

        for (int i = 0; i < headers.Count; i++)
        {
            var currentHeader = headers[i];
            var nextHeaderStart = (i + 1 < headers.Count) ? headers[i + 1].Span.Start : source.Length;

            var subContent = source.Substring(lastPos, currentHeader.Span.Start - lastPos);
            if (!string.IsNullOrWhiteSpace(subContent))
                result.AddRange(await SplitTextByHeaderAsync(subContent, level + 1, CloneStack(hierarchy), cancellationToken, workerId));

            var headerTitle = source.Substring(currentHeader.Span.Start, currentHeader.Span.Length);
            var headerContent = source.Substring(currentHeader.Span.Start, nextHeaderStart - currentHeader.Span.Start);

            UpdateHierarchyStack(hierarchy, new KeyValuePair<int, string>(level, headerTitle));
            var headerChunks = await SplitTextByHeaderAsync(headerContent, level + 1, CloneStack(hierarchy), cancellationToken, workerId);

            if (headerChunks.Count == 0)
            {
                pendingHeaders = pendingHeaders != null
                    ? pendingHeaders + "\n" + headerTitle
                    : headerTitle;
            }
            else
            {
                if (pendingHeaders != null)
                {
                    var merged = pendingHeaders + "\n" + headerChunks[0].RawContent;
                    headerChunks[0].RawContent = merged;
                    headerChunks[0].Chunk.Content = merged;
                    headerChunks[0].Chunk.TokensCount = (await _tokenCountService.CountAsync(new() { Text = merged }, cancellationToken)).TokenCount;
                    pendingHeaders = null;
                }
                result.AddRange(headerChunks);
            }

            lastPos = nextHeaderStart;
        }

        if (pendingHeaders != null && result.Count > 0)
        {
            var last = result[^1];
            var merged = pendingHeaders + "\n" + last.RawContent;
            last.RawContent = merged;
            last.Chunk.Content = merged;
            last.Chunk.TokensCount = (await _tokenCountService.CountAsync(new() { Text = merged }, cancellationToken)).TokenCount;
        }

        return result;
    }

    private bool IsTrivialContent(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return true;
        var blocks = MarkdownServiceHelper.GetAllBlock(source, _pipeline);
        return !blocks.Any(b => b is not HeadingBlock
                             and not HtmlBlock
                             and not ThematicBreakBlock);
    }

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
