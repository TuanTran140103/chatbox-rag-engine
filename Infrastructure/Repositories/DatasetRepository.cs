using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class DatasetRepository : GenericRepository<Dataset>, IDatasetRepository
{
    public DatasetRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<Dataset?> GetByIdWithPermissionsAsync(Guid id)
    {
        return await _dbSet
            .AsNoTracking()
            .Include(d => d.AccessShares)
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<IEnumerable<Dataset>> GetByOwnerIdAsync(Guid ownerUserId)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Dataset>> SearchByNameAsync(string name)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => EF.Functions.TrigramsAreSimilar(d.Name, name) || EF.Functions.ILike(d.Name, $"%{name}%"))
            .OrderBy(d => EF.Functions.TrigramsSimilarityDistance(d.Name, name))
            .ToListAsync();
    }

    public async Task<Dictionary<Guid, int>> GetCountsByDepartmentAsync()
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => d.DepartmentId.HasValue)
            .GroupBy(d => d.DepartmentId!.Value)
            .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DepartmentId, x => x.Count);
    }
}
