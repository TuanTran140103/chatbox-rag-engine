using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class DocumentJobRepository : GenericRepository<DocumentJob>, IDocumentJobRepository
{
    public DocumentJobRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<DocumentJob?> GetByOcrJobIdAsync(string ocrJobId)
    {
        return await _dbSet
            .Include(j => j.Document)
            .FirstOrDefaultAsync(j => j.OcrJobId == ocrJobId);
    }

    public async Task<DocumentJob?> GetByGenQaJobIdAsync(string genQaJobId)
    {
        return await _dbSet
            .Include(j => j.Document)
            .FirstOrDefaultAsync(j => j.GenQaJobId == genQaJobId);
    }

    public async Task<DocumentJob?> GetByDocumentIdAsync(Guid documentId)
    {
        return await _dbSet
            .Include(j => j.Document)
            .FirstOrDefaultAsync(j => j.DocumentId == documentId);
    }

    public async Task<IEnumerable<DocumentJob>> GetJobsByStatusAsync(StatusJob status, bool isOcr = true)
    {
        if (isOcr)
        {
            return await _dbSet.AsNoTracking().Where(j => j.StatusOcr == status).ToListAsync();
        }
        else
        {
            return await _dbSet.AsNoTracking().Where(j => j.StatusGenQa == status).ToListAsync();
        }
    }
}
