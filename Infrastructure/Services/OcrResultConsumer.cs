using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models;
using System.Collections.Concurrent;
using StackExchange.Redis;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using System.Text.Json;
using MarkdownGenQAs.Utils;
using MarkdownGenQAs.Infrastructure.Exceptions;

namespace MarkdownGenQAs.Infrastructure.Services;

public class OcrResultConsumer : BackgroundService, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OcrResultConsumer> _logger;
    private readonly IProcessBroadcaster _broadcaster;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _uploadSemaphore;

    private const string StreamKey = "ocr:events:stream";
    private const string ConsumerGroup = "markdowngenqas-group";
    private const string ConsumerName = "consumer-1";
    private const int BatchSize = 50;
    private const int MaxDegreeOfParallelism = 10;

    private readonly ConcurrentDictionary<string, Guid> _taskToDocumentId = new();

    private readonly ConcurrentDictionary<string, AsyncQueue<OcrEventWrapper>> _documentQueues = new();
    private readonly ConcurrentDictionary<string, Task> _documentWorkers = new();

    private class OcrEventWrapper
    {
        public StreamEntry Message { get; set; }
        public OcrEventBase? EventBase { get; set; }
        public TaskCompletionSource<bool> CompletionSource { get; set; } = new();
    }

    public OcrResultConsumer(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ILogger<OcrResultConsumer> logger,
        IProcessBroadcaster broadcaster)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _broadcaster = broadcaster;
        _uploadSemaphore = new SemaphoreSlim(MaxDegreeOfParallelism, MaxDegreeOfParallelism);
        _cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache_ocr");

        if (!Directory.Exists(_cacheDir))
        {
            Directory.CreateDirectory(_cacheDir);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== OcrResultConsumer ExecuteAsync called ===");

        var db = _redis.GetDatabase();

        _logger.LogInformation("=== Redis connection: {Server} ===", _redis.GetEndPoints().First());

        await EnsureConsumerGroupExistsAsync(db);

        _logger.LogInformation("OcrResultConsumer started, listening on {StreamKey} (BatchSize: {BatchSize}, MaxConcurrency: {MaxConcurrency})",
            StreamKey, BatchSize, MaxDegreeOfParallelism);

        _ = Task.Run(async () => await CleanupCachePeriodicallyAsync(stoppingToken), stoppingToken);

        int loopCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (++loopCount % 100 == 0)
                {
                    await db.StreamTrimAsync(StreamKey, 1000);
                }

                var messages = await db.StreamReadGroupAsync(
                    StreamKey,
                    ConsumerGroup,
                    ConsumerName,
                    ">",
                    count: BatchSize);

                if (messages.Length == 0)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                _logger.LogDebug("Received {Count} messages from Redis Stream", messages.Length);

                foreach (var message in messages)
                {
                    _ = ProcessMessageAsync(message, db, stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading from Redis Stream");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task EnsureConsumerGroupExistsAsync(IDatabase db)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, "$", true);
            _logger.LogInformation("Created Redis Stream consumer group: {Group}, starting from latest", ConsumerGroup);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("already exists"))
        {
            _logger.LogInformation("Consumer group {Group} already exists, will continue from last position", ConsumerGroup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Redis Stream consumer group");
        }
    }

    private async Task ProcessMessageAsync(StreamEntry entry, IDatabase db, CancellationToken ct)
    {
        try
        {
            if (!TryDeserializeEvent(entry, out var eventBase) || eventBase == null)
            {
                _logger.LogWarning("Invalid message format in OCR stream: {Id}", entry.Id);
                await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, entry.Id);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var docId = await GetDocumentIdAsync(eventBase.TaskId, uow);
            if (docId == null)
            {
                _logger.LogWarning("No DocumentJob found for OCR TaskId: {TaskId}", eventBase.TaskId);
                await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, entry.Id);
                return;
            }

            var docIdStr = docId.Value.ToString();

            var queue = _documentQueues.GetOrAdd(docIdStr, _ => new AsyncQueue<OcrEventWrapper>());

            var wrapper = new OcrEventWrapper
            {
                Message = entry,
                EventBase = eventBase,
                CompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            await queue.EnqueueAsync(wrapper);

            var existingOrNewWorker = _documentWorkers.GetOrAdd(docIdStr, _ =>
                Task.Run(async () => await ProcessDocumentQueueAsync(docId.Value, queue, ct, db)));

            if (existingOrNewWorker.IsCompleted)
            {
                var newWorker = Task.Run(async () => await ProcessDocumentQueueAsync(docId.Value, queue, ct, db));
                _documentWorkers.TryUpdate(docIdStr, newWorker, existingOrNewWorker);
            }

            await wrapper.CompletionSource.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message {EntryId}", entry.Id);
        }
    }

    private async Task ProcessDocumentQueueAsync(Guid docId, AsyncQueue<OcrEventWrapper> queue, CancellationToken ct, IDatabase db)
    {
        var docIdStr = docId.ToString();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wrapper = await queue.DequeueAsync(ct);
                if (wrapper == null)
                {
                    break;
                }

                var entry = wrapper.Message;

                try
                {
                    var eventBase = wrapper.EventBase;
                    if (eventBase == null)
                    {
                        _logger.LogWarning("EventBase is null in wrapper for entry: {Id}", entry.Id);
                        await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, entry.Id);
                        wrapper.CompletionSource.SetResult(true);
                        continue;
                    }

                    _logger.LogInformation("Processing OCR Event: TaskId={TaskId}, Type={Type}, Status={Status}, Message={Message}",
                        eventBase.TaskId, eventBase.EventType, eventBase.Status, eventBase.Message);

                    using var scope = _scopeFactory.CreateScope();
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                    _logger.LogDebug("Event routing: TaskId={TaskId} -> EventType={EventType} (lower: {EventTypeLower})",
                        eventBase.TaskId, eventBase.EventType, eventBase.EventType.ToLower());

                    switch (eventBase.EventType.ToLower())
                    {
                        case "logging":
                            _logger.LogDebug("Routing to HandleLoggingEventAsync for TaskId={TaskId}, Status={Status}", eventBase.TaskId, eventBase.Status);
                            await HandleLoggingEventAsync(docId, eventBase as OcrLoggingEvent, uow);
                            break;
                        case "getmarkdown":
                            await HandleGetMarkdownEventAsync(docId, eventBase as OcrGetMarkdownEvent, scope);
                            break;
                        case "savelog":
                            await HandleSaveLogEventAsync(docId, eventBase as OcrSaveLogEvent, uow);
                            break;
                    }

                    await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, entry.Id);

                    wrapper.CompletionSource.SetResult(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Processing failed for OCR event");

                    await _broadcaster.PublishAsync("ocr", new NotificationMessage
                    {
                        DocumentId = docId,
                        Message = $"OCR processing failed: {ex.Message}",
                        Status = "Failed",
                        Stage = "OCR"
                    });

                    await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, entry.Id);

                    wrapper.CompletionSource.SetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (queue.IsCompletedAndEmpty)
            {
                _documentWorkers.TryRemove(docIdStr, out _);
                _documentQueues.TryRemove(docIdStr, out _);
                _logger.LogDebug("Cleaned up worker and queue for DocumentId: {DocId}", docIdStr);
            }
        }
    }

    private bool TryDeserializeEvent(StreamEntry entry, out OcrEventBase? eventBase)
    {
        eventBase = null;

        var jsonValue = entry.Values
            .FirstOrDefault(v => v.Name.ToString() == "data"
                              || v.Name.ToString() == "dataJson"
                              || v.Name.ToString() == "data_json")
            .Value;

        if (jsonValue.IsNullOrEmpty)
        {
            _logger.LogWarning("Redis Stream entry has no 'data' field: {Id}", entry.Id);
            return false;
        }

        var jsonString = jsonValue.ToString();

        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonString);
            var root = jsonDoc.RootElement;

            var eventType = root.TryGetProperty("eventType", out var etProp) ? etProp.GetString()
                            : root.TryGetProperty("event_type", out var etSnakeProp) ? etSnakeProp.GetString()
                            : null;

            if (string.IsNullOrEmpty(eventType))
            {
                _logger.LogWarning("Event missing eventType field. Json: {Json}", jsonString);
                return false;
            }

            eventBase = eventType.ToLower() switch
            {
                "logging" => JsonSerializer.Deserialize<OcrLoggingEvent>(jsonString),
                "getmarkdown" => JsonSerializer.Deserialize<OcrGetMarkdownEvent>(jsonString),
                "savelog" => JsonSerializer.Deserialize<OcrSaveLogEvent>(jsonString),
                _ => JsonSerializer.Deserialize<OcrEventBase>(jsonString)
            };

            if (eventBase == null || string.IsNullOrEmpty(eventBase.TaskId))
            {
                _logger.LogWarning("Deserialized event is null or missing TaskId. EventType: {EventType}", eventType);
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON event. Raw JSON: {Json}", jsonString);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to deserialize JSON event - field type mismatch. Raw JSON: {Json}", jsonString);
            return false;
        }
    }

    private async Task<Guid?> GetDocumentIdAsync(string taskId, IUnitOfWork uow)
    {
        if (_taskToDocumentId.TryGetValue(taskId, out var cachedId)) return cachedId;

        var job = await uow.DocumentJobs.GetByOcrJobIdAsync(taskId);
        if (job != null)
        {
            _taskToDocumentId[taskId] = job.DocumentId;
            return job.DocumentId;
        }
        return null;
    }

    private async Task HandleLoggingEventAsync(Guid docId, OcrLoggingEvent? eventBase, IUnitOfWork uow)
    {
        if (eventBase == null)
        {
            _logger.LogWarning("HandleLoggingEventAsync received null eventBase for DocumentId: {DocId}", docId);
            return;
        }

        var mappedStatus = eventBase.Status.ToLower() switch
        {
            "started" => "Started",
            "succeeded" => "Succeeded",
            "failed" => "Failed",
            "canceled" => "Canceled",
            _ => StatusDocument.ProcessingOcr.ToString()
        };

        if (eventBase.Status.Equals("Started", StringComparison.OrdinalIgnoreCase))
        {
            await InitJobAsync(docId, uow);
        }
        else if (eventBase.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("OCR processing completed for DocumentId: {DocId}, waiting for SaveLog and GetMarkdown events", docId);
        }
        else if (eventBase.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                 eventBase.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("OCR {Status} for DocumentId: {DocId}, TaskId: {TaskId}. Message: {Message}",
                eventBase.Status, docId, eventBase.TaskId, eventBase.Message);
            await FinalizeJobStatusAsync(docId, mappedStatus, uow);
            _taskToDocumentId.TryRemove(eventBase.TaskId, out _);
        }

        await BroadcastProgressAsync(docId, eventBase.Message, mappedStatus, eventBase.ProcessingTime);
    }

    private async Task InitJobAsync(Guid docId, IUnitOfWork uow)
    {
        var doc = await uow.Documents.GetByIdAsync(docId);
        var job = await uow.DocumentJobs.GetByDocumentIdAsync(docId);

        if (doc != null)
        {
            doc.Status = StatusDocument.ProcessingOcr;
            uow.Documents.Update(doc);
        }

        if (job != null)
        {
            job.StatusOcr = StatusJob.Processing;
            uow.DocumentJobs.Update(job);
        }

        await uow.SaveChangesAsync();
        _logger.LogInformation("Job initialized for DocumentId: {DocId}", docId);
    }

    private async Task FinalizeJobStatusAsync(Guid docId, string status, IUnitOfWork uow)
    {
        var doc = await uow.Documents.GetByIdAsync(docId);
        var job = await uow.DocumentJobs.GetByDocumentIdAsync(docId);

        var finalStatusDesc = status.ToLower();
        var statusDoc = finalStatusDesc == "canceled" ? StatusDocument.Canceled : StatusDocument.Failed;
        var statusJob = finalStatusDesc == "canceled" ? StatusJob.Canceled : StatusJob.Failed;

        if (doc != null)
        {
            doc.Status = statusDoc;
            uow.Documents.Update(doc);
        }

        if (job != null)
        {
            job.StatusOcr = statusJob;
            uow.DocumentJobs.Update(job);
        }

        await uow.SaveChangesAsync();
    }

    private async Task HandleGetMarkdownEventAsync(Guid docId, OcrGetMarkdownEvent? eventBase, IServiceScope scope)
    {
        if (eventBase == null) return;

        _logger.LogInformation("Handling GetMarkdown for DocumentId: {DocId}, TaskId: {TaskId}, MarkdownUrl: {MarkdownUrl}",
            docId, eventBase.TaskId, eventBase.MarkdownUrl ?? "N/A");

        var ocrService = scope.ServiceProvider.GetRequiredService<IOCRService>();
        var s3Service = scope.ServiceProvider.GetRequiredService<IS3Service>();
        var markdownService = scope.ServiceProvider.GetRequiredService<IMarkdownService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        try
        {
            _logger.LogInformation("Fetching OCR result from OCR service for TaskId: {TaskId} (ONE-TIME CALL)", eventBase.TaskId);
            var ocrResponse = await ocrService.GetMarkdownAsync(eventBase.TaskId);

            if (ocrResponse == null || ocrResponse.Pages == null || ocrResponse.Pages.Count == 0)
            {
                throw new InvalidOperationException("OCR response is null or contains no pages");
            }

            _logger.LogInformation("OCR response contains {Count} pages with {ImageCount} total images for TaskId: {TaskId}",
                ocrResponse.Pages.Count,
                ocrResponse.Pages.Sum(p => p.Images.Count),
                eventBase.TaskId);

            var transformedMarkdown = await markdownService.TransformPagesImagesToMinioLinkAsync(
                string.Empty,
                docId.ToString(),
                ocrResponse.Pages,
                _uploadSemaphore);

            var doc = await uow.Documents.GetByIdAsync(docId);
            if (doc != null)
            {
                doc.OcrContent = transformedMarkdown;
                doc.Status = StatusDocument.Succeeded;
                doc.IsOcred = true;
                doc.SummaryContent = null;
                doc.QaSummaryContent = null;
                uow.Documents.Update(doc);

                var job = await uow.DocumentJobs.GetByDocumentIdAsync(docId);
                if (job != null)
                {
                    job.StatusOcr = StatusJob.Succeeded;
                    uow.DocumentJobs.Update(job);
                }

                await uow.SaveChangesAsync();
                _logger.LogInformation("Saved OCR markdown to DB (length: {Length}) for DocumentId: {DocId}",
                    transformedMarkdown.Length, docId);
                await BroadcastProgressAsync(docId, "OCR processed successfully", "Succeeded", eventBase.ProcessingTime);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRITICAL: Failed to process OCR markdown after downloading (file already deleted on server). DocumentId: {DocId}, TaskId: {TaskId}",
                docId, eventBase.TaskId);

            var doc = await uow.Documents.GetByIdAsync(docId);
            if (doc != null)
            {
                doc.Status = StatusDocument.Failed;
                uow.Documents.Update(doc);

                var job = await uow.DocumentJobs.GetByDocumentIdAsync(docId);
                if (job != null)
                {
                    job.StatusOcr = StatusJob.Failed;
                    uow.DocumentJobs.Update(job);
                }

                await uow.SaveChangesAsync();
            }

            await _broadcaster.PublishAsync("ocr", new NotificationMessage
            {
                DocumentId = docId,
                Message = $"Failed to process OCR result: {ex.Message}",
                Status = "Failed",
                Stage = "OCR"
            });

            throw;
        }
        finally
        {
            _taskToDocumentId.TryRemove(eventBase.TaskId, out _);
        }
    }

    private async Task HandleSaveLogEventAsync(Guid docId, OcrSaveLogEvent? eventBase, IUnitOfWork uow)
    {
        if (eventBase == null) return;

        if (string.IsNullOrEmpty(eventBase.DataJson))
        {
            _logger.LogWarning("SaveLog event has empty DataJson for TaskId: {TaskId}", eventBase.TaskId);
            return;
        }

        if (eventBase.Data == null || eventBase.Data.Count == 0)
        {
            _logger.LogWarning("SaveLog event DataJson parsed to null/empty for TaskId: {TaskId}", eventBase.TaskId);
            return;
        }

        try
        {
            var logMessage = await uow.LogMessages.GetByDocumentIdAsync(docId);
            if (logMessage == null)
            {
                logMessage = new LogMessage { DocumentId = docId, LogsOcr = eventBase.Data };
                await uow.LogMessages.AddAsync(logMessage);
            }
            else
            {
                logMessage.LogsOcr = eventBase.Data;
                uow.LogMessages.Update(logMessage);
            }
            await uow.SaveChangesAsync();
            _logger.LogInformation("Updated LogMessage with {Count} events for DocumentId: {DocId}",
                eventBase.Data.Count, docId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save LogEvents for TaskId: {TaskId}", eventBase.TaskId);
        }
    }

    private async Task BroadcastProgressAsync(Guid docId, string message, string status, double? processingTime = null)
    {
        await _broadcaster.PublishAsync("ocr", new NotificationMessage
        {
            DocumentId = docId,
            Message = message,
            Status = status,
            ProcessingTime = processingTime,
            Stage = "OCR",
            Timestamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
        });
    }

    private async Task CleanupCachePeriodicallyAsync(CancellationToken ct)
    {
        const int CleanupIntervalMinutes = 5;

        // Local cache is no longer used. All data is stored in S3.
        _logger.LogInformation("Cache cleanup skipped - local cache is deprecated.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(CleanupIntervalMinutes), ct);
            }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _uploadSemaphore?.Dispose();

        foreach (var queue in _documentQueues.Values)
        {
            queue.Complete();
        }
        _documentQueues.Clear();

        var workers = _documentWorkers.Values.ToList();
        if (workers.Count > 0)
        {
            try
            {
                await Task.WhenAll(workers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for document workers to finish during shutdown");
            }
        }
        _documentWorkers.Clear();

        await Task.CompletedTask;
    }
}
