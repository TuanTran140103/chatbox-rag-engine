using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IUserPositionRepository : IGenericRepository<UserPosition>
{
    Task<IEnumerable<UserPosition>> GetByUserAsync(Guid userId);
    Task<IEnumerable<UserPosition>> GetByOUAsync(Guid ouId);
    Task<IEnumerable<UserPosition>> GetByOUIdsAsync(IEnumerable<Guid> ouIds);
    Task<UserPosition?> GetPrimaryPositionAsync(Guid userId);
    Task<IEnumerable<Guid>> GetUserOUIdsAsync(Guid userId);
    Task<Dictionary<Guid, int>> GetCountsByOUAsync();
}
