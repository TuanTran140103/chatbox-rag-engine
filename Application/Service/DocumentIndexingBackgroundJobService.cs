using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using Microsoft.EntityFrameworkCore;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Service;

public class DocumentIndexingBackgroundJobService : IDocumentIndexingBackgroundJobService
{
    private readonly ILogger<DocumentIndexingBackgroundJobService> _logger;
    private readonly IUnitOfWork _uow;
    private readonly IProcessBroadcaster _broadcaster;
    private readonly ICacheService _cacheService;
    private readonly DocumentService _documentService;
    private readonly IMarkdownService _markdownService;
    private readonly GenQAsService _genQAsService;
    private readonly IQdrantService _qdrantService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<NotificationMessage> _logMessages = new();
    private readonly object _logLock = new();
    private int _lastSavedCount = 0;
    private const int LogSaveInterval = 10;

    public DocumentIndexingBackgroundJobService(
        ILogger<DocumentIndexingBackgroundJobService> logger,
        IUnitOfWork uow,
        IProcessBroadcaster broadcaster,
        ICacheService cacheService,
        DocumentService documentService,
        IMarkdownService markdownService,
        GenQAsService genQAsService,
        IQdrantService qdrantService,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _uow = uow;
        _broadcaster = broadcaster;
        _cacheService = cacheService;
        _documentService = documentService;
        _markdownService = markdownService;
        _genQAsService = genQAsService;
        _qdrantService = qdrantService;
        _scopeFactory = scopeFactory;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ProcessIndexing(Guid documentId, CancellationToken cancellationToken, PerformContext? context = null)
    {
        _logger.LogInformation("Starting document indexing for fileId: {Id}", documentId);

        lock (_logLock)
        {
            _logMessages.Clear();
        }
        _lastSavedCount = 0;

        try
        {
            await _broadcaster.ClearHistoryAsync("indexing", documentId);
        }
        catch (Exception clearEx)
        {
            _logger.LogWarning(clearEx, "Failed to clear history for document {Id}", documentId);
        }

        Document? document = null;
        var totalSw = Stopwatch.StartNew();

        try
        {
            await AddLogAndBroadcastAsync(documentId, "Indexing started", "Started");

            document = await InitializeJobStateAsync(documentId, context);
            if (document == null) return;

            if (string.IsNullOrEmpty(document.OcrContent))
                throw new Exception("OCR content is empty. Cannot start indexing without OCR.");

            string? datasetIdStr = null;
            if (document.DatasetItem?.DatasetId != null)
                datasetIdStr = document.DatasetItem.DatasetId.ToString();

            var ocrContent = document.OcrContent;
            var fileName = document.FileName;

            var metadataTask = RunMetadataExtractionAsync(document, cancellationToken);
            var chunkingTask = RunChunkingAsync(document, ocrContent, cancellationToken);
            var summaryTask = RunSummaryAsync(document, ocrContent, fileName, cancellationToken);

            await Task.WhenAll(metadataTask, chunkingTask, summaryTask);

            var chunkResult = await chunkingTask;
            var summaryResult = await summaryTask;

            if (chunkResult != null)
            {
                document.ChunkContent = JsonSerializer.Serialize(chunkResult);
            }
            if (summaryResult != null)
            {
                document.SummaryContent = summaryResult;
            }
            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

            await AddLogAndBroadcastAsync(documentId, "Phase 1 completed. Starting Qdrant indexing...");

            await RunQdrantIndexingAsync(document.Id, document.DatasetItem?.DatasetId, cancellationToken);

            document = await _uow.Documents.GetByIdAsync(documentId);
            if (document == null)
            {
                _logger.LogWarning("Document not found after indexing for finalization: {Id}", documentId);
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
                _logger.LogWarning("Document not found: {Id}", documentId);
                await _uow.RollbackTransactionAsync();
                return null;
            }

            if (string.IsNullOrEmpty(document.OcrContent))
            {
                _logger.LogWarning("OCR content not found: {Id}", documentId);
                await AddLogAndBroadcastAsync(documentId, $"OCR content not found: {documentId}. Please process OCR first.", StatusDocument.Failed.ToString());
                await _uow.RollbackTransactionAsync();
                return null;
            }

            document = await _uow.Documents.Query
                .Include(d => d.DatasetItem)
                .FirstOrDefaultAsync(d => d.Id == documentId);
            if (document == null)
            {
                await _uow.RollbackTransactionAsync();
                return null;
            }

            document.Status = StatusDocument.ProcessingIndexing;
            document.IndexingCount = document.IndexingCount + 1;
            _uow.Documents.Update(document);

            var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (documentJob != null)
            {
                documentJob.StatusIndexing = StatusJob.Processing;
                if (context != null)
                {
                    documentJob.IndexingJobId = context.BackgroundJob.Id;
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
            _logger.LogError(ex, "Error initializing indexing state for file {Id}", documentId);
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
            var doc = await _uow.Documents.GetByIdAsync(document.Id);
            if (doc != null)
            {
                doc.MetadataError = result.ErrorMessage;
                _uow.Documents.Update(doc);
                await _uow.SaveChangesAsync();
            }
            _logger.LogWarning("[METADATA] Metadata extraction failed for file {Id}: {Error}", document.Id, result.ErrorMessage);
            await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Metadata extraction failed: {result.ErrorMessage}", "Failed");
        }
    }

    private async Task<List<ChunkInfo>?> RunChunkingAsync(Document document, string ocrContent, CancellationToken ct)
    {
        // if (!string.IsNullOrEmpty(document.ChunkContent))
        // {
        //     _logger.LogInformation("[CHUNKING] Using cached chunks for file {Id}", document.Id);
        //     await AddLogAndBroadcastAsync(document.Id, "[CHUNKING] Using cached chunks");
        //     return JsonSerializer.Deserialize<List<ChunkInfo>>(document.ChunkContent);
        // }

        await AddLogAndBroadcastAsync(document.Id, "[CHUNKING] Splitting document into chunks...");

        var sw = Stopwatch.StartNew();
        try
        {
            var textChunksTask = _markdownService.CreateChunkAsync(ocrContent);
            var tableChunksTask = _markdownService.CreateChunkTableAsync(ocrContent);

            await Task.WhenAll(textChunksTask, tableChunksTask);

            var textChunks = await textChunksTask;
            var tableChunks = await tableChunksTask;

            var allChunks = await _markdownService.MergeAndReindexChunksAsync(ocrContent, textChunks, tableChunks, ct);

            _logger.LogInformation("[CHUNKING] Created {TextCount} text + {TableCount} table = {Total} chunks for document {Id}",
                textChunks.Count, tableChunks.Count, allChunks.Count, document.Id);

            await AddLogAndBroadcastAsync(document.Id,
                $"[CHUNKING] Created {allChunks.Count} chunks ({textChunks.Count} text + {tableChunks.Count} table) in {sw.Elapsed.TotalSeconds:0.00}s",
                processingTime: sw.Elapsed.TotalSeconds);

            var largeChunks = allChunks.Where(c => c.NeedsSummary).ToList();
            if (largeChunks.Count > 0)
            {
                _logger.LogInformation("[CHUNKING] Summarizing {Count} large chunks for document {Id}", largeChunks.Count, document.Id);
                await AddLogAndBroadcastAsync(document.Id, $"[CHUNKING] Summarizing {largeChunks.Count} large chunks...");

                var concurrency = new SemaphoreSlim(3);
                var summaryTasks = largeChunks.Select(async chunk =>
                {
                    await concurrency.WaitAsync(ct);
                    try
                    {
                        var chunkName = chunk.Title ?? chunk.TittleHirarchy ?? $"chunk {chunk.Index}";
                        var summary = await _genQAsService.GenSummaryDocumentAsync(chunk.Content, chunkName, ct);
                        chunk.ContentSummary = summary;
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                });
                await Task.WhenAll(summaryTasks);
                concurrency.Dispose();

                _logger.LogInformation("[CHUNKING] Large chunk summarization completed for document {Id}", document.Id);
            }

            sw.Stop();
            return allChunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CHUNKING] Failed for document {Id}", document.Id);
            await AddLogAndBroadcastAsync(document.Id, $"[CHUNKING] Failed: {ex.Message}", "Failed");
            throw;
        }
    }

    private async Task<string?> RunSummaryAsync(Document document, string ocrContent, string fileName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(document.SummaryContent))
        {
            _logger.LogInformation("[SUMMARY] Using cached summary for file {Id}", document.Id);
            await AddLogAndBroadcastAsync(document.Id, "[SUMMARY] Using cached summary");
            return document.SummaryContent;
        }

        await AddLogAndBroadcastAsync(document.Id, $"[SUMMARY] Generating summary for: {fileName}");

        var sw = Stopwatch.StartNew();
        try
        {
            var summary = await _genQAsService.GenSummaryDocumentAsync(ocrContent, fileName, ct);
            sw.Stop();

            if (!string.IsNullOrEmpty(summary))
            {
                _logger.LogInformation("[SUMMARY] Summary generated for document {Id} in {Time:0.00}s ({Length} chars)",
                    document.Id, sw.Elapsed.TotalSeconds, summary.Length);
                await AddLogAndBroadcastAsync(document.Id,
                    $"[SUMMARY] Summary generated in {sw.Elapsed.TotalSeconds:0.00}s",
                    processingTime: sw.Elapsed.TotalSeconds);
                return summary;
            }

            _logger.LogWarning("[SUMMARY] Empty summary returned for document {Id}", document.Id);
            await AddLogAndBroadcastAsync(document.Id, "[SUMMARY] Empty summary returned");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SUMMARY] Failed for document {Id}", document.Id);
            await AddLogAndBroadcastAsync(document.Id, $"[SUMMARY] Failed: {ex.Message}", "Failed");
            throw;
        }
    }

    private async Task RunQdrantIndexingAsync(Guid documentId, Guid? datasetId, CancellationToken ct)
    {
        if (datasetId == null)
        {
            _logger.LogWarning("[QDRANT] Document {Id} has no DatasetId, skipping indexing", documentId);
            await AddLogAndBroadcastAsync(documentId, "[QDRANT] Skipped: document has no dataset");
            return;
        }

        await AddLogAndBroadcastAsync(documentId, "[QDRANT] Indexing document to vector database...");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _qdrantService.AddDocumentPointAsync("documents", documentId, datasetId.Value, ct);

            sw.Stop();

            _logger.LogInformation("[QDRANT] Indexing completed for document {Id} in {Time:0.00}s, status: {Status}",
                documentId, sw.Elapsed.TotalSeconds, result.Status);
            await AddLogAndBroadcastAsync(documentId,
                $"[QDRANT] Indexing completed in {sw.Elapsed.TotalSeconds:0.00}s",
                processingTime: sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QDRANT] Indexing failed for document {Id}", documentId);
            await AddLogAndBroadcastAsync(documentId, $"[QDRANT] Indexing failed: {ex.Message}", "Failed");
            throw;
        }
    }

