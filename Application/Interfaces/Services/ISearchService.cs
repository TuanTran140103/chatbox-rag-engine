using MarkdownGenQAs.Application.Dto.Search;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface ISearchService
{
    Task<ServiceResult<ReadDocumentResult>> ReadDocumentAsync(Guid userId, Guid documentId, string? contentType);
    Task<ServiceResult<List<DocumentSearchItem>>> SearchDocumentsByNameAsync(Guid userId, string queryText, List<Guid>? datasetIds);
    Task<ServiceResult<List<VectorSearchItem>>> VectorSearchAsync(Guid userId, VectorSearchRequest request);
    Task<ServiceResult<List<DocumentSearchItem>>> ListDatasetDocumentsAsync(Guid userId, Guid datasetId);
}
