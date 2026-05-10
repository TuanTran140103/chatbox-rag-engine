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
                ProcessingTimeGenQa = f.ProcessingTimeGenQa,
                IsOcred = f.IsOcred,
                IsQaGenerated = f.IsQaGenerated,
                OcrCount = f.OcrCount,
                GenQaCount = f.GenQaCount,
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
            var qaTask = !string.IsNullOrEmpty(f.QaContent) ? GetQaContentAsync(id) : null;
            var summaryTask = !string.IsNullOrEmpty(f.SummaryContent) ? GetSummaryContentAsync(id) : null;

            var allTasks = new List<Task>();
            if (ocrTask != null) allTasks.Add(ocrTask);
            if (qaTask != null) allTasks.Add(qaTask);
            if (summaryTask != null) allTasks.Add(summaryTask);

            if (allTasks.Any())
            {
                await Task.WhenAll(allTasks);

                if (ocrTask != null && ocrTask.Status == TaskStatus.RanToCompletion && ocrTask.Result.IsSuccess)
                {
                    detailDto.Content.OcrMarkdown = ocrTask.Result.Data;
                }

                if (qaTask != null && qaTask.Status == TaskStatus.RanToCompletion && qaTask.Result.IsSuccess)
                    detailDto.Content.QAs = qaTask.Result.Data;

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

    public async Task<ServiceResult<DocumentUploadResponseDto>> UploadAsync(DocumentUploadRequestDto dto)
    {
        try
        {
            if (dto.FileStream == null)
                return new ServiceResult<DocumentUploadResponseDto> { IsSuccess = false, ErrorMessage = "File stream is required" };

            var extension = Path.GetExtension(dto.FileName).ToLowerInvariant();
            if (extension != ".pdf") return new ServiceResult<DocumentUploadResponseDto> { IsSuccess = false, ErrorMessage = "Only PDF files are supported" };

            var buffer = new byte[4];
            long currentPosition = 0;

            if (dto.FileStream.CanSeek)
            {
                currentPosition = dto.FileStream.Position;
                await dto.FileStream.ReadExactlyAsync(buffer, 0, 4);
                dto.FileStream.Position = currentPosition;
            }
            else
            {
                await dto.FileStream.ReadExactlyAsync(buffer, 0, 4);
            }

            var signature = Encoding.ASCII.GetString(buffer);
            if (signature != "%PDF")
            {
                _logger.LogWarning("Security Warning: File {FileName} has .pdf extension but invalid signature: {Signature}", dto.FileName, signature);
                return new ServiceResult<DocumentUploadResponseDto> { IsSuccess = false, ErrorMessage = "Invalid PDF file content." };
            }

            var existingFile = (await _uow.Documents.FindAsync(f => f.FileName == dto.FileName)).FirstOrDefault();
            if (existingFile != null)
            {
                return new ServiceResult<DocumentUploadResponseDto> { IsSuccess = false, ErrorMessage = $"A file with name '{dto.FileName}' already exists." };
            }

            var ocrFile = new Document
            {
                FileName = dto.FileName,
                ObjectKeyFilePdf = "",
                DatasetItemId = dto.CategoryId,
                UserId = null,
                Status = StatusDocument.Uploaded,
                ProcessingTimeOcr = 0
            };

            await _uow.Documents.AddAsync(ocrFile);
            await _uow.SaveChangesAsync();

            using var memoryStream = new MemoryStream();
            dto.FileStream.Position = 0;
            await dto.FileStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var s3Stream = new MemoryStream();
            using var cacheStream = new MemoryStream();
            await memoryStream.CopyToAsync(s3Stream);
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(cacheStream);

            s3Stream.Position = 0;
            cacheStream.Position = 0;

            var s3Task = _s3Service.UploadFileAsync(s3Stream, dto.FileName, S3BucketName.OCRUploadPdf, dto.ContentType ?? "application/pdf");
            var cacheTask = DocumentHelper.SaveToCacheAsync(ocrFile.Id, DocumentHelper.BucketUploads, ".pdf", cacheStream);

            await Task.WhenAll(s3Task, cacheTask);

            ocrFile.ObjectKeyFilePdf = await s3Task;
            _uow.Documents.Update(ocrFile);
            await _uow.SaveChangesAsync();

            return new ServiceResult<DocumentUploadResponseDto> { IsSuccess = true, Data = MapToUploadResponseDto(ocrFile) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File upload error");
            return new ServiceResult<DocumentUploadResponseDto> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
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

            Stream? fileStream = null;
            if (DocumentHelper.Exists(document.Id, DocumentHelper.BucketUploads, ".pdf"))
            {
                fileStream = DocumentHelper.GetContent(document.Id, DocumentHelper.BucketUploads, ".pdf");
                _logger.LogInformation("Using cached PDF for document {Id}", documentId);
            }
            else
            {
                fileStream = await _s3Service.DownloadFileAsync(document.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);
                _logger.LogInformation("Downloaded PDF from S3 for document {Id}", documentId);
            }

            if (fileStream == null)
                return new ServiceResult<OcrProcessResponse> { IsSuccess = false, ErrorMessage = "Could not retrieve file" };

            using (fileStream)
            {
                var ocrResponse = await _ocrService.ProcessAsync(fileStream, document.FileName, "application/pdf", effectiveModelId);

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
                    job.StatusGenQa = StatusJob.None;
                    job.GenQaJobId = null;
                    _uow.DocumentJobs.Update(job);
                }
                await _uow.SaveChangesAsync();

                document.Status = StatusDocument.ProcessingOcr;
                _uow.Documents.Update(document);
                await _uow.SaveChangesAsync();

                // Clear stale SSE messages from previous runs
                await _broadcaster.ClearHistoryAsync("ocr", document.Id);

                return new ServiceResult<OcrProcessResponse> { IsSuccess = true, Data = ocrResponse };
            }
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

    public async Task<ServiceResult<Guid>> ProcessGenQAs(Guid documentId)
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
                    ErrorMessage = $"OCR must be completed before GenQA. Current status: {document.Status}"
                };

            if (string.IsNullOrEmpty(document.OcrContent))
                return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = "OCR content not found. Cannot start GenQA until OCR is completed." };

            if (document.Status == StatusDocument.ProcessingGenQa)
                return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = "GenQA is already processing" };

            var jobId = BackgroundJob.Enqueue<IGenQaBackgroundJobService>(x => x.ProcessGenChunkQA(documentId, CancellationToken.None, null));

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (job == null)
            {
                job = new DocumentJob { DocumentId = document.Id, GenQaJobId = jobId, StatusGenQa = StatusJob.Pending };
                await _uow.DocumentJobs.AddAsync(job);
            }
            else
            {
                job.GenQaJobId = jobId;
                job.StatusGenQa = StatusJob.Pending;
                _uow.DocumentJobs.Update(job);
            }

            document.Status = StatusDocument.ProcessingGenQa;
            document.GenQaStartedAt = DateTime.UtcNow;
            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

            return new ServiceResult<Guid> { IsSuccess = true, Data = document.Id };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenQA processing error for document {Id}", documentId);
            return new ServiceResult<Guid> { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ServiceResult<string>> CancelGenQA(Guid documentId)
    {
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "Document not found" };

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (job == null || string.IsNullOrEmpty(job.GenQaJobId))
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = "GenQA job not found or already completed" };

            if (document.Status != StatusDocument.ProcessingGenQa)
                return new ServiceResult<string> { IsSuccess = false, ErrorMessage = $"GenQA job is not running. Current status: {document.Status}" };

            BackgroundJob.Delete(job.GenQaJobId);

            document.Status = StatusDocument.Canceled;
            _uow.Documents.Update(document);
            job.StatusGenQa = StatusJob.Canceled;
            _uow.DocumentJobs.Update(job);
            await _uow.SaveChangesAsync();

            _logger.LogInformation("GenQA job {JobId} deleted for Document {DocumentId}", job.GenQaJobId, documentId);
            return new ServiceResult<string>
            {
                IsSuccess = true,
                Data = $"GenQA job {job.GenQaJobId} has been canceled."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling GenQA for document {Id}", documentId);
            return new ServiceResult<string> { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ServiceResult<int>> RecoverStuckGenQAJobsAsync()
    {
        try
        {
            var stuckDocuments = await _uow.Documents.GetByStatusAsync(StatusDocument.ProcessingGenQa);
            var stuckList = stuckDocuments.ToList();

            if (stuckList.Count == 0)
            {
                _logger.LogInformation("No stuck GenQA jobs found.");
                return new ServiceResult<int> { IsSuccess = true, Data = 0 };
            }

            _logger.LogInformation("Found {Count} documents stuck in ProcessingGenQa. Attempting recovery...", stuckList.Count);
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
                            existingJob.StatusGenQa = StatusJob.Failed;
                        }
                        continue;
                    }

                    var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
                    if (documentJob != null && !string.IsNullOrEmpty(documentJob.GenQaJobId))
                    {
                        BackgroundJob.Delete(documentJob.GenQaJobId);
                    }

                    var newJobId = BackgroundJob.Enqueue<IGenQaBackgroundJobService>(
                        "critical",
                        x => x.ProcessGenChunkQA(document.Id, CancellationToken.None, null));

                    if (documentJob == null)
                    {
                        documentJob = new DocumentJob
                        {
                            DocumentId = document.Id,
                            GenQaJobId = newJobId,
                            StatusGenQa = StatusJob.Pending
                        };
                        await _uow.DocumentJobs.AddAsync(documentJob);
                    }
                    else
                    {
                        documentJob.GenQaJobId = newJobId;
                        documentJob.StatusGenQa = StatusJob.Pending;
                    }

                    document.GenQaStartedAt = DateTime.UtcNow;

                    _logger.LogInformation("Recovered GenQA job for document {Id}: new job {JobId}", document.Id, newJobId);
                    recovered++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recover GenQA job for document {Id}", doc.Id);
                }
            }

            await _uow.SaveChangesAsync();
            _logger.LogInformation("GenQA recovery completed. Recovered {Count} jobs.", recovered);

            return new ServiceResult<int> { IsSuccess = true, Data = recovered };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenQA recovery error");
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
                    Stream? originalStream = DocumentHelper.GetContent(f.Id, DocumentHelper.BucketUploads, ".pdf")
                        ?? await _s3Service.DownloadFileAsync(f.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);

                    if (originalStream == null) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Original file not found in storage" };
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (originalStream, "application/pdf", f.FileName) };

                case "qa-markdown":
                    if (string.IsNullOrEmpty(f.QaContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "QAs not yet generated" };

                    var qaMdResult = await GetQaContentAsync(id);
                    if (!qaMdResult.IsSuccess) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = qaMdResult.ErrorMessage };

                    var qaMd = RenderQaToMarkdown(f.FileName, qaMdResult.Data);
                    var ms = new MemoryStream(Encoding.UTF8.GetBytes(qaMd));
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (ms, "text/markdown", $"{Path.GetFileNameWithoutExtension(f.FileName)}_QAs.md") };

                case "ocr-markdown":
                    if (string.IsNullOrEmpty(f.OcrContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "OCR result not found" };
                    Stream mdStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.OcrContent));
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (mdStream, "text/markdown", $"{Path.GetFileNameWithoutExtension(f.FileName)}.md") };

                case "all":
                    return await DownloadAllAsync(f);

                default:
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Invalid scope. Allowed values: original, ocr-markdown, qa-markdown, all" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download data for file {Id}, scope {Scope}", id, scope);
            return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
    }

    private async Task<ServiceResult<(Stream Stream, string ContentType, string FileName)>> DownloadAllAsync(Document f)
    {
        var baseName = Path.GetFileNameWithoutExtension(f.FileName);
        var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
        {
            if (!string.IsNullOrEmpty(f.ObjectKeyFilePdf))
            {
                var originalStream = DocumentHelper.GetContent(f.Id, DocumentHelper.BucketUploads, ".pdf")
                    ?? await _s3Service.DownloadFileAsync(f.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);

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

            if (!string.IsNullOrEmpty(f.QaContent))
            {
                var qaResult = await GetQaContentAsync(f.Id);
                if (qaResult.IsSuccess && qaResult.Data != null)
                {
                    var qaMd = RenderQaToMarkdown(f.FileName, qaResult.Data);
                    var entry = archive.CreateEntry($"{baseName}_QAs.md", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(Encoding.UTF8.GetBytes(qaMd));
                }
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

    private static string RenderQaToMarkdown(string fileName, List<ChunkQAInfor>? chunkQAs)
    {
        if (chunkQAs == null) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"# QAs for {fileName}");
        sb.AppendLine();

        int qCount = 1;
        foreach (var chunk in chunkQAs)
        {
            if (chunk?.QAs != null)
            {
                foreach (var qa in chunk.QAs)
                {
                    if (qa == null) continue;

                    sb.AppendLine($"### Question [{qCount}]: {qa.Question}");
                    sb.AppendLine($"**Answer**: {qa.Answer}");
                    sb.AppendLine();
                    qCount++;
                }
            }

            // Table QAs are now included in the flat QAs list above
        }

        return sb.ToString();
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

    public async Task<ServiceResult<List<ChunkQAInfor>>> GetQaContentAsync(Guid id)
    {
        try
        {
            var f = await _uow.Documents.GetByIdAsync(id);
            if (f == null || string.IsNullOrEmpty(f.QaContent)) return new ServiceResult<List<ChunkQAInfor>> { IsSuccess = false, ErrorMessage = "QA content not found" };

            var data = JsonSerializer.Deserialize<List<ChunkQAInfor>>(f.QaContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new ServiceResult<List<ChunkQAInfor>> { IsSuccess = true, Data = data ?? new() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting QA content {Id}", id);
            return new ServiceResult<List<ChunkQAInfor>> { IsSuccess = false, ErrorMessage = "Internal server error" };
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

        if (string.IsNullOrEmpty(metadata))
        {
            _logger.LogError("Failed to extract metadata for document {DocumentId}: empty response", documentId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Failed to extract metadata: empty response" };
        }

        document.MetadataContent = metadata;
        await _context.SaveChangesAsync(ct);

        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult> UpdateMetadataAsync(Guid documentId, string metadataContent, bool isExtracted)
    {
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
                return new ServiceResult { IsSuccess = false, ErrorMessage = "Document not found" };

            document.MetadataContent = metadataContent;
            if (isExtracted)
                document.IsMetadataExtracted = true;

            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

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

    private DocumentUploadResponseDto MapToUploadResponseDto(Document f)
    {
        return new DocumentUploadResponseDto
        {
            Id = f.Id,
            FileName = f.FileName,
            Status = f.Status.ToString(),
            CategoryId = f.DatasetItemId,
            CreatedAt = f.CreatedAt
        };
    }
}
