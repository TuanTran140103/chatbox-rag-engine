using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class LogMessageRepository : GenericRepository<LogMessage>, ILogMessageRepository
{
    public LogMessageRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<LogMessage?> GetByDocumentIdAsync(Guid documentId)
    {
        return await _dbSet
            .Include(l => l.Document)
            .FirstOrDefaultAsync(l => l.DocumentId == documentId);
    }
}
