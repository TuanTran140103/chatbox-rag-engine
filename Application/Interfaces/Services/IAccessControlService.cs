using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IAccessControlService
{
    Task<bool> CanViewDatasetAsync(Guid userId, Dataset dataset);
    Task<bool> CanWriteDatasetAsync(Guid userId, Dataset dataset);
    Task<bool> CanDeleteDatasetAsync(Guid userId, Dataset dataset);
    Task<bool> CanShareDatasetAsync(Guid userId, Dataset dataset);

    Task<bool> CanViewDatasetItemAsync(Guid userId, DatasetItem datasetItem);
    Task<bool> CanWriteDatasetItemAsync(Guid userId, DatasetItem datasetItem);

    Task<bool> IsAdminAsync(Guid userId);
    Task<bool> IsManagerOfOUAsync(Guid userId, Guid ouId);
    Task<bool> IsManagerOrAboveOfOUAsync(Guid userId, Guid ouId);
    Task<bool> IsInOUAsync(Guid userId, Guid ouId);

    Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, Dataset dataset);
    Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, DatasetItem datasetItem);

    Task<List<Guid>> GetAccessibleDatasetIdsAsync(Guid userId);
    Task<List<Guid>> GetAccessibleDocumentIdsAsync(Guid userId);
}
