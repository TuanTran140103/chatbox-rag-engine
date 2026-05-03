using System.Text;
using System.Text.Json;
using Hangfire;
using MarkdownGenQAs.Application.Dto.Documents;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure.Exceptions;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.QA;
using MarkdownGenQAs.Options;
using MarkdownGenQAs.Helper;

namespace MarkdownGenQAs.Application.Service;

public class DocumentService
{
    private readonly IUnitOfWork _uow;
    private readonly IOCRService _ocrService;
    private readonly IS3Service _s3Service;
    private readonly ILogger<DocumentService> _logger;
    private readonly string _defaultOcrModelId;

    public DocumentService(
        IUnitOfWork uow,
        IOCRService ocrService,
        IS3Service s3Service,
        ILogger<DocumentService> logger,
        Microsoft.Extensions.Options.IOptions<ExternalServiceOptions> options)
    {
        _uow = uow;
        _ocrService = ocrService;
        _s3Service = s3Service;
        _logger = logger;
        _defaultOcrModelId = options.Value.OCRService.DefaultModelId;
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
                Content = new DocumentContent()
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
                    job = new DocumentJob { DocumentId = document.Id, OcrJobId = ocrResponse.TaskId };
                    await _uow.DocumentJobs.AddAsync(job);
                }
                else
                {
                    job.OcrJobId = ocrResponse.TaskId;
                    _uow.DocumentJobs.Update(job);
                }
                await _uow.SaveChangesAsync();

                document.Status = StatusDocument.ProcessingOcr;
                _uow.Documents.Update(document);
                await _uow.SaveChangesAsync();

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
                job = new DocumentJob { DocumentId = document.Id, GenQaJobId = jobId };
                await _uow.DocumentJobs.AddAsync(job);
            }
            else
            {
                job.GenQaJobId = jobId;
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
                            StatusGenQa = StatusJob.Pendding
                        };
                        await _uow.DocumentJobs.AddAsync(documentJob);
                    }
                    else
                    {
                        documentJob.GenQaJobId = newJobId;
                        documentJob.StatusGenQa = StatusJob.Pendding;
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

                case "qa-json":
                    if (string.IsNullOrEmpty(f.QaContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "QAs not yet generated" };
                    var qaBytes = System.Text.Encoding.UTF8.GetBytes(f.QaContent);
                    Stream jsonStream = new MemoryStream(qaBytes);
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (jsonStream, "application/json", $"{Path.GetFileNameWithoutExtension(f.FileName)}_QAs.json") };

                case "qa-markdown":
                    if (string.IsNullOrEmpty(f.QaContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "QAs not yet generated" };

                    var qaMdResult = await GetQaContentAsync(id);
                    if (!qaMdResult.IsSuccess) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = qaMdResult.ErrorMessage };

                    var chunkQAs = qaMdResult.Data ?? new List<ChunkQAInfor>();
                    var sb = new StringBuilder();
                    sb.AppendLine($"# QAs for {f.FileName}");
                    sb.AppendLine();

                    int qCount = 1;
                    foreach (var chunk in chunkQAs)
                    {
                        if (chunk?.QAs == null) continue;

                        foreach (var qa in chunk.QAs)
                        {
                            if (qa == null) continue;

                            sb.AppendLine($"### Question [{qCount}]: {qa.Question}");
                            sb.AppendLine($"**Answer**: {qa.Answer}");
                            sb.AppendLine();
                            qCount++;
                        }
                    }

                    var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (ms, "text/markdown", $"{Path.GetFileNameWithoutExtension(f.FileName)}_QAs.md") };

                case "markdown":
                    if (string.IsNullOrEmpty(f.OcrContent)) return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "OCR markdown not found" };
                    Stream mdStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.OcrContent));
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = true, Data = (mdStream, "text/markdown", $"{Path.GetFileNameWithoutExtension(f.FileName)}_OCR.md") };

                default:
                    return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Invalid scope. Allowed values: original, markdown, qa-markdown, qa-json" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download data for file {Id}, scope {Scope}", id, scope);
            return new ServiceResult<(Stream, string, string)> { IsSuccess = false, ErrorMessage = "Internal server error" };
        }
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

    private DocumentListDto MapToListDto(Document f)
    {
        return new DocumentListDto
        {
            Id = f.Id,
            DocumentName = f.FileName,
            StatusOcr = f.IsOcred,
            GenQa = f.IsQaGenerated,
            StatusDocument = f.Status.ToString(),
            CategoryName = f.DatasetItem?.Name,
            OcrCount = f.OcrCount,
            GenQaCount = f.GenQaCount,
            CreatedAt = f.CreatedAt,
            UpdatedAt = f.UpdatedAt
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
