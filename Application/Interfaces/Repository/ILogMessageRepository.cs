using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface ILogMessageRepository : IGenericRepository<LogMessage>
{
    Task<LogMessage?> GetByDocumentIdAsync(Guid documentId);
}
