using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
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
    private readonly DocumentProcessOption _documentProcessOption;
    private readonly List<NotificationMessage> _logMessages = new();
    private readonly object _logLock = new();

    public GenQaBackgroundJobService(
        ILogger<GenQaBackgroundJobService> logger,
        IUnitOfWork uow,
        GenQAsService genQAsService,
        IMarkdownService markdownService,
        IS3Service s3Service,
        IProcessBroadcaster broadcaster,
        ICacheService cacheService,
        IOptions<DocumentProcessOption> documentProcessOption)
    {
        _logger = logger;
        _uow = uow;
        _genQAsService = genQAsService;
        _markdownService = markdownService;
        _s3Service = s3Service;
        _broadcaster = broadcaster;
        _cacheService = cacheService;
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

            var markdownContent = document.OcrContent;
            if (string.IsNullOrEmpty(markdownContent)) throw new Exception("Markdown content empty");

            var (summary, qaSummary) = await RunPhase1Async(document, markdownContent, cancellationToken);

            var chunks = await RunChunkingAsync(document, markdownContent, cancellationToken);

            var chunkQAInfors = await RunPhase2Async(document, summary, qaSummary, chunks, cancellationToken);

            await RunSaveResultsAsync(document, summary, chunkQAInfors);

            await FinalizeSuccessAsync(document, totalSw);
        }
        catch (TaskCanceledException ex)
        {
            await HandleJobErrorAsync(documentId, document, ex);
            throw;
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

    private async Task<(string Summary, List<ChunkQA> QASummary)> RunPhase1Async(
        Document document,
        string markdownContent,
        CancellationToken ct)
    {
        string summary = "";
        List<ChunkQA> qaSummary = new List<ChunkQA>();

        await Task.WhenAll(
            RunWithConcurrencyAsync(ct, async () =>
            {
                // STEP 1a: Summary — skip if already cached in DB
                if (!string.IsNullOrEmpty(document.SummaryContent))
                {
                    summary = document.SummaryContent;
                    _logger.LogInformation("[STEP 1a] ✅ Using cached summary for {FileName}", document.FileName);
                    await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] Using cached summary for {document.FileName}");
                    return;
                }

                var sw = Stopwatch.StartNew();
                await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] Summary generating file: {document.FileName}");
                summary = await _genQAsService.GenSummaryDocumentAsync(markdownContent, document.FileName, ct);

                document.SummaryContent = summary;
                _uow.Documents.Update(document);
                await _uow.SaveChangesAsync();

                sw.Stop();
                _logger.LogInformation("[STEP 1a] ✅ Summary completed in {0:0.00}s", sw.Elapsed.TotalSeconds);
                await AddLogAndBroadcastAsync(document.Id, $"[STEP 1a] ✅ Summary completed in {sw.Elapsed.TotalSeconds:0.00}s");
            }),
            RunWithConcurrencyAsync(ct, async () =>
            {
                var sw = Stopwatch.StartNew();
                await AddLogAndBroadcastAsync(document.Id, $"[STEP 1b] QAs summary generating file: {document.FileName}");
                qaSummary = await _genQAsService.GenQAsSumaryAsync(markdownContent, document.FileName, ct);
                sw.Stop();
                _logger.LogInformation("[STEP 1b] ✅ QAs summary completed in {0:0.00}s", sw.Elapsed.TotalSeconds);
                await AddLogAndBroadcastAsync(document.Id, $"[STEP 1b] ✅ QAs summary completed in {sw.Elapsed.TotalSeconds:0.00}s ({qaSummary.Count} QAs)");
            })
        );

        return (summary, qaSummary);
    }

    private async Task<List<ChunkInfo>> RunChunkingAsync(Document document, string markdownContent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 2] Chunking file: {document.FileName}");

        var chunks = await _markdownService.CreateChunkAsync(markdownContent, ct);

        sw.Stop();

        await File.WriteAllTextAsync("chunks_test.json", JsonSerializer.Serialize(chunks));

        _logger.LogInformation("[STEP 2] ✅ Chunking completed in {0:0.00}s - {1} chunks", sw.Elapsed.TotalSeconds, chunks.Count);
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 2] ✅ Chunking completed in {sw.Elapsed.TotalSeconds:0.00}s ({chunks.Count} chunks)");

        return chunks;
    }

    private async Task<List<ChunkQAInfor>> RunPhase2Async(
        Document document,
        string summary,
        List<ChunkQA> qaSummary,
        List<ChunkInfo> chunks,
        CancellationToken ct)
    {
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 3] QAs for chunks generating file: {document.FileName}");

        var results = new List<ChunkQAInfor>();
        results.Add(new ChunkQAInfor { ChunkInfo = new ChunkInfo { Type = TypeChunk.Summary, TokensCount = -1, Content = "Content is markdown" }, QAs = qaSummary });

        int total = chunks.Sum(c => 1 + c.TableChunks.Count);
        int completed = 0;

        var tasks = chunks.Select(chunk =>
            RunWithConcurrencyAsync(ct, async () =>
            {
                var csw = Stopwatch.StartNew();
                try
                {
                    // Process text/summary chunk
                    var qas = await _genQAsService.GenQAsTextAsync(chunk, summary, document.FileName, ct);
                    lock (results) results.Add(new ChunkQAInfor { ChunkInfo = chunk, QAs = qas });

                    // Process nested table chunks within this chunk
                    foreach (var tableChunk in chunk.TableChunks)
                    {
                        var tableQas = await _genQAsService.GenQAsTableAsync(tableChunk, summary, document.FileName, ct);
                        lock (results) results.Add(new ChunkQAInfor { ChunkInfo = tableChunk, QAs = tableQas });
                    }

                    ct.ThrowIfCancellationRequested();
                }
                finally
                {
                    csw.Stop();
                    var done = Interlocked.Increment(ref completed);
                    await AddLogAndBroadcastAsync(document.Id, $"[STEP 3] Progress: {done}/{total} sub-chunks ({csw.ElapsedMilliseconds}ms)");
                }
            })
        );

        await Task.WhenAll(tasks);
        return results;
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

        _logger.LogInformation("[STEP 4] ✅ Saving completed in {0:0.00}s", sw.Elapsed.TotalSeconds);
        sw.Stop();
        await AddLogAndBroadcastAsync(document.Id, $"[STEP 4] ✅ Saving completed in {sw.Elapsed.TotalSeconds:0.00}s");
    }

    private async Task FinalizeSuccessAsync(Document document, Stopwatch sw)
    {
        sw.Stop();
        await AddLogAndBroadcastAsync(document.Id, $"Processing completed in {sw.Elapsed.TotalSeconds:0.00}s", "Succeeded");
        _logger.LogInformation("✅ GenQA job SUCCEEDED for file {Id} in {Time}s", document.Id, sw.Elapsed.TotalSeconds);

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

    private async Task AddLogAndBroadcastAsync(Guid documentId, string message, string? status = null)
    {
        var notification = new NotificationMessage
        {
            DocumentId = documentId,
            Message = message,
            Status = status ?? StatusDocument.ProcessingGenQa.ToString(),
            Stage = "GenQA"
        };

        lock (_logLock)
        {
            _logMessages.Add(notification);
        }

        await _broadcaster.PublishAsync("gen-qa", notification);
    }

    private async Task UpdateJobLogsAsync(Guid documentId)
    {
        List<LogEvent> logEvents;
        lock (_logLock)
        {
            logEvents = _logMessages.Select(m => new LogEvent
            {
                TaskId = documentId.ToString(),
                Status = m.Status,
                Message = m.Message,
                Time = m.Timestamp
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

    private async Task RunWithConcurrencyAsync(
        CancellationToken cancellationToken,
        Func<Task> work)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await work();
    }
}
