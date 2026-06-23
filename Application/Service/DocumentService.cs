using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GenQAServer.Options;
using Hangfire;
using MarkdownGenQAs.Application.Dto.Documents;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Infrastructure.Exceptions;
using MarkdownGenQAs.Infrastructure.Services;
using MarkdownGenQAs.Utils;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.QA;
using MarkdownGenQAs.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Application.Service;

public class DocumentService
{
    private readonly IUnitOfWork _uow;
    private readonly IOCRService _ocrService;
    private readonly IS3Service _s3Service;
    private readonly ILogger<DocumentService> _logger;
    private readonly IProcessBroadcaster _broadcaster;
    private readonly string _defaultOcrModelId;
    private readonly ApplicationContext _context;
    private readonly LlmService _llmService;
    private readonly SystemPrompts _systemPrompts;
    private readonly DocumentProcessOption _documentProcessOptions;
    private readonly string _baseDir;

    public DocumentService(
        IUnitOfWork uow,
        IOCRService ocrService,
        IS3Service s3Service,
        IProcessBroadcaster broadcaster,
        ILogger<DocumentService> logger,
        IOptions<ExternalServiceOptions> options,
        ApplicationContext context,
        LlmService llmService,
        IOptions<SystemPrompts> systemPrompts,
        IOptions<DocumentProcessOption> documentProcessOption)
    {
        _uow = uow;
        _ocrService = ocrService;
        _s3Service = s3Service;
        _broadcaster = broadcaster;
        _logger = logger;
        _defaultOcrModelId = options.Value.OCRService.DefaultModelId;
        _context = context;
        _llmService = llmService;
        _systemPrompts = systemPrompts.Value;
        _documentProcessOptions = documentProcessOption.Value;
        _baseDir = AppContext.BaseDirectory;
    }

    public async Task<ServiceResult<DocumentDetailDto>> GetDetailAsync(Guid id)
    {
        try
        {
            var f = await _uow.Documents.GetByIdAsync(id);
            if (f == null) return new ServiceResult<DocumentDetailDto> { IsSuccess = false, ErrorMessage = "File not found" };

            var detailDto = new DocumentDetailDto
            {
                Id = f.Id,
                FileName = f.FileName,
                Status = f.Status.ToString(),
                ProcessingTimeOcr = f.ProcessingTimeOcr,
                IsOcred = f.IsOcred,
                IsIndexed = f.IsIndexed,
                OcrCount = f.OcrCount,
                UserId = f.UserId,
                CategoryId = f.DatasetItemId,
                CategoryName = f.DatasetItem?.Name,
                CreatedAt = f.CreatedAt,
                Content = new DocumentContent(),
                Metadata = new DocumentMetadata
                {
                    IsMetadataExtracted = f.IsMetadataExtracted,
                    MetadataContent = f.MetadataContent,
                    MetadataError = f.MetadataError
                }
            };

            var ocrTask = !string.IsNullOrEmpty(f.OcrContent) ? GetOcrContentAsync(id) : null;
            var summaryTask = !string.IsNullOrEmpty(f.SummaryContent) ? GetSummaryContentAsync(id) : null;

            var allTasks = new List<Task>();
            if (ocrTask != null) allTasks.Add(ocrTask);
            if (summaryTask != null) allTasks.Add(summaryTask);

            if (allTasks.Any())
            {
                await Task.WhenAll(allTasks);

                if (ocrTask != null && ocrTask.Status == TaskStatus.RanToCompletion && ocrTask.Result.IsSuccess)
                {
                    detailDto.Content.OcrMarkdown = ocrTask.Result.Data;
                }

                if (summaryTask != null && summaryTask.Status == TaskStatus.RanToCompletion && summaryTask.Result.IsSuccess)
                    detailDto.Content.Summary = summaryTask.Result.Data;
            }

            return new ServiceResult<DocumentDetailDto> { IsSuccess = true, Data = detailDto };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting document detail {Id}", id);
            return new ServiceResult<DocumentDetailDto> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    public async Task<List<string>> GetSupportedModelsAsync()
    {
        return await _ocrService.GetSupportedModelsAsync();
    }

    public async Task<ServiceResult<OcrProcessResponse>> ProcessOCR(Guid documentId, string? modelId = null)
    {
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
                return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = "File record not found" };

            if (string.IsNullOrEmpty(document.ObjectKeyFilePdf))
                return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = "File PDF not found in storage" };

            if (document.Status == StatusDocument.ProcessingOcr)
                return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = "File is already processing" };

            var isOcrServerAlive = await _ocrService.PingAsync();
            if (!isOcrServerAlive)
            {
                return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = "OCR server timeout after 3 seconds" };
            }

            var effectiveModelId = !string.IsNullOrEmpty(modelId) ? modelId : _defaultOcrModelId;

            await _broadcaster.ClearHistoryAsync("ocr", document.Id);

            _logger.LogInformation("Sending OCR request with S3 key for document {Id}, bucket={Bucket}, key={Key}",
                documentId, S3BucketName.OCRUploadPdf, document.ObjectKeyFilePdf);

            var ocrResponse = await _ocrService.ProcessFromS3Async(
                S3BucketName.OCRUploadPdf, document.ObjectKeyFilePdf, effectiveModelId);

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
            if (job == null)
            {
                job = new DocumentJob { DocumentId = document.Id, OcrJobId = ocrResponse.TaskId, StatusOcr = StatusJob.Pending };
                await _uow.DocumentJobs.AddAsync(job);
            }
            else
            {
                job.OcrJobId = ocrResponse.TaskId;
                job.StatusOcr = StatusJob.Pending;
                job.StatusIndexing = StatusJob.None;
                job.IndexingJobId = null;
                _uow.DocumentJobs.Update(job);
            }
            await _uow.SaveChangesAsync();

            document.Status = StatusDocument.ProcessingOcr;
            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

            return new ServiceResult<OcrProcessResponse> { IsSuccess = true, Data = ocrResponse };
        }
        catch (OcrApiException ex)
        {
            _logger.LogError(ex, "OCR API error for document {Id}: StatusCode={StatusCode}, Error={ErrorBody}",
                documentId, ex.StatusCode, ex.ErrorBody);

            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document != null)
            {
                document.Status = StatusDocument.Failed;
                _uow.Documents.Update(document);
                await _uow.SaveChangesAsync();
            }

            var errorMessage = ex.StatusCode switch
            {
                400 => $"Invalid request: {ex.ErrorBody}",
                409 => $"Conflict: {ex.ErrorBody}",
                500 => $"OCR service error: {ex.ErrorBody}",
                503 => $"OCR service unavailable: {ex.ErrorBody}",
                _ => $"OCR API error ({ex.StatusCode}): {ex.ErrorBody}"
            };

            return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = errorMessage };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR processing error for document {Id}", documentId);
            return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = $"Internal server error: {ex.Message}" };
        }
    }

    public async Task<ServiceResult<string>> CancelOCR(Guid documentId)
    {
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Document not found" };

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (job == null || string.IsNullOrEmpty(job.OcrJobId))
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "OCR job not found or already completed" };

            if (document.Status != StatusDocument.ProcessingOcr)
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = $"OCR job is not running. Current status: {document.Status}" };

            var cancelMessage = await _ocrService.CancelJobAsync(job.OcrJobId);

            if (!string.IsNullOrEmpty(cancelMessage))
            {
                _logger.LogInformation("Cancel signal sent for OCR Task {TaskId} (Document {DocumentId}): {Message}", job.OcrJobId, documentId, cancelMessage);

                document.Status = StatusDocument.Canceled;
                _uow.Documents.Update(document);
                job.StatusOcr = StatusJob.Canceled;
                _uow.DocumentJobs.Update(job);
                await _uow.SaveChangesAsync();

                return new ServiceResult<string>
                {
                    IsSuccess = true,
                    Data = cancelMessage
                };
            }
            else
            {
                _logger.LogWarning("Failed to cancel OCR Task {TaskId} (Document {DocumentId})", job.OcrJobId, documentId);
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Failed to send cancel signal to OCR service" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling OCR for document {Id}", documentId);
            return new ServiceResult<string> { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ServiceResult<Guid>> ProcessIndexing(Guid documentId)
    {
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
                return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = "File record not found" };

            if (!document.IsOcred)
                return new ServiceResult<Guid>
                {
                    IsSuccess = false,
                    ErrorMessage = $"OCR must be completed before indexing. Current status: {document.Status}"
                };

            if (string.IsNullOrEmpty(document.OcrContent))
                return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = "OCR content not found. Cannot start indexing until OCR is completed." };

            if (document.Status == StatusDocument.ProcessingIndexing)
                return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = "Indexing is already processing" };

            var jobId = BackgroundJob.Enqueue<IDocumentIndexingBackgroundJobService>(x => x.ProcessIndexing(documentId, CancellationToken.None, null));

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (job == null)
            {
                job = new DocumentJob { DocumentId = document.Id, IndexingJobId = jobId, StatusIndexing = StatusJob.Pending };
                await _uow.DocumentJobs.AddAsync(job);
            }
            else
            {
                job.IndexingJobId = jobId;
                job.StatusIndexing = StatusJob.Pending;
                _uow.DocumentJobs.Update(job);
            }

            document.Status = StatusDocument.ProcessingIndexing;
            document.IndexingStartedAt = DateTime.UtcNow;
            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

            return new ServiceResult<Guid> { IsSuccess = true, Data = document.Id };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing processing error for document {Id}", documentId);
            return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ServiceResult<string>> CancelIndexing(Guid documentId)
    {
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Document not found" };

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (job == null || string.IsNullOrEmpty(job.IndexingJobId))
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Indexing job not found or already completed" };

            if (document.Status != StatusDocument.ProcessingIndexing)
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = $"Indexing job is not running. Current status: {document.Status}" };

            BackgroundJob.Delete(job.IndexingJobId);

            document.Status = StatusDocument.Canceled;
            _uow.Documents.Update(document);
            job.StatusIndexing = StatusJob.Canceled;
            _uow.DocumentJobs.Update(job);
            await _uow.SaveChangesAsync();

            _logger.LogInformation("Indexing job {JobId} deleted for Document {DocumentId}", job.IndexingJobId, documentId);
            return new ServiceResult<string>
            {
                IsSuccess = true,
                Data = $"Indexing job {job.IndexingJobId} has been canceled."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling indexing for document {Id}", documentId);
            return new ServiceResult<string> { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ServiceResult<int>> RecoverStuckIndexingJobsAsync()
    {
        try
        {
            var stuckDocuments = await _uow.Documents.GetByStatusAsync(StatusDocument.ProcessingIndexing);
            var stuckList = stuckDocuments.ToList();

            if (stuckList.Count == 0)
            {
                _logger.LogInformation("No stuck indexing jobs found.");
                return new ServiceResult<int> { IsSuccess = true, Data = 0 };
            }

            _logger.LogInformation("Found {Count} documents stuck in ProcessingIndexing. Attempting recovery...", stuckList.Count);
            var recovered = 0;

            foreach (var doc in stuckList)
            {
                try
                {
                    var document = await _uow.Documents.GetByIdAsync(doc.Id);
                    if (document == null) continue;

                    if (string.IsNullOrEmpty(document.OcrContent))
                    {
                        _logger.LogWarning("Document {Id} has no OCR content. Marking as Failed.", document.Id);
                        document.Status = StatusDocument.Failed;
                        var existingJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
                        if (existingJob != null)
                        {
                            existingJob.StatusIndexing = StatusJob.Failed;
                        }
                        continue;
                    }

                    var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
                    if (documentJob != null && !string.IsNullOrEmpty(documentJob.IndexingJobId))
                    {
                        BackgroundJob.Delete(documentJob.IndexingJobId);
                    }

                    var newJobId = BackgroundJob.Enqueue<IDocumentIndexingBackgroundJobService>(
                        "critical",
                        x => x.ProcessIndexing(document.Id, CancellationToken.None, null));

                    if (documentJob == null)
                    {
                        documentJob = new DocumentJob
                        {
                            DocumentId = document.Id,
                            IndexingJobId = newJobId,
                            StatusIndexing = StatusJob.Pending
                        };
                        await _uow.DocumentJobs.AddAsync(documentJob);
                    }
                    else
                    {
                        documentJob.IndexingJobId = newJobId;
                        documentJob.StatusIndexing = StatusJob.Pending;
                    }

                    document.IndexingStartedAt = DateTime.UtcNow;

                    _logger.LogInformation("Recovered indexing job for document {Id}: new job {JobId}", document.Id, newJobId);
                    recovered++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recover indexing job for document {Id}", doc.Id);
                }
            }

            await _uow.SaveChangesAsync();
            _logger.LogInformation("Indexing recovery completed. Recovered {Count} jobs.", recovered);

            return new ServiceResult<int> { IsSuccess = true, Data = recovered };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing recovery error");
            return new ServiceResult<int> { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ServiceResult<(Stream Stream, string ContentType, string FileName)>> GetDownloadDataAsync(Guid id, string scope)
    {
        try
        {
            var f = await _uow.Documents.GetByIdAsync(id);
            if (f == null) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "File not found" };

            switch (scope.ToLowerInvariant())
            {
                case "original":
                    if (string.IsNullOrEmpty(f.ObjectKeyFilePdf)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Original file not found" };
                    var originalStream = await _s3Service.DownloadFileAsync(f.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);

                    if (originalStream == null) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Original file not found in storage" };
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (originalStream, "application/pdf", f.FileName) };

                case "chunks-markdown":
                    if (string.IsNullOrEmpty(f.ChunkContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Chunks not yet generated" };

                    var chunksContent = f.ChunkContent;
                    var ms = new MemoryStream(Encoding.UTF8.GetBytes(chunksContent));
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (ms, "application/json", $"{Path.GetFileNameWithoutExtension(f.FileName)}_Chunks.json") };

                case "ocr-markdown":
                    if (string.IsNullOrEmpty(f.OcrContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "OCR result not found" };
                    Stream mdStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.OcrContent));
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (mdStream, "text/markdown", $"{Path.GetFileNameWithoutExtension(f.FileName)}.md") };

                case "all":
                    return await DownloadAllAsync(f);

                default:
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Invalid scope. Allowed values: original, ocr-markdown, chunks-markdown, all" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download data for file {Id}, scope {Scope}", id, scope);
            return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    public async Task<string?> GetPresignedDownloadUrlAsync(Guid id)
    {
        var f = await _uow.Documents.GetByIdAsync(id);
        if (f == null || string.IsNullOrEmpty(f.ObjectKeyFilePdf))
            return null;

        return await _s3Service.GeneratePresignedDownloadUrlAsync(
            f.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf, TimeSpan.FromMinutes(15));
    }

    private async Task<ServiceResult<(Stream Stream, string ContentType, string FileName)>> DownloadAllAsync(Document f)
    {
        var baseName = Path.GetFileNameWithoutExtension(f.FileName);
        var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
        {
            if (!string.IsNullOrEmpty(f.ObjectKeyFilePdf))
            {
                var originalStream = await _s3Service.DownloadFileAsync(f.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);

                if (originalStream != null)
                {
                    var entry = archive.CreateEntry(Path.GetFileName(f.FileName), CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await originalStream.CopyToAsync(entryStream);
                    await originalStream.DisposeAsync();
                }
            }

            if (!string.IsNullOrEmpty(f.OcrContent))
            {
                    var entry = archive.CreateEntry($"{baseName}.md", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(Encoding.UTF8.GetBytes(f.OcrContent));
            }

            if (!string.IsNullOrEmpty(f.ChunkContent))
            {
                var entry = archive.CreateEntry($"{baseName}_Chunks.json", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(Encoding.UTF8.GetBytes(f.ChunkContent));
            }

            if (!string.IsNullOrEmpty(f.SummaryContent))
            {
                var entry = archive.CreateEntry($"{baseName}_Summary.md", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(Encoding.UTF8.GetBytes(f.SummaryContent));
            }
        }

        zipStream.Position = 0;
        return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (zipStream, "application/zip", $"{baseName}_All.zip") };
    }

    public async Task<ServiceResult<string>> GetOcrContentAsync(Guid id)
    {
        try
        {
            var f = await _uow.Documents.GetByIdAsync(id);
            if (f == null || string.IsNullOrEmpty(f.OcrContent)) return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "OCR content not found" };

            return new ServiceResult<string> { IsSuccess = true, Data = f.OcrContent };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting OCR content {Id}", id);
            return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    public async Task<ServiceResult<string>> GetChunkContentAsync(Guid id)
    {
        try
        {
            var f = await _uow.Documents.GetByIdAsync(id);
            if (f == null || string.IsNullOrEmpty(f.ChunkContent)) return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Chunk content not found" };

            return new ServiceResult<string> { IsSuccess = true, Data = f.ChunkContent };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting chunk content {Id}", id);
            return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    public async Task<ServiceResult<string>> GetSummaryContentAsync(Guid id)
    {
        try
        {
            var f = await _uow.Documents.GetByIdAsync(id);
            if (f == null || string.IsNullOrEmpty(f.SummaryContent)) return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Summary content not found" };

            return new ServiceResult<string> { IsSuccess = true, Data = f.SummaryContent };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Summary content {Id}", id);
            return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    public async Task<ServiceResult> ExtractMetadataAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await _context.Documents
#pragma warning disable CS8602
            .Include(d => d.DatasetItem)
                .ThenInclude(di => di.Dataset)
#pragma warning restore CS8602
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (document == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Document not found" };

        var dataset = document.DatasetItem?.Dataset;
        if (dataset?.TemplateMetadataId == null)
        {
            _logger.LogInformation("Document {DocumentId} has no template assigned, skipping metadata extraction", documentId);
            return new ServiceResult { IsSuccess = true };
        }

        var template = await _context.TemplateMetadatas.FindAsync([dataset.TemplateMetadataId.Value], ct);
        if (template == null)
        {
            _logger.LogWarning("TemplateMetadata {TemplateId} not found for document {DocumentId}", dataset.TemplateMetadataId, documentId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Template metadata not found" };
        }

        if (string.IsNullOrEmpty(document.OcrContent))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "OCR content not found" };

        var pages = MarkdownServiceHelper.SplitIntoPages(document.OcrContent);
        var pageCount = Math.Min(pages.Count, _documentProcessOptions.MaxExtractionPages);
        var content = string.Join("\n\n---\n\n", pages.Take(pageCount));

        _logger.LogInformation("Extracting metadata for document {DocumentId}: {PageCount} pages",
            documentId, pageCount);

        var messages = BuildMetadataMessages(
            template.JsonSchema,
            content,
            document.FileName);

        var metadata = await _llmService.ChatMetadataExtractionAsync(messages, template.JsonSchema, ct);

        document.MetadataContent = metadata;

        var defaultJson = MetadataSchemaHelper.GenerateDefaultJson(template.JsonSchema);
        if (metadata == defaultJson)
        {
            document.IsMetadataExtracted = false;
            document.MetadataError = "Trích xuất metadata thất bại sau nhiều lần thử, đã dùng giá trị mặc định.";
            _logger.LogWarning("ExtractMetadataAsync for document {DocumentId}: using default values (extraction failed)", documentId);
        }
        else
        {
            document.IsMetadataExtracted = true;
        }

        await _context.SaveChangesAsync(ct);
        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult> UpdateMetadataAsync(Guid documentId, string metadataContent, bool isExtracted)
    {
        try
        {
            var document = await _context.Documents
                .Include(d => d.DatasetItem!)
                    .ThenInclude(di => di.Dataset!)
                        .ThenInclude(ds => ds.TemplateMetadata)
                .FirstOrDefaultAsync(d => d.Id == documentId);

            if (document == null)
                return new ServiceResult { IsSuccess = false, ErrorMessage = "Document not found" };

            var schema = document.DatasetItem?.Dataset?.TemplateMetadata?.JsonSchema;
            if (schema != null)
            {
                var (isValid, errorMessage) = MetadataSchemaHelper.ValidateJsonAgainstSchema(metadataContent, schema);
                if (!isValid)
                {
                    _logger.LogWarning("UpdateMetadataAsync: metadata validation failed for document {DocumentId}: {Error}", documentId, errorMessage);
                    return new ServiceResult { IsSuccess = false, ErrorMessage = $"Metadata không hợp lệ: {errorMessage}" };
                }
            }

            document.MetadataContent = metadataContent;
            if (isExtracted)
                document.IsMetadataExtracted = true;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Metadata for document {DocumentId} updated by user (isExtracted: {IsExtracted})", documentId, isExtracted);
            return new ServiceResult { IsSuccess = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for document {Id}", documentId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    private List<ChatMessage> BuildMetadataMessages(
        string jsonSchema,
        string content,
        string fileName)
    {
        string path = Path.Combine(_baseDir, _systemPrompts.MetadataExtraction.PathTemplatePrompt);
        string templatePrompt = File.ReadAllText(path);

        string prompt = string.Format(templatePrompt, fileName, content, "None", jsonSchema);

        return new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, _systemPrompts.MetadataExtraction.SystemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };
    }

}
