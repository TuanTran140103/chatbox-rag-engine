using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class AccessShareRepository : GenericRepository<AccessShare>, IAccessShareRepository
{
    public AccessShareRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<AccessShare?> GetByTargetAndDatasetAsync(Guid datasetId, Guid? datasetItemId, Guid? userId, Guid? ouId)
    {
        var query = _context.AccessShares.Where(s => s.DatasetId == datasetId);

        if (datasetItemId.HasValue)
            query = query.Where(s => s.DatasetItemId == datasetItemId);

        if (userId.HasValue)
            query = query.Where(s => s.ShareToUserId == userId);

        if (ouId.HasValue)
            query = query.Where(s => s.ShareToOUId == ouId);

        return await query.FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<AccessShare>> GetByDatasetAsync(Guid datasetId)
    {
        return await _context.AccessShares
            .Where(s => s.DatasetId == datasetId && s.DatasetItemId == null)
            .ToListAsync();
    }

    public async Task<IEnumerable<AccessShare>> GetByDatasetItemAsync(Guid datasetItemId)
    {
        return await _context.AccessShares
            .Where(s => s.DatasetItemId == datasetItemId)
            .ToListAsync();
    }

    public async Task<IEnumerable<AccessShare>> GetByUserAsync(Guid userId)
    {
        return await _context.AccessShares
            .Where(s => s.ShareToUserId == userId)
            .ToListAsync();
    }

    public async Task<IEnumerable<AccessShare>> GetByOUAsync(Guid ouId)
    {
        return await _context.AccessShares
            .Where(s => s.ShareToOUId == ouId)
            .ToListAsync();
    }
}
