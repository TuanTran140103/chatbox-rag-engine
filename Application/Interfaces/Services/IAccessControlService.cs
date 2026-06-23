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

    Task<bool> CanViewDocumentAsync(Guid userId, Guid documentId);
    Task<bool> CanWriteDocumentAsync(Guid userId, Guid documentId);

    Task<bool> IsAdminAsync(Guid userId);

    Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, Dataset dataset);
    Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, DatasetItem datasetItem);

    Task<List<Guid>> GetAccessibleDatasetIdsAsync(Guid userId, bool includeDeleted = false);
    Task<List<Guid>> GetAccessibleDocumentIdsAsync(Guid userId);
}