using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IOrganizationUnitRepository : IGenericRepository<OrganizationUnit>
{
    Task<OrganizationUnit?> GetByPathAsync(string path);
    Task<IEnumerable<OrganizationUnit>> GetChildrenAsync(Guid parentId);
    Task<IEnumerable<OrganizationUnit>> GetAncestorsAsync(string path);
}
