using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class DatasetItemRepository : GenericRepository<DatasetItem>, IDatasetItemRepository
{
    public DatasetItemRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<IEnumerable<DatasetItem>> GetChildrenAsync(Guid datasetId, Guid? parentId)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(i => i.DatasetId == datasetId && i.ParentId == parentId)
            .OrderBy(i => i.ItemType)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<DatasetItem>> GetByPathAsync(string path)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(i => EF.Functions.ILike(i.Path, $"{path}%"))
            .OrderBy(i => i.Level)
            .ThenBy(i => i.SortOrder)
            .ToListAsync();
    }

    public async Task<bool> HasChildrenAsync(Guid parentId)
    {
        return await _dbSet.AnyAsync(i => i.ParentId == parentId);
    }

    public async Task<DatasetItem?> GetByDocumentIdAsync(Guid documentId)
    {
        return await _dbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.DocumentId == documentId);
    }
}