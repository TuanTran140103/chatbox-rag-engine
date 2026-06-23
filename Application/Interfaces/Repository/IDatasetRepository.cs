using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IDatasetRepository : IGenericRepository<Dataset>
{
    Task<Dataset?> GetByIdWithPermissionsAsync(Guid id);
    Task<IEnumerable<Dataset>> GetByOwnerIdAsync(Guid ownerId);
    Task<IEnumerable<Dataset>> SearchByNameAsync(string name);
    Task<Dictionary<Guid, int>> GetCountsByDepartmentAsync();
}