using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Application.Interfaces.ExternalServices;

public readonly record struct DocumentPointInput(Guid DocumentId, Guid DatasetId);

public interface IQdrantService
{
    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken = default);

    Task CreateCollectionAsync(
        string collectionName,
        VectorParams vectorsConfig,
        uint shardNumber = 1,
        uint replicationFactor = 1,
        uint writeConsistencyFactor = 1,
        bool onDiskPayload = false,
        ShardingMethod? shardingMethod = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScoredPoint>> SearchAsync(
        string collectionName,
        ReadOnlyMemory<float> vector,
        Filter? filter = null,
        ulong limit = 10,
        float? scoreThreshold = null,
        string? vectorName = null,
        ShardKeySelector? shardKeySelector = null,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> DeleteAsync(
        string collectionName,
        IReadOnlyList<Guid> ids,
        bool wait = true,
        ShardKeySelector? shardKeySelector = null,
        CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(
        string collectionName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> CreatePayloadIndexAsync(
        string collectionName,
        string fieldName,
        PayloadSchemaType schemaType = PayloadSchemaType.Keyword,
        PayloadIndexParams? indexParams = null,
        bool wait = true,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> DeletePayloadIndexAsync(
        string collectionName,
        string fieldName,
        bool wait = true,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> AddDocumentPointAsync(
        string collectionName,
        Guid documentId,
        Guid datasetId,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> AddDocumentPointsAsync(
        string collectionName,
        IReadOnlyList<DocumentPointInput> points,
        int batchSize = 100,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> UpdatePayloadByDocumentIdAsync(
        string collectionName,
        Guid documentId,
        Guid datasetId,
        CancellationToken cancellationToken = default);

    Task<UpdateResult> DeleteByFilterAsync(
        string collectionName,
        Filter filter,
        bool wait = true,
        CancellationToken cancellationToken = default);

    Task CreateShardKeyAsync(string collectionName, Guid datasetId, CancellationToken cancellationToken = default);
}
