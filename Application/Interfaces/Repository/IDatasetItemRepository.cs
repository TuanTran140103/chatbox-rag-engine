using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IDatasetItemRepository : IGenericRepository<DatasetItem>
{
    Task<IEnumerable<DatasetItem>> GetChildrenAsync(Guid datasetId, Guid? parentId);
    Task<IEnumerable<DatasetItem>> GetByPathAsync(string path);
    Task<bool> HasChildrenAsync(Guid parentId);
    Task<DatasetItem?> GetByDocumentIdAsync(Guid documentId);
}