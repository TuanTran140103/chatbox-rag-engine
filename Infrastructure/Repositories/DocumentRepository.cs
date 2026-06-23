using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class DocumentRepository : GenericRepository<Document>, IDocumentRepository
{
    public DocumentRepository(ApplicationContext context) : base(context)
    {
    }

    public async Task<IEnumerable<Document>> SearchByFileNameInDatasetsAsync(string fileName, IReadOnlyCollection<Guid> datasetIds)
    {
        return await _dbSet.AsNoTracking()
            .Where(d => d.DatasetItem != null && datasetIds.Contains(d.DatasetItem.DatasetId) &&
                (EF.Functions.TrigramsAreSimilar(d.FileName, fileName) ||
                 EF.Functions.ILike(d.FileName, $"%{fileName}%")))
            .OrderBy(d => EF.Functions.TrigramsSimilarityDistance(d.FileName, fileName))
            .Take(20)
            .ToListAsync();
    }

    public async Task<IEnumerable<Document>> GetByStatusAsync(StatusDocument status)
    {
        return await _dbSet.AsNoTracking().Where(d => d.Status == status).ToListAsync();
    }

    public async Task<IEnumerable<Document>> GetByDatasetItemAsync(Guid datasetItemId)
    {
        return await _dbSet.AsNoTracking().Where(d => d.DatasetItemId == datasetItemId).ToListAsync();
    }

    public async Task<Document?> GetWithDetailsAsync(Guid id)
    {
        return await _dbSet
            .Include(d => d.DatasetItem)
            .Include(d => d.LogMessage)
            .Include(d => d.DocumentJob)
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<IEnumerable<Document>> GetPendingOcrAsync()
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => d.Status == StatusDocument.Uploaded && !d.IsOcred)
            .ToListAsync();
    }

    public async Task<IEnumerable<Document>> GetPendingIndexingAsync()
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => d.Status == StatusDocument.Succeeded && d.IsOcred && !d.IsIndexed)
            .ToListAsync();
    }

    public async Task<IEnumerable<Document>> SearchByFileNameAsync(string fileName)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(d => EF.Functions.TrigramsAreSimilar(d.FileName, fileName) || EF.Functions.ILike(d.FileName, $"%{fileName}%"))
            .OrderBy(d => EF.Functions.TrigramsSimilarityDistance(d.FileName, fileName))
            .ToListAsync();
    }

    public async Task<IEnumerable<Document>> GetPagedAsync(DateTime? lastCreatedAt, Guid? lastId, int pageSize, Guid? userId = null)
    {
        var query = _dbSet.AsNoTracking().AsQueryable();

        if (userId.HasValue)
        {
            query = query.Where(d => d.UserId == userId.Value);
        }

        if (lastCreatedAt.HasValue && lastId.HasValue)
        {
            query = query.Where(d => d.CreatedAt < lastCreatedAt.Value ||
                                    (d.CreatedAt == lastCreatedAt.Value && d.Id.CompareTo(lastId.Value) < 0));
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<Document>> GetPagedByUpdatedAtAsync(DateTime? lastUpdatedAt, Guid? lastId, int pageSize, Guid? userId = null)
    {
        var query = _dbSet.AsNoTracking().AsQueryable();

        if (userId.HasValue)
        {
            query = query.Where(d => d.UserId == userId.Value);
        }

        if (lastUpdatedAt.HasValue && lastId.HasValue)
        {
            query = query.Where(d => d.UpdatedAt < lastUpdatedAt.Value ||
                                    (d.UpdatedAt == lastUpdatedAt.Value && d.Id.CompareTo(lastId.Value) < 0));
        }

        return await query
            .OrderByDescending(d => d.Status == StatusDocument.ProcessingOcr || d.Status == StatusDocument.ProcessingIndexing)
            .ThenByDescending(d => d.UpdatedAt)
            .ThenByDescending(d => d.Id)
            .Take(pageSize)
            .ToListAsync();
    }
}
