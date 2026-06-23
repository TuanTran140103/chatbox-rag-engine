using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Application.Service;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;
using StackExchange.Redis;

namespace MarkdownGenQAs.Infrastructure.Services;

public class SystemInitializationService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemInitializationService> _logger;
    private readonly IConnectionMultiplexer _redis;
    private const string OcrStreamKey = "ocr:events:stream";
    private const string OcrConsumerGroup = "markdowngenqas-group";

    public SystemInitializationService(
        IServiceProvider serviceProvider,
        ILogger<SystemInitializationService> logger,
        IConnectionMultiplexer redis)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _redis = redis;
    }

    private Distance ParseDistance(string? distance) => distance switch
    {
        "Euclid" => Distance.Euclid,
        "Dot" => Distance.Dot,
        _ => Distance.Cosine
    };

    private ShardingMethod? ParseShardingMethod(string? method) => method switch
    {
        "Custom" => ShardingMethod.Custom,
        "Auto" => ShardingMethod.Auto,
        _ => null
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SystemInitialization] Starting system initialization on app startup...");

        using (var scope = _serviceProvider.CreateScope())
        {
            try
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationContext>();

                await context.Database.MigrateAsync(cancellationToken);
                _logger.LogInformation("[SystemInitialization] Database migrations applied.");

                var concurrencyService = scope.ServiceProvider.GetRequiredService<IConcurrencyService>();
                await concurrencyService.ClearAllModelsAsync();
                await concurrencyService.ClearAllStreamsAsync();
                _logger.LogInformation("[SystemInitialization] Cleanup successful.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Fatal error during initialization.");
                throw;
            }

            await InitializeQdrantCollectionsAsync(scope);

            try
            {
                var templateSeeder = scope.ServiceProvider.GetRequiredService<TemplateMetadataSeeder>();
                await templateSeeder.SeedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Error during template seeding.");
            }

            try
            {
                await ResetOcrStreamAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Error during Redis OCR stream reset.");
            }

            try
            {
                var ocrRecovery = scope.ServiceProvider.GetRequiredService<OcrRecoveryService>();
                var ocrResult = await ocrRecovery.RecoverOcrJobsAsync(cancellationToken);
                _logger.LogInformation(
                    "[SystemInitialization] OCR recovery: processing={ProcFound} (resubmitted={ProcResub}, failed={ProcFail}), pending={PendFound} (resubmitted={PendResub}, skipped={PendSkip}, failed={PendFail})",
                    ocrResult.ProcessingFound, ocrResult.ProcessingResubmitted, ocrResult.ProcessingFailed,
                    ocrResult.PendingFound, ocrResult.PendingResubmitted, ocrResult.PendingSkipped, ocrResult.PendingFailed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Error during OCR job recovery.");
            }

            try
            {
                var documentService = scope.ServiceProvider.GetRequiredService<DocumentService>();
                var recoveryResult = await documentService.RecoverStuckIndexingJobsAsync();

                if (recoveryResult.IsSuccess)
                {
                    _logger.LogInformation("Indexing recovery completed. Processed {Count} documents.", recoveryResult.Data);
                }
                else
                {
                    _logger.LogWarning("Indexing recovery issue: {Message}", recoveryResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Indexing job recovery.");
            }

            try
            {
                var minioAdmin = scope.ServiceProvider.GetRequiredService<IMinioAdminService>();
                await minioAdmin.EnsureOcrUserAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Error during MinIO OCR user setup.");
            }

            _logger.LogInformation("[SystemInitialization] System initialization completed successfully.");
        }
    }

    private async Task InitializeQdrantCollectionsAsync(IServiceScope scope)
    {
        var qdrantService = scope.ServiceProvider.GetRequiredService<IQdrantService>();
        var qdrantOptions = scope.ServiceProvider.GetRequiredService<IOptions<QdrantOptions>>().Value;

        var collectionName = "documents";

        if (!await qdrantService.CollectionExistsAsync(collectionName))
        {
            var distance = ParseDistance(qdrantOptions.Embedding.Distance);
            var shardingMethod = ParseShardingMethod(qdrantOptions.DefaultCollection.ShardingMethod);

            await qdrantService.CreateCollectionAsync(
                collectionName,
                new VectorParams
                {
                    Size = (ulong)qdrantOptions.Embedding.Dimension,
                    Distance = distance
                },
                shardNumber: qdrantOptions.DefaultCollection.ShardNumber,
                replicationFactor: qdrantOptions.DefaultCollection.ReplicationFactor,
                writeConsistencyFactor: qdrantOptions.DefaultCollection.WriteConsistencyFactor,
                onDiskPayload: qdrantOptions.DefaultCollection.OnDiskPayload,
                shardingMethod: shardingMethod);

            _logger.LogInformation(
                "[Qdrant] Collection '{CollectionName}' created: dim={Dimension}, distance={Distance}, shards={Shards}, sharding={ShardingMethod}",
                collectionName,
                qdrantOptions.Embedding.Dimension,
                distance,
                qdrantOptions.DefaultCollection.ShardNumber,
                qdrantOptions.DefaultCollection.ShardingMethod ?? "default");
        }
        else
        {
            _logger.LogInformation("[Qdrant] Collection '{CollectionName}' already exists.", collectionName);
        }

        try
        {
            await qdrantService.CreatePayloadIndexAsync(collectionName, "documentId", PayloadSchemaType.Keyword);
            _logger.LogInformation("[Qdrant] Payload index for 'documentId' created.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Qdrant] Failed to create payload index for 'documentId' (may already exist)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ResetOcrStreamAsync()
    {
        var db = _redis.GetDatabase();

        try
        {
            await db.KeyDeleteAsync(OcrStreamKey);
            _logger.LogInformation("[SystemInitialization] Deleted Redis stream {StreamKey} for clean restart.", OcrStreamKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SystemInitialization] Failed to delete stream {StreamKey}.", OcrStreamKey);
        }

        try
        {
            await db.StreamCreateConsumerGroupAsync(OcrStreamKey, OcrConsumerGroup, "$", true);
            _logger.LogInformation("[SystemInitialization] Recreated consumer group {Group} on {StreamKey}.", OcrConsumerGroup, OcrStreamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP") || ex.Message.Contains("already exists"))
        {
            _logger.LogInformation("[SystemInitialization] Consumer group {Group} already exists.", OcrConsumerGroup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SystemInitialization] Failed to recreate consumer group.");
        }
    }
}