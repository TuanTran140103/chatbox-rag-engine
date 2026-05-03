using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IAccessShareRepository : IGenericRepository<AccessShare>
{
    Task<AccessShare?> GetByTargetAndDatasetAsync(Guid datasetId, Guid? datasetItemId, Guid? userId, Guid? ouId);
    Task<IEnumerable<AccessShare>> GetByDatasetAsync(Guid datasetId);
    Task<IEnumerable<AccessShare>> GetByDatasetItemAsync(Guid datasetItemId);
    Task<IEnumerable<AccessShare>> GetByUserAsync(Guid userId);
    Task<IEnumerable<AccessShare>> GetByOUAsync(Guid ouId);
}
