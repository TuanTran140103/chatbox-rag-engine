using System.Text.Json;
using MarkdownGenQAs.Application.Dto.Search;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Infrastructure.Services;

internal sealed class SearchService(
    IAccessControlService accessControl,
    IUnitOfWork unitOfWork,
    IQdrantService qdrantService,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    : ISearchService
{
    private const string QdrantCollection = "documents";

    public async Task<ServiceResult<ReadDocumentResult>> ReadDocumentAsync(
        Guid userId, Guid documentId, string? contentType)
    {
        try
        {
            var accessibleDatasetIds = await GetAccessibleDatasetIdsAsync(userId);

            var document = await unitOfWork.Documents.Query
                .Include(d => d.DatasetItem)
                .FirstOrDefaultAsync(d => d.Id == documentId);

            if (document == null)
                return new ServiceResult<ReadDocumentResult>
                {
                    IsSuccess = false,
                    ErrorMessage = "Document not found"
                };

            if (document.DatasetItem == null || !accessibleDatasetIds.Contains(document.DatasetItem.DatasetId))
                return new ServiceResult<ReadDocumentResult>
                {
                    IsSuccess = false,
                    ErrorMessage = "Access denied"
                };

            return new ServiceResult<ReadDocumentResult>
            {
                IsSuccess = true,
                Data = new ReadDocumentResult
                {
                    DocumentId = document.Id,
                    DatasetId = document.DatasetItem.DatasetId,
                    FileName = document.FileName,
                    Content = ResolveContent(document, contentType),
                    ContentType = contentType
                }
            };
        }
        catch (Exception ex)
        {
            return new ServiceResult<ReadDocumentResult>
            {
                IsSuccess = false,
                ErrorMessage = $"Internal server error: {ex.Message}"
            };
        }
    }

    public async Task<ServiceResult<List<DocumentSearchItem>>> SearchDocumentsByNameAsync(
        Guid userId, string queryText, List<Guid>? datasetIds)
    {
        try
        {
            var accessibleDatasetIds = await GetAccessibleDatasetIdsAsync(userId);

            if (datasetIds?.Count > 0)
            {
                var invalidIds = datasetIds.Except(accessibleDatasetIds).ToList();
                if (invalidIds.Count != 0)
                    return new ServiceResult<List<DocumentSearchItem>>
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Access denied to datasets: {string.Join(", ", invalidIds)}"
                    };
                accessibleDatasetIds = datasetIds;
            }

            if (accessibleDatasetIds.Count == 0)
                return new ServiceResult<List<DocumentSearchItem>> { IsSuccess = true, Data = [] };

            var documents = await unitOfWork.Documents
                .SearchByFileNameInDatasetsAsync(queryText, accessibleDatasetIds);

            var items = documents.Select(d => new DocumentSearchItem
            {
                DocumentId = d.Id,
                DatasetId = d.DatasetItem!.DatasetId,
                FileName = d.FileName
            }).ToList();

            return new ServiceResult<List<DocumentSearchItem>>
            {
                IsSuccess = true,
                Data = items
            };
        }
        catch (Exception ex)
        {
            return new ServiceResult<List<DocumentSearchItem>>
            {
                IsSuccess = false,
                ErrorMessage = $"Internal server error: {ex.Message}"
            };
        }
    }

    public async Task<ServiceResult<List<VectorSearchItem>>> VectorSearchAsync(
        Guid userId, VectorSearchRequest request)
    {
        try
        {
            var accessibleDatasetIds = await GetAccessibleDatasetIdsAsync(userId);

            if (request.DatasetIds?.Count > 0)
            {
                var invalidIds = request.DatasetIds.Except(accessibleDatasetIds).ToList();
                if (invalidIds.Count != 0)
                    return new ServiceResult<List<VectorSearchItem>>
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Access denied to datasets: {string.Join(", ", invalidIds)}"
                    };
                accessibleDatasetIds = request.DatasetIds;
            }

            if (accessibleDatasetIds.Count == 0)
                return new ServiceResult<List<VectorSearchItem>> { IsSuccess = true, Data = [] };

            var embeddings = await embeddingGenerator.GenerateAsync([request.QueryText]);
            if (embeddings.Count == 0)
                return new ServiceResult<List<VectorSearchItem>> { IsSuccess = true, Data = [] };

            var vector = embeddings[0].Vector.ToArray();

            var conditions = new List<Condition>();
            if (request.MetadataFilter?.Count > 0)
            {
                foreach (var (key, jsonElem) in request.MetadataFilter)
                    conditions.Add(BuildMetadataCondition(key, jsonElem));
            }

            Filter? filter = conditions.Count > 0 ? new Filter { Must = { conditions } } : null;

            var shardKey = new ShardKeySelector();
            shardKey.ShardKeys.AddRange(
                accessibleDatasetIds.Select(id => (ShardKey)id.ToString()));

            var scoredPoints = await qdrantService.SearchAsync(
                QdrantCollection, vector, filter,
                limit: (ulong)request.TopK,
                scoreThreshold: request.ScoreThreshold,
                shardKeySelector: shardKey);

            var seenDocIds = new HashSet<Guid>();
            var items = new List<VectorSearchItem>();
            foreach (var point in scoredPoints)
            {
                var docId = TryGetPayloadGuid(point.Payload, "documentId");
                if (docId == null || !seenDocIds.Add(docId.Value))
                    continue;

                var datasetId = TryGetPayloadGuid(point.Payload, "datasetId") ?? Guid.Empty;
                var chunkType = TryGetPayloadString(point.Payload, "chunk_type");
                var content = TryGetPayloadString(point.Payload, "content");
                var contentFull = TryGetPayloadString(point.Payload, "contentFullForSummary");
                var resolvedContent = chunkType == "summary" && contentFull != null ? contentFull : content;

                items.Add(new VectorSearchItem
                {
                    DocumentId = docId.Value,
                    DatasetId = datasetId,
                    FileName = string.Empty,
                    Content = resolvedContent,
                    ChunkType = chunkType,
                    Score = point.Score
                });
            }

            if (items.Count > 0)
                await EnrichFileNamesAsync(items);

            return new ServiceResult<List<VectorSearchItem>> { IsSuccess = true, Data = items };
        }
        catch (Exception ex)
        {
            return new ServiceResult<List<VectorSearchItem>>
            {
                IsSuccess = false,
                ErrorMessage = $"Internal server error: {ex.Message}"
            };
        }
    }

    public async Task<ServiceResult<List<DocumentSearchItem>>> ListDatasetDocumentsAsync(
        Guid userId, Guid datasetId)
    {
        try
        {
            var accessibleDatasetIds = await GetAccessibleDatasetIdsAsync(userId);
            if (!accessibleDatasetIds.Contains(datasetId))
                return new ServiceResult<List<DocumentSearchItem>>
                {
                    IsSuccess = false,
                    ErrorMessage = "Dataset not found"
                };

            var datasetItems = await unitOfWork.DatasetItems
                .FindAsync(di => di.DatasetId == datasetId && di.DocumentId != null);

            var docIds = datasetItems.Select(di => di.DocumentId!.Value).ToList();
            if (docIds.Count == 0)
                return new ServiceResult<List<DocumentSearchItem>> { IsSuccess = true, Data = [] };

            var documents = await unitOfWork.Documents.FindAsync(d => docIds.Contains(d.Id));

            var items = documents.Select(d => new DocumentSearchItem
            {
                DocumentId = d.Id,
                DatasetId = datasetId,
                FileName = d.FileName
            }).ToList();

            return new ServiceResult<List<DocumentSearchItem>> { IsSuccess = true, Data = items };
        }
        catch (Exception ex)
        {
            return new ServiceResult<List<DocumentSearchItem>>
            {
                IsSuccess = false,
                ErrorMessage = $"Internal server error: {ex.Message}"
            };
        }
    }

    private async Task EnrichFileNamesAsync(List<VectorSearchItem> items)
    {
        var docIds = items.Select(i => i.DocumentId).Distinct().ToList();
        var documents = await unitOfWork.Documents.FindAsync(d => docIds.Contains(d.Id));
        var docMap = documents.ToDictionary(d => d.Id, d => d.FileName);

        foreach (var item in items)
        {
            if (docMap.TryGetValue(item.DocumentId, out var fileName))
                item.FileName = fileName;
        }
    }

    private async Task<List<Guid>> GetAccessibleDatasetIdsAsync(Guid userId)
    {
        var isAdmin = await accessControl.IsAdminAsync(userId);
        if (isAdmin)
        {
            var all = await unitOfWork.Datasets.GetAllAsync();
            return all.Select(d => d.Id).ToList();
        }

        return await accessControl.GetAccessibleDatasetIdsAsync(userId);
    }

    private static string? ResolveContent(Models.Entities.Document document, string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "summary" => document.SummaryContent ?? document.QaSummaryContent,
            "fullcontent" => document.OcrContent,
            _ => null
        };
    }

    private static Condition BuildMetadataCondition(string key, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => Conditions.MatchKeyword(key, value.GetString()!),
            JsonValueKind.Number when value.TryGetInt64(out var l) => Conditions.Match(key, l),
            JsonValueKind.Number => Conditions.Range(key, new Qdrant.Client.Grpc.Range { Gte = value.GetDouble(), Lte = value.GetDouble() }),
            JsonValueKind.True => Conditions.Match(key, true),
            JsonValueKind.False => Conditions.Match(key, false),
            _ => throw new ArgumentException($"Unsupported metadata filter value kind: {value.ValueKind}")
        };
    }

    private static Guid? TryGetPayloadGuid(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value))
            return null;
        return Guid.TryParse(value.StringValue, out var id) ? id : null;
    }

    private static string? TryGetPayloadString(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value))
            return null;
        return value.StringValue;
    }
}