    private async Task FinalizeSuccessAsync(Document document, Stopwatch sw)
    {
        sw.Stop();
        await AddLogAndBroadcastAsync(document.Id, $"Indexing completed in {sw.Elapsed.TotalSeconds:0.00}s", "Succeeded", sw.Elapsed.TotalSeconds);
        _logger.LogInformation("Indexing job SUCCEEDED for file {Id} in {Time}s", document.Id, sw.Elapsed.TotalSeconds);

        await _uow.BeginTransactionAsync();
        try
        {
            await UpdateJobLogsAsync(document.Id);

            document.Status = StatusDocument.Successed;
            document.ProcessingTimeIndexing = (int)sw.Elapsed.TotalSeconds;
            document.IsIndexed = true;
            _uow.Documents.Update(document);

            var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
            if (documentJob != null)
            {
                documentJob.StatusIndexing = StatusJob.Succeeded;
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
        _logger.LogWarning("Indexing job was cancelled for file {Id}.", documentId);

        await AddLogAndBroadcastAsync(documentId, "Job was cancelled", "Canceled");
        _logger.LogWarning("Indexing job CANCELED for file {Id}", documentId);

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
                    documentJob.StatusIndexing = StatusJob.Canceled;
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
        _logger.LogError(ex, "Error indexing file: {Id}", documentId);

        try
        {
            await _broadcaster.ClearHistoryAsync("indexing", documentId);
        }
        catch (Exception clearEx)
        {
            _logger.LogWarning(clearEx, "Failed to clear broadcast history for document {Id}", documentId);
        }

        await AddLogAndBroadcastAsync(documentId, $"Error: {ex.Message}", "Failed");

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
                    documentJob.StatusIndexing = StatusJob.Failed;
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
            Status = status ?? StatusDocument.ProcessingIndexing.ToString(),
            Stage = "Indexing",
            Timestamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            ProcessingTime = processingTime
        };

        lock (_logLock)
        {
            _logMessages.Add(notification);
        }

        await _broadcaster.PublishAsync("indexing", notification);

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
            existingLog.LogsIndexing = logEvents;
            _uow.LogMessages.Update(existingLog);
        }
        else
        {
            await _uow.LogMessages.AddAsync(new LogMessage { LogsIndexing = logEvents, DocumentId = documentId });
        }
        await _uow.SaveChangesAsync();
    }

    private async Task UpdateJobLogsAsync(Guid documentId)
    {
        await PersistLogsAsync(documentId);
    }
}
