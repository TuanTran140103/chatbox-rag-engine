using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IDocumentJobRepository : IGenericRepository<DocumentJob>
{
    Task<DocumentJob?> GetByOcrJobIdAsync(string ocrJobId);
    Task<DocumentJob?> GetByIndexingJobIdAsync(string indexingJobId);
    Task<DocumentJob?> GetByDocumentIdAsync(Guid documentId);
    Task<IEnumerable<DocumentJob>> GetJobsByStatusAsync(StatusJob status, bool isOcr = true);
}
