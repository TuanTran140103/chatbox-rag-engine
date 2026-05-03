using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class OrganizationUnitRepository : GenericRepository<OrganizationUnit>, IOrganizationUnitRepository
{
    public OrganizationUnitRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<OrganizationUnit?> GetByPathAsync(string path)
    {
        return await _context.OrganizationUnits
            .FirstOrDefaultAsync(ou => ou.Path == path);
    }

    public async Task<IEnumerable<OrganizationUnit>> GetChildrenAsync(Guid parentId)
    {
        return await _context.OrganizationUnits
            .Where(ou => ou.ParentId == parentId)
            .ToListAsync();
    }

    public async Task<IEnumerable<OrganizationUnit>> GetAncestorsAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return Enumerable.Empty<OrganizationUnit>();

        var ouIds = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        return await _context.OrganizationUnits
            .Where(ou => ouIds.Contains(ou.Id))
            .ToListAsync();
    }
}
