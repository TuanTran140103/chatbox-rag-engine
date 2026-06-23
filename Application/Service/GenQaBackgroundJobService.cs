using System.Diagnostics;
using Hangfire;
using Hangfire.Server;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Service;

public class GenQaBackgroundJobService : IGenQaBackgroundJobService
{
    private readonly ILogger<GenQaBackgroundJobService> _logger;
    private readonly IUnitOfWork _uow;
    private readonly IProcessBroadcaster _broadcaster;
    private readonly ICacheService _cacheService;
    private readonly DocumentService _documentService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<NotificationMessage> _logMessages = new();
    private readonly object _logLock = new();
    private int _lastSavedCount = 0;
    private const int LogSaveInterval = 10;

    public GenQaBackgroundJobService(
        ILogger<GenQaBackgroundJobService> logger,
        IUnitOfWork uow,
        IProcessBroadcaster broadcaster,
        ICacheService cacheService,
        DocumentService documentService,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _uow = uow;
        _broadcaster = broadcaster;
        _cacheService = cacheService;
        _documentService = documentService;
        _scopeFactory = scopeFactory;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ProcessGenChunkQA(Guid documentId, CancellationToken cancellationToken, PerformContext? context = null)
    {
        _logger.LogInformation("Starting background processing for fileId: {Id}", documentId);

        lock (_logLock)
        {
            _logMessages.Clear();
        }
        _lastSavedCount = 0;

        try
        {
            await _broadcaster.ClearHistoryAsync("gen-qa", documentId);
        }
        catch (Exception clearEx)
        {
            _logger.LogWarning(clearEx, "Failed to clear history for document {Id}", documentId);
        }

        Document? document = null;
        var totalSw = Stopwatch.StartNew();

        try
        {
            await AddLogAndBroadcastAsync(documentId, $"Processing started", "Started");

            document = await InitializeJobStateAsync(documentId, context);
            if (document == null) return;

            if (string.IsNullOrEmpty(document.OcrContent)) throw new Exception("Markdown content empty");

            var metadataTask = RunMetadataExtractionAsync(document, cancellationToken);
            // var genQaTask = RunGenQAPipelineInNewScopeAsync(document.Id, cancellationToken);
            var genQaTask = RunChunkAndSummaryOnlyInNewScopeAsync(document.Id, cancellationToken);

            await Task.WhenAll(metadataTask, genQaTask);

            document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
            {
                _logger.LogWarning("Document not found after pipeline for finalization: {Id}", documentId);
                return;
            }
            await FinalizeSuccessAsync(document, totalSw);
        }
        catch (OperationCanceledException)
        {
            await HandleJobCancelledAsync(documentId, document);
        }
        catch (Exception ex)
        {
            await HandleJobErrorAsync(documentId, document, ex);
            throw;
        }
        finally
        {
            await CleanupConcurrenyAndCacheAsync(documentId, context);
        }
    }

    private async Task<Document?> InitializeJobStateAsync(Guid documentId, PerformContext? context)
    {
        await _uow.BeginTransactionAsync();
        try
        {
            var document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
            {
                _logger.LogWarning("File metadata not found: {Id}", documentId);
                await _uow.RollbackTransactionAsync();
                return null;
            }

            if (string.IsNullOrEmpty(document.OcrContent))
            {
                _logger.LogWarning("File markdown not found: {Id}", documentId);
                await AddLogAndBroadcastAsync(documentId, $"File markdown not found: {documentId}. Please process OCR first.", StatusDocument.Failed.ToString());
                await _uow.RollbackTransactionAsync();
                return null;
            }

            document.Status = StatusDocument.ProcessingGenQa;
            document.GenQaCount = document.GenQaCount + 1;
            _uow.Documents.Update(document);

            var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (documentJob != null)
            {
                documentJob.StatusGenQa = StatusJob.Processing;
                if (context != null)
                {
                    documentJob.GenQaJobId = context.BackgroundJob.Id;
                }
                _uow.DocumentJobs.Update(documentJob);
            }

            await _uow.SaveChangesAsync();
            await _uow.CommitTransactionAsync();

            return document;
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            _logger.LogError(ex, "Error initializing job state for file {Id}", documentId);
            throw;
        }
    }

    private async Task RunMetadataExtractionAsync(Document document, CancellationToken ct)
    {
        if (document.IsMetadataExtracted && !string.IsNullOrEmpty(document.MetadataContent))
        {
            _logger.LogInformation("[METADATA] Using cached metadata for file {Id}", document.Id);
            await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Using cached metadata for {document.FileName}");
            return;
        }

        await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Extracting metadata for: {document.FileName}");

        var sw = Stopwatch.StartNew();
        var result = await _documentService.ExtractMetadataAsync(document.Id, ct);
        sw.Stop();

        if (result.IsSuccess)
        {
            _logger.LogInformation("[METADATA] Metadata extraction completed for file {Id} in {Time:0.00}s", document.Id, sw.Elapsed.TotalSeconds);
            await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Metadata extraction completed in {sw.Elapsed.TotalSeconds:0.00}s", processingTime: sw.Elapsed.TotalSeconds);
        }
        else
        {
            document.MetadataError = result.ErrorMessage;
            _uow.Documents.Update(document);
            _logger.LogWarning("[METADATA] Metadata extraction failed for file {Id}: {Error}", document.Id, result.ErrorMessage);
            await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Metadata extraction failed: {result.ErrorMessage}", "Failed");
        }
    }

    private async Task RunGenQAPipelineInNewScopeAsync(Guid documentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<GenQaPipelineRunner>();
        await runner.RunAsync(documentId, ct);
    }

    private async Task RunChunkAndSummaryOnlyInNewScopeAsync(Guid documentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<GenQaPipelineRunner>();
        await runner.RunChunkAndSummaryOnlyAsync(documentId, ct);
    }

    private async Task FinalizeSuccessAsync(Document document, Stopwatch sw)
    {
        sw.Stop();
        await AddLogAndBroadcastAsync(document.Id, $"Processing completed in {sw.Elapsed.TotalSeconds:0.00}s", "Succeeded", sw.Elapsed.TotalSeconds);
        _logger.LogInformation("GenQA job SUCCEEDED for file {Id} in {Time}s", document.Id, sw.Elapsed.TotalSeconds);

        await _uow.BeginTransactionAsync();
        try
        {
            await UpdateJobLogsAsync(document.Id);

            document.Status = StatusDocument.Succeeded;
            document.ProcessingTimeGenQa = (int)sw.Elapsed.TotalSeconds;
            document.IsQaGenerated = true;
            _uow.Documents.Update(document);

            var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
            if (documentJob != null)
            {
                documentJob.StatusGenQa = StatusJob.Succeeded;
                _uow.DocumentJobs.Update(documentJob);
            }

            await _uow.SaveChangesAsync();
            await _uow.CommitTransactionAsync();
        }
        catch (Exception)
        {
            await _uow.RollbackTransactionAsync();
            throw;
        }
    }

    private async Task HandleJobCancelledAsync(Guid documentId, Document? document)
    {
        _logger.LogWarning("Process Gen Chunk QAs was cancelled for file {Id}.", documentId);

        await AddLogAndBroadcastAsync(documentId, "Job was cancelled", "Canceled");
        _logger.LogWarning("GenQA job CANCELED for file {Id}", documentId);

        if (document != null)
        {
            await _uow.BeginTransactionAsync();
            try
            {
                document.Status = StatusDocument.Canceled;
                _uow.Documents.Update(document);

                var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
                if (documentJob != null)
                {
                    documentJob.StatusGenQa = StatusJob.Canceled;
                    _uow.DocumentJobs.Update(documentJob);
                }

                await _uow.SaveChangesAsync();
                await UpdateJobLogsAsync(documentId);
                await _uow.CommitTransactionAsync();
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Failed to update DB status for file {Id}", documentId);
                await _uow.RollbackTransactionAsync();
            }
        }
    }

    private async Task HandleJobErrorAsync(Guid documentId, Document? document, Exception ex)
    {
        _logger.LogError(ex, "Error processing file: {Id}", documentId);

        try
        {
            await _broadcaster.ClearHistoryAsync("gen-qa", documentId);
        }
        catch (Exception clearEx)
        {
            _logger.LogWarning(clearEx, "Failed to clear broadcast history for document {Id}", documentId);
        }

        await AddLogAndBroadcastAsync(documentId, $"Error: {ex.Message}", "Failed");
        _logger.LogError("GenQA job FAILED for file {Id}: {Message}", documentId, ex.Message);

        if (document != null)
        {
            await _uow.BeginTransactionAsync();
            try
            {
                document.Status = StatusDocument.Failed;
                _uow.Documents.Update(document);

                var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
                if (documentJob != null)
                {
                    documentJob.StatusGenQa = StatusJob.Failed;
                    _uow.DocumentJobs.Update(documentJob);
                }

                await _uow.SaveChangesAsync();
                await UpdateJobLogsAsync(documentId);
                await _uow.CommitTransactionAsync();
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Failed to update DB status for file {Id}", documentId);
                await _uow.RollbackTransactionAsync();
            }
        }
    }

    private async Task CleanupConcurrenyAndCacheAsync(Guid documentId, PerformContext? context)
    {
        if (context != null)
        {
            await _cacheService.TryClearActiveGenQAJobIdAsync(documentId, context.BackgroundJob.Id);
        }
    }

    private async Task AddLogAndBroadcastAsync(Guid documentId, string message, string? status = null, double? processingTime = null)
    {
        var notification = new NotificationMessage
        {
            DocumentId = documentId,
            Message = message,
            Status = status ?? StatusDocument.ProcessingGenQa.ToString(),
            Stage = "GenQA",
            Timestamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            ProcessingTime = processingTime
        };

        lock (_logLock)
        {
            _logMessages.Add(notification);
        }

        await _broadcaster.PublishAsync("gen-qa", notification);

        if (message.StartsWith("[STEP 3] Progress:") && processingTime.HasValue)
        {
            int currentCount;
            lock (_logLock)
            {
                currentCount = _logMessages.Count;
            }
            if (currentCount - _lastSavedCount >= LogSaveInterval)
            {
                _lastSavedCount = currentCount;
                await PersistLogsAsync(documentId);
            }
        }
    }

    private async Task PersistLogsAsync(Guid documentId)
    {
        List<LogEvent> logEvents;
        lock (_logLock)
        {
            logEvents = _logMessages.Select(m => new LogEvent
            {
                TaskId = documentId.ToString(),
                Status = m.Status,
                Message = m.Message,
                Time = m.Timestamp,
                ProcessingTime = m.ProcessingTime
            }).ToList();
        }

        var existingLog = await _uow.LogMessages.GetByDocumentIdAsync(documentId);
        if (existingLog != null)
        {
            existingLog.LogsGenQa = logEvents;
            _uow.LogMessages.Update(existingLog);
        }
        else
        {
            await _uow.LogMessages.AddAsync(new LogMessage { LogsGenQa = logEvents, DocumentId = documentId });
        }
        await _uow.SaveChangesAsync();
    }

    private async Task UpdateJobLogsAsync(Guid documentId)
    {
        await PersistLogsAsync(documentId);
    }
}