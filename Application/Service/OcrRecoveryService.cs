using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Application.Service;

public class OcrRecoveryService
{
    private readonly IUnitOfWork _uow;
    private readonly IOCRService _ocrService;
    private readonly IS3Service _s3Service;
    private readonly IProcessBroadcaster _broadcaster;
    private readonly ILogger<OcrRecoveryService> _logger;
    private readonly string _defaultOcrModelId;

    public OcrRecoveryService(
        IUnitOfWork uow,
        IOCRService ocrService,
        IS3Service s3Service,
        IProcessBroadcaster broadcaster,
        ILogger<OcrRecoveryService> logger,
        IOptions<ExternalServiceOptions> options)
    {
        _uow = uow;
        _ocrService = ocrService;
        _s3Service = s3Service;
        _broadcaster = broadcaster;
        _logger = logger;
        _defaultOcrModelId = options.Value.OCRService.DefaultModelId;
    }

    public async Task<OcrRecoveryResult> RecoverOcrJobsAsync(CancellationToken cancellationToken)
    {
        var result = new OcrRecoveryResult();

        var isOcrServerAlive = await _ocrService.PingAsync(cancellationToken);
        if (!isOcrServerAlive)
        {
            _logger.LogWarning("OCR server is not reachable. Skipping OCR recovery.");
            return result;
        }

        var processingDocs = (await _uow.Documents.GetByStatusAsync(StatusDocument.ProcessingOcr)).ToList();
        result.ProcessingFound = processingDocs.Count;
        _logger.LogInformation("Found {Count} documents in ProcessingOcr state. Cancelling and resubmitting...", processingDocs.Count);

        foreach (var doc in processingDocs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await CancelAndResubmitAsync(doc, cancellationToken);
                result.ProcessingResubmitted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover OCR for document {Id}", doc.Id);
                result.ProcessingFailed++;
                await MarkFailedAndBroadcastAsync(doc, ex.Message);
            }
        }

        var pendingDocs = (await _uow.Documents.GetByStatusAsync(StatusDocument.Uploaded)).ToList();
        result.PendingFound = pendingDocs.Count;
        _logger.LogInformation("Found {Count} documents in Uploaded state. Submitting OCR...", pendingDocs.Count);

        foreach (var doc in pendingDocs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                if (string.IsNullOrEmpty(doc.ObjectKeyFilePdf))
                {
                    _logger.LogWarning("Document {Id} has no ObjectKeyFilePdf. Skipping.", doc.Id);
                    result.PendingSkipped++;
                    continue;
                }

                await SubmitOcrAsync(doc, cancellationToken);
                result.PendingResubmitted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit OCR for pending document {Id}", doc.Id);
                result.PendingFailed++;
                await MarkFailedAndBroadcastAsync(doc, ex.Message);
            }
        }

        return result;
    }

    private async Task CancelAndResubmitAsync(Document doc, CancellationToken ct)
    {
        var job = await _uow.DocumentJobs.GetByDocumentIdAsync(doc.Id);

        if (job != null && !string.IsNullOrEmpty(job.OcrJobId))
        {
            try
            {
                _logger.LogInformation("Cancelling old OCR job {TaskId} for document {Id}", job.OcrJobId, doc.Id);
                var cancelResult = await _ocrService.CancelJobAsync(job.OcrJobId);
                _logger.LogInformation("Cancel signal sent for {TaskId}: {Message}", job.OcrJobId, cancelResult);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel old OCR job {TaskId}. Will proceed with resubmit anyway.", job.OcrJobId);
            }
        }

        await SubmitOcrAsync(doc, ct);
    }

    private async Task SubmitOcrAsync(Document doc, CancellationToken ct)
    {
        await _broadcaster.ClearHistoryAsync("ocr", doc.Id);

        await _broadcaster.PublishAsync("ocr", new NotificationMessage
        {
            DocumentId = doc.Id,
            Message = "App was restarted. Resubmitting OCR job...",
            Status = "Resuming",
            Stage = "OCR"
        });

        if (string.IsNullOrEmpty(doc.ObjectKeyFilePdf))
        {
            throw new InvalidOperationException($"Document {doc.Id} has no ObjectKeyFilePdf");
        }

        var ocrResponse = await _ocrService.ProcessFromS3Async(
            S3BucketName.OCRUploadPdf, doc.ObjectKeyFilePdf, _defaultOcrModelId);

        var job = await _uow.DocumentJobs.GetByDocumentIdAsync(doc.Id);
        if (job == null)
        {
            job = new DocumentJob
            {
                DocumentId = doc.Id,
                OcrJobId = ocrResponse.TaskId,
                StatusOcr = StatusJob.Pending
            };
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

        doc.Status = StatusDocument.ProcessingOcr;
        _uow.Documents.Update(doc);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Resubmitted OCR for document {Id}, new taskId: {TaskId}", doc.Id, ocrResponse.TaskId);
    }

    private async Task MarkFailedAndBroadcastAsync(Document doc, string errorMessage)
    {
        try
        {
            doc.Status = StatusDocument.Failed;
            _uow.Documents.Update(doc);

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(doc.Id);
            if (job != null)
            {
                job.StatusOcr = StatusJob.Failed;
                _uow.DocumentJobs.Update(job);
            }
            await _uow.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark document {Id} as Failed during OCR recovery", doc.Id);
        }

        await _broadcaster.PublishAsync("ocr", new NotificationMessage
        {
            DocumentId = doc.Id,
            Message = $"OCR recovery failed: {errorMessage}",
            Status = "Failed",
            Stage = "OCR"
        });
    }
}

public class OcrRecoveryResult
{
    public int ProcessingFound { get; set; }
    public int ProcessingResubmitted { get; set; }
    public int ProcessingFailed { get; set; }
    public int PendingFound { get; set; }
    public int PendingResubmitted { get; set; }
    public int PendingSkipped { get; set; }
    public int PendingFailed { get; set; }

    public int TotalResubmitted => ProcessingResubmitted + PendingResubmitted;
    public int TotalFailed => ProcessingFailed + PendingFailed;
}
