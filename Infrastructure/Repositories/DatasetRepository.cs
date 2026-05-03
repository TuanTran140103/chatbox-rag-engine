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

    public async Task<Dictionary<Guid, int>> GetCountsByOUAsync()
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => d.OUId.HasValue)
            .GroupBy(d => d.OUId!.Value)
            .Select(g => new { OUId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OUId, x => x.Count);
    }
}
