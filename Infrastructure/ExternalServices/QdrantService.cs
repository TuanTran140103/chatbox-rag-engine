using System.Text.Json;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Options;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.QA;
using MarkdownGenQAs.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Infrastructure.ExternalServices;

public class QdrantService : IQdrantService
{
    private sealed record DocumentPayload(
        Guid Id,
        string? ChunkContent,
        string? MetadataContent,
        string? JsonSchema);

    private readonly QdrantClient _client;
    private readonly ILogger<QdrantService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmbeddingService _embeddingService;

    public QdrantService(
        IOptions<Options.QdrantOptions> options,
        ILogger<QdrantService> logger,
        IServiceScopeFactory scopeFactory,
        IEmbeddingService embeddingService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _embeddingService = embeddingService;
        var opts = options.Value;

        var apiKey = string.IsNullOrEmpty(opts.ApiKey) ? null : opts.ApiKey;
        var timeout = TimeSpan.FromSeconds(opts.GrpcTimeoutSeconds);

        _client = !string.IsNullOrEmpty(opts.Url)
            ? new QdrantClient(new Uri(opts.Url), apiKey, timeout, loggerFactory: null)
            : new QdrantClient(opts.Host, opts.Port, opts.Https, apiKey, timeout, loggerFactory: null);
    }

