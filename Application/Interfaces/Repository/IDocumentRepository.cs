using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IDocumentRepository : IGenericRepository<Document>
{
    Task<IEnumerable<Document>> SearchByFileNameInDatasetsAsync(string fileName, IReadOnlyCollection<Guid> datasetIds);
    Task<IEnumerable<Document>> GetByStatusAsync(StatusDocument status);
    Task<IEnumerable<Document>> GetByDatasetItemAsync(Guid datasetItemId);
    Task<Document?> GetWithDetailsAsync(Guid id);
    Task<IEnumerable<Document>> GetPendingOcrAsync();
    Task<IEnumerable<Document>> GetPendingIndexingAsync();
    Task<IEnumerable<Document>> SearchByFileNameAsync(string fileName);
    Task<IEnumerable<Document>> GetPagedAsync(DateTime? lastCreatedAt, Guid? lastId, int pageSize, Guid? userId = null);
    Task<IEnumerable<Document>> GetPagedByUpdatedAtAsync(DateTime? lastUpdatedAt, Guid? lastId, int pageSize, Guid? userId = null);
}
