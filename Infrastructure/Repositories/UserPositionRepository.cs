using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class UserPositionRepository : GenericRepository<UserPosition>, IUserPositionRepository
{
    public UserPositionRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<IEnumerable<UserPosition>> GetByUserAsync(Guid userId)
    {
        return await _context.UserPositions
            .Include(up => up.User)
            .Include(up => up.OrganizationUnit)
            .Include(up => up.Manager)
            .Where(up => up.UserId == userId)
            .ToListAsync();
    }

    public async Task<IEnumerable<UserPosition>> GetByOUAsync(Guid ouId)
    {
        return await _context.UserPositions
            .Include(up => up.User)
            .Include(up => up.Manager)
            .Where(up => up.OUId == ouId)
            .ToListAsync();
    }

    public async Task<UserPosition?> GetPrimaryPositionAsync(Guid userId)
    {
        return await _context.UserPositions
            .FirstOrDefaultAsync(up => up.UserId == userId && up.IsPrimary);
    }

    public async Task<IEnumerable<Guid>> GetUserOUIdsAsync(Guid userId)
    {
        return await _context.UserPositions
            .Where(up => up.UserId == userId)
            .Select(up => up.OUId)
            .ToListAsync();
    }

    public async Task<IEnumerable<UserPosition>> GetByOUIdsAsync(IEnumerable<Guid> ouIds)
    {
        return await _context.UserPositions
            .Include(up => up.User)
            .Include(up => up.Manager)
            .Where(up => ouIds.Contains(up.OUId))
            .ToListAsync();
    }

    public async Task<Dictionary<Guid, int>> GetCountsByOUAsync()
    {
        return await _context.UserPositions
            .AsNoTracking()
            .GroupBy(up => up.OUId)
            .Select(g => new { OUId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OUId, x => x.Count);
    }
}