    public async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        return await _client.CollectionExistsAsync(collectionName, cancellationToken);
    }

    public async Task CreateCollectionAsync(
        string collectionName,
        VectorParams vectorsConfig,
        uint shardNumber = 1,
        uint replicationFactor = 1,
        uint writeConsistencyFactor = 1,
        bool onDiskPayload = false,
        ShardingMethod? shardingMethod = null,
        CancellationToken cancellationToken = default)
    {
        await _client.CreateCollectionAsync(
            collectionName,
            vectorsConfig,
            shardNumber,
            replicationFactor,
            writeConsistencyFactor,
            onDiskPayload,
            shardingMethod: shardingMethod,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ScoredPoint>> SearchAsync(
        string collectionName,
        ReadOnlyMemory<float> vector,
        Filter? filter = null,
        ulong limit = 10,
        float? scoreThreshold = null,
        string? vectorName = null,
        ShardKeySelector? shardKeySelector = null,
        CancellationToken cancellationToken = default)
    {
        return await _client.SearchAsync(
            collectionName,
            vector,
            filter,
            limit: limit,
            scoreThreshold: scoreThreshold,
            vectorName: vectorName,
            shardKeySelector: shardKeySelector,
            cancellationToken: cancellationToken);
    }

    public async Task<UpdateResult> DeleteAsync(
        string collectionName,
        IReadOnlyList<Guid> ids,
        bool wait = true,
        ShardKeySelector? shardKeySelector = null,
        CancellationToken cancellationToken = default)
    {
        return await _client.DeleteAsync(collectionName, ids, wait, shardKeySelector: shardKeySelector, cancellationToken: cancellationToken);
    }

    public async Task DeleteCollectionAsync(
        string collectionName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await _client.DeleteCollectionAsync(collectionName, timeout, cancellationToken);
    }

    public async Task<UpdateResult> CreatePayloadIndexAsync(
        string collectionName,
        string fieldName,
        PayloadSchemaType schemaType = PayloadSchemaType.Keyword,
        PayloadIndexParams? indexParams = null,
        bool wait = true,
        CancellationToken cancellationToken = default)
    {
        return await _client.CreatePayloadIndexAsync(collectionName, fieldName, schemaType, indexParams, wait, cancellationToken: cancellationToken);
    }

    public async Task<UpdateResult> DeletePayloadIndexAsync(
        string collectionName,
        string fieldName,
        bool wait = true,
        CancellationToken cancellationToken = default)
    {
        return await _client.DeletePayloadIndexAsync(collectionName, fieldName, wait, cancellationToken: cancellationToken);
    }

    public async Task<UpdateResult> AddDocumentPointAsync(
        string collectionName,
        Guid documentId,
        Guid datasetId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var document = await uow.Documents.Query
            .Where(d => d.Id == documentId)
            .Select(d => new DocumentPayload(
                d.Id,
                d.ChunkContent,
                d.MetadataContent,
                d.DatasetItem!.Dataset!.TemplateMetadata!.JsonSchema))
            .FirstOrDefaultAsync(cancellationToken);

        if (document == null)
            throw new InvalidOperationException($"Document {documentId} not found");

        if (string.IsNullOrEmpty(document.ChunkContent))
        {
            _logger.LogWarning("Document {DocumentId} has no ChunkContent, skipping", documentId);
            return new UpdateResult { Status = UpdateStatus.Completed };
        }

        var chunks = JsonSerializer.Deserialize<List<ChunkInfo>>(document.ChunkContent);
        if (chunks == null || chunks.Count == 0)
            return new UpdateResult { Status = UpdateStatus.Completed };

        var shardKey = new ShardKeySelector
        {
            ShardKeys = { datasetId.ToString() }
        };

        // Delete existing points for this document before re-indexing
        var deleteFilter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "documentId",
                        Match = new Match { Keyword = documentId.ToString() }
                    }
                }
            }
        };
        await _client.DeleteAsync(collectionName, deleteFilter, wait: true, shardKeySelector: shardKey, cancellationToken: cancellationToken);

        var basePayload = MetadataSchemaHelper.ConvertToPayload(document.MetadataContent, document.JsonSchema);
        basePayload["documentId"] = documentId.ToString();
        basePayload["datasetId"] = datasetId.ToString();

        var pointStructs = await _embeddingService.BuildChunkPointStructsAsync(chunks, basePayload, documentId, cancellationToken);

        _logger.LogInformation("[QDRANT] Document {DocId}: {Chunks} chunks, {Points} points to upsert",
            documentId, chunks.Count, pointStructs.Count);

        var upsertResult = await _client.UpsertAsync(collectionName, pointStructs, wait: true, shardKeySelector: shardKey, cancellationToken: cancellationToken);

        _logger.LogInformation("[QDRANT] Document {DocId}: upsert {Status}, {Points} points",
            documentId, upsertResult.Status, pointStructs.Count);

        return upsertResult;
    }

    public async Task<UpdateResult> AddDocumentPointsAsync(
        string collectionName,
        IReadOnlyList<DocumentPointInput> points,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var groups = points.GroupBy(p => p.DatasetId);
        var result = new UpdateResult();

        foreach (var group in groups)
        {
            var datasetId = group.Key;
            var groupList = group.ToList();
            var docIds = groupList.Select(p => p.DocumentId).Distinct().ToList();

            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var documentPayloads = await uow.Documents.Query
                .Where(d => docIds.Contains(d.Id))
                .Select(d => new DocumentPayload(
                    d.Id,
                    d.ChunkContent,
                    d.MetadataContent,
                    d.DatasetItem!.Dataset!.TemplateMetadata!.JsonSchema))
                .ToListAsync(cancellationToken);

            var docMap = documentPayloads.ToDictionary(d => d.Id);

            var allPointStructs = new List<PointStruct>();
            foreach (var docInput in groupList)
            {
                var doc = docMap.GetValueOrDefault(docInput.DocumentId);
                if (doc == null || string.IsNullOrEmpty(doc.ChunkContent)) continue;

                var chunks = JsonSerializer.Deserialize<List<ChunkInfo>>(doc.ChunkContent);
                if (chunks == null || chunks.Count == 0) continue;

                var basePayload = MetadataSchemaHelper.ConvertToPayload(doc.MetadataContent, doc.JsonSchema);
                basePayload["documentId"] = docInput.DocumentId.ToString();
                basePayload["datasetId"] = datasetId.ToString();

                var pointStructs = await _embeddingService.BuildChunkPointStructsAsync(chunks, basePayload, docInput.DocumentId, cancellationToken);
                allPointStructs.AddRange(pointStructs);
            }

            var shardKey = new ShardKeySelector
            {
                ShardKeys = { datasetId.ToString() }
            };

            // Delete existing points for all documents in this batch
            var deleteFilter = new Filter
            {
                Should =
                {
                    docIds.Select(id => new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "documentId",
                            Match = new Match { Keyword = id.ToString() }
                        }
                    })
                }
            };
            await _client.DeleteAsync(collectionName, deleteFilter, wait: true, shardKeySelector: shardKey, cancellationToken: cancellationToken);

            _logger.LogInformation("[QDRANT] Batch dataset {Dataset}: {Points} points to upsert",
                datasetId, allPointStructs.Count);

            for (int i = 0; i < allPointStructs.Count; i += batchSize)
            {
                var batch = allPointStructs.Skip(i).Take(batchSize).ToList();
                result = await _client.UpsertAsync(collectionName, batch, wait: true, shardKeySelector: shardKey, cancellationToken: cancellationToken);
            }
        }

        return result;
    }

    public async Task<UpdateResult> UpdatePayloadByDocumentIdAsync(
        string collectionName,
        Guid documentId,
        Guid datasetId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var document = await uow.Documents.Query
            .Where(d => d.Id == documentId)
            .Select(d => new DocumentPayload(
                d.Id,
                d.ChunkContent,
                d.MetadataContent,
                d.DatasetItem!.Dataset!.TemplateMetadata!.JsonSchema))
            .FirstOrDefaultAsync(cancellationToken);

        if (document == null)
            throw new InvalidOperationException($"Document {documentId} not found");

        var payload = MetadataSchemaHelper.ConvertToPayload(document.MetadataContent, document.JsonSchema);
        payload["documentId"] = documentId.ToString();
        payload["datasetId"] = datasetId.ToString();

        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "documentId",
                        Match = new Match { Keyword = documentId.ToString() }
                    }
                }
            }
        };

        return await _client.SetPayloadAsync(collectionName, payload, filter, wait: true, cancellationToken: cancellationToken);
    }

    public async Task<UpdateResult> DeleteByFilterAsync(
        string collectionName,
        Filter filter,
        bool wait = true,
        CancellationToken cancellationToken = default)
    {
        return await _client.DeleteAsync(collectionName, filter, wait, cancellationToken: cancellationToken);
    }

    public async Task CreateShardKeyAsync(string collectionName, Guid datasetId, CancellationToken cancellationToken = default)
    {
        await _client.CreateShardKeyAsync(collectionName, new CreateShardKey
        {
            ShardKey = new ShardKey { Keyword = datasetId.ToString() }
        }, cancellationToken: cancellationToken);
    }
}
