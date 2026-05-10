using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.QA;
using GenQAServer.Options;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Application.Service;

public class GenQaBackgroundJobService : IGenQaBackgroundJobService
{
    private readonly ILogger<GenQaBackgroundJobService> _logger;
    private readonly IUnitOfWork _uow;
    private readonly IMarkdownService _markdownService;
    private readonly GenQAsService _genQAsService;
    private readonly IS3Service _s3Service;
    private readonly IProcessBroadcaster _broadcaster;
    private readonly ICacheService _cacheService;
    private readonly ITokenCountService _tokenCountService;
    private readonly DocumentService _documentService;
    private readonly DocumentProcessOption _documentProcessOption;
    private readonly List<NotificationMessage> _logMessages = new();
    private readonly object _logLock = new();
    private int _lastSavedCount = 0;
    private const int LogSaveInterval = 10;

    public GenQaBackgroundJobService(
        ILogger<GenQaBackgroundJobService> logger,
        IUnitOfWork uow,
        GenQAsService genQAsService,
        IMarkdownService markdownService,
        IS3Service s3Service,
        IProcessBroadcaster broadcaster,
        ICacheService cacheService,
        ITokenCountService tokenCountService,
        DocumentService documentService,
        IOptions<DocumentProcessOption> documentProcessOption)
    {
        _logger = logger;
        _uow = uow;
        _genQAsService = genQAsService;
        _markdownService = markdownService;
        _s3Service = s3Service;
        _broadcaster = broadcaster;
        _cacheService = cacheService;
        _tokenCountService = tokenCountService;
        _documentService = documentService;
        _documentProcessOption = documentProcessOption.Value;
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
            var genQaTask = RunGenQAPipelineAsync(document, cancellationToken);

            await Task.WhenAll(metadataTask, genQaTask);

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

    private async Task InitializeJobStateForPipelineAsync(Document document, PerformContext? context)
    {
        document.Status = StatusDocument.ProcessingGenQa;
        document.GenQaCount = document.GenQaCount + 1;
        _uow.Documents.Update(document);

        var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
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

        await _uow.BeginTransactionAsync();
        try
        {
            var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
            if (documentJob != null)
            {
                documentJob.StatusMetadata = StatusJob.Processing;
                _uow.DocumentJobs.Update(documentJob);
                await _uow.SaveChangesAsync();
            }
            await _uow.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            _logger.LogWarning(ex, "Failed to set StatusMetadata=Processing for document {Id}", document.Id);
        }

        await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Extracting metadata for: {document.FileName}");

        var sw = Stopwatch.StartNew();
        var result = await _documentService.ExtractMetadataAsync(document.Id, ct);
        sw.Stop();

        await _uow.BeginTransactionAsync();
        try
        {
            var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(document.Id);
            if (documentJob != null)
            {
                if (result.IsSuccess)
                {
                    documentJob.StatusMetadata = StatusJob.Succeeded;
                    _logger.LogInformation("[METADATA] Metadata extraction completed for file {Id} in {Time:0.00}s", document.Id, sw.Elapsed.TotalSeconds);
                    await AddLogAndBroadcastAsync(document.Id, $"[METADATA] Metadata extraction completed in {sw.Elapsed.TotalSeconds:0.00}s", processingTime: sw.Elapsed.TotalSeconds);
                }
                else
                {
                    documentJob.StatusMetadata = StatusJob.Failed;
                    documentJob.MetadataError = result.ErrorMessage;
                    document.MetadataError = result.ErrorMessage;
                    _uow.Documents.Update(document);
                    _logger.LogWarning("[METADATA] Metadata extraction failed for file {Id}: {Error}", document.Id, result.ErrorMessage);
                }
                _uow.DocumentJobs.Update(documentJob);
                await _uow.SaveChangesAsync();
            }
            await _uow.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            _logger.LogWarning(ex, "Failed to update StatusMetadata for document {Id}", document.Id);
            return;
        }
    }

    private async Task RunGenQAPipelineAsync(Document document, CancellationToken ct)
    {
        var markdownContent = document.OcrContent!;

        var summary = await RunPhase1Async(document, markdownContent, ct);

        var chunkingTask = RunChunkingAsync(document, markdownContent, ct);
        var tableChunkingTask = RunTableChunkingAsync(document, markdownContent, ct);

        await Task.WhenAll(chunkingTask, tableChunkingTask);

        var textChunks = await chunkingTask;
        var tableChunks = await tableChunkingTask;
        var allChunks = textChunks.Concat(tableChunks).ToList();

        var phase2Task = RunPhase2Async(document, summary, allChunks, ct);
        var summaryChunksTask = SummarizeLargeChunksAsync(document, textChunks, ct);

        await Task.WhenAll(phase2Task, summaryChunksTask);

        var chunkQAInfors = await phase2Task;

        await RunSaveResultsAsync(document, summary, chunkQAInfors);
    }

    private async Task<string> RunPhase1Async(
        Document document,
        string markdownContent,
        CancellationToken ct)
    {
        var summary = await RunSummaryPhaseAsync(document, markdownContent, ct);

        document.SummaryContent = summary;
        _uow.Documents.Update(document);
        await _uow.SaveChangesAsync();

        return summary;
    }

    private async Task<string> RunSummaryPhaseAsync(
        Document document,
        string markdownContent,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(document.SummaryContent))
        {
            _logger.LogInformation("[STEP 1a] Using cached summary for {FileName}", document.FileName);
            await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] Using cached summary for {document.FileName}");
            return document.SummaryContent;
        }

        var sw = Stopwatch.StartNew();
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] Summary generating file: {document.FileName}");

        string summary;
        var totalTokens = (await _tokenCountService.CountAsync(new() { Text = markdownContent }, ct)).TokenCount;
        _logger.LogInformation($"[STEP 1a] Total tokens: {totalTokens}");

        if (totalTokens <= _documentProcessOption.SummaryChunkMaxTokens)
        {
            summary = await _genQAsService.GenSummaryDocumentAsync(markdownContent, document.FileName, ct);
        }
        else
        {
            var chunks = await _markdownService.SplitDocumentForSummaryAsync(markdownContent, _documentProcessOption.SummaryChunkMaxTokens, ct);
            var subSummaries = new string[chunks.Count];

            using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var subTasks = chunks.Select((chunk, i) =>
                RunWithConcurrencyAsync(summaryCts, async t =>
                {
                    var sectionName = string.IsNullOrEmpty(chunk.Title) ? $"{document.FileName}" : $"{document.FileName}[{chunk.Title}]";
                    subSummaries[i] = await _genQAsService.GenSummaryDocumentAsync(chunk.Content, sectionName, t);
                })
            ).ToList();
            await Task.WhenAll(subTasks);

            var mergedChunks = new List<SummaryChunk>();
            for (int i = 0; i < chunks.Count; i++)
            {
                mergedChunks.Add(new SummaryChunk
                {
                    Content = subSummaries[i],
                    HierarchyPath = chunks[i].HierarchyPath,
                    Title = chunks[i].Title
                });
            }

            summary = await _genQAsService.MergeSummaryChunksAsync(mergedChunks, document.FileName, ct);
            await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] Merged summary from {chunks.Count} sections");
        }

        sw.Stop();
        _logger.LogInformation("[STEP 1a] Summary completed in {0:0.00}s", sw.Elapsed.TotalSeconds);
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] Summary completed in {sw.Elapsed.TotalSeconds:0.00}s", processingTime: sw.Elapsed.TotalSeconds);

        return summary;
    }

    private async Task<List<ChunkInfo>> RunChunkingAsync(Document document, string markdownContent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 2] Chunking file: {document.FileName}");

        var chunks = await _markdownService.CreateChunkAsync(markdownContent, ct);

        sw.Stop();

        _logger.LogInformation("[STEP 2] Chunking completed in {0:0.00}s - {1} chunks", sw.Elapsed.TotalSeconds, chunks.Count);
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 2] Chunking completed in {sw.Elapsed.TotalSeconds:0.00}s ({chunks.Count} chunks)", processingTime: sw.Elapsed.TotalSeconds);

        return chunks;
    }

    private async Task<List<ChunkInfo>> RunTableChunkingAsync(Document document, string markdownContent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await AddLogAndBroadcastAsync(document.Id, $"[TABLE] Creating table chunks for: {document.FileName}");

        var tableChunks = await _markdownService.CreateChunkTableAsync(markdownContent, null, ct);

        sw.Stop();

        _logger.LogInformation("[TABLE] Table chunking completed in {0:0.00}s - {1} table chunks", sw.Elapsed.TotalSeconds, tableChunks.Count);
        await AddLogAndBroadcastAsync(document.Id, $"[TABLE] Table chunking completed in {sw.Elapsed.TotalSeconds:0.00}s ({tableChunks.Count} table chunks)", processingTime: sw.Elapsed.TotalSeconds);

        return tableChunks;
    }

    private async Task SummarizeLargeChunksAsync(Document document, List<ChunkInfo> chunks, CancellationToken ct)
    {
        var chunksToSummarize = chunks.Where(c => c.NeedsSummary).ToList();
        if (chunksToSummarize.Count == 0) return;

        await AddLogAndBroadcastAsync(document.Id, $"[CHUNK SUMMARY] Summarizing {chunksToSummarize.Count} large chunks...");

        using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = chunksToSummarize.Select(chunk =>
            RunWithConcurrencyAsync(summaryCts, async token =>
            {
                var chunkName = string.IsNullOrEmpty(chunk.Title) ? "untitled" : chunk.Title;
                var contentSummary = await _genQAsService.GenSummaryDocumentAsync(chunk.Content, chunkName, token);
                chunk.ContentSummary = contentSummary;
            })
        ).ToList();

        await Task.WhenAll(tasks);

        var summarizedCount = chunksToSummarize.Count(c => !string.IsNullOrEmpty(c.ContentSummary));
        await AddLogAndBroadcastAsync(document.Id, $"[CHUNK SUMMARY] Done - {summarizedCount}/{chunksToSummarize.Count} chunks summarized");
    }

    private async Task<List<ChunkQAInfor>> RunPhase2Async(
        Document document,
        string summary,
        List<ChunkInfo> allChunks,
        CancellationToken ct)
    {
        var tableChunks = allChunks.Where(c => c.Type == TypeChunk.Table).ToList();
        var textCount = allChunks.Count(c => c.Type != TypeChunk.Table);
        var tableCount = tableChunks.Count;

        await AddLogAndBroadcastAsync(document.Id,
            $"[STEP 3] QAs for {tableCount} table chunk(s), {textCount} text chunk(s) skipped: {document.FileName}");

        var results = new ChunkQAInfor[allChunks.Count];

        if (tableCount > 0)
        {
            int completed = 0;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var tableTasks = tableChunks.Select(chunk =>
                RunWithConcurrencyAsync(linkedCts, async token =>
                {
                    var qas = await _genQAsService.GenQAsTableAsync(chunk, summary, document.FileName, token);
                    var done = Interlocked.Increment(ref completed);
                    var chunkName = string.IsNullOrEmpty(chunk.Title) ? "untitled" : chunk.Title;

                    _logger.LogInformation("[STEP 3] documentId: {0} Progress: {1}/{2} - table chunk '{3}'", document.Id, done, tableCount, chunkName);
                    await AddLogAndBroadcastAsync(document.Id,
                        $"[STEP 3] Progress: {done}/{tableCount} - table chunk '{chunkName}'");

                    return (chunk, qas);
                })
            ).ToList();

            var tableResults = await Task.WhenAll(tableTasks);
            var qaByChunk = tableResults.ToDictionary(r => r.chunk, r => r.qas);

            for (int i = 0; i < allChunks.Count; i++)
            {
                var chunk = allChunks[i];
                if (chunk.Type == TypeChunk.Table)
                {
                    results[i] = new ChunkQAInfor
                    {
                        ChunkInfo = chunk,
                        QAs = qaByChunk.GetValueOrDefault(chunk, new List<ChunkQA>())
                    };
                }
                else
                {
                    results[i] = new ChunkQAInfor
                    {
                        ChunkInfo = chunk,
                        QAs = new List<ChunkQA>()
                    };
                }
            }
        }
        else
        {
            for (int i = 0; i < allChunks.Count; i++)
            {
                results[i] = new ChunkQAInfor
                {
                    ChunkInfo = allChunks[i],
                    QAs = new List<ChunkQA>()
                };
            }
        }

        return results.ToList();
    }

    private async Task RunSaveResultsAsync(Document document, string summary, List<ChunkQAInfor> results)
    {
        var sw = Stopwatch.StartNew();
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 4] Saving results to DB: {document.FileName}");

        string qaJson = JsonSerializer.Serialize(results);
        document.QaContent = qaJson;
        document.SummaryContent = summary;
        _uow.Documents.Update(document);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("[STEP 4] Saving completed in {0:0.00}s", sw.Elapsed.TotalSeconds);
        sw.Stop();
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 4] Saving completed in {sw.Elapsed.TotalSeconds:0.00}s", processingTime: sw.Elapsed.TotalSeconds);
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

            document.Status = StatusDocument.Successed;
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

    private async Task RunWithConcurrencyAsync(
        CancellationTokenSource linkedCts,
        Func<CancellationToken, Task> work)
    {
        var token = linkedCts.Token;
        token.ThrowIfCancellationRequested();
        try
        {
            await work(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            linkedCts.Cancel();
            throw;
        }
    }

    private async Task<T> RunWithConcurrencyAsync<T>(
        CancellationTokenSource linkedCts,
        Func<CancellationToken, Task<T>> work)
    {
        var token = linkedCts.Token;
        token.ThrowIfCancellationRequested();
        try
        {
            return await work(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            linkedCts.Cancel();
            throw;
        }
    }
}