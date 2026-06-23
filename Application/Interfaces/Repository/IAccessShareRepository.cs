using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IAccessShareRepository : IGenericRepository<AccessShare>
{
    Task<AccessShare?> GetByTargetAndDatasetAsync(Guid datasetId, Guid? datasetItemId, Guid? userId, Guid? departmentId);
    Task<IEnumerable<AccessShare>> GetByDatasetAsync(Guid datasetId);
    Task<IEnumerable<AccessShare>> GetByDatasetItemAsync(Guid datasetItemId);
    Task<IEnumerable<AccessShare>> GetByUserAsync(Guid userId);
    Task<IEnumerable<AccessShare>> GetByDepartmentAsync(Guid departmentId);
}
