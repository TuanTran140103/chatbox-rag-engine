using MarkdownGenQAs.Application.Interfaces.Repository;
using Microsoft.EntityFrameworkCore.Storage;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationContext _context;
    private IDbContextTransaction? _transaction;

    public IDocumentRepository Documents { get; }
    public IDocumentJobRepository DocumentJobs { get; }
    public ILogMessageRepository LogMessages { get; }
    public IDatasetRepository Datasets { get; }
    public IDatasetItemRepository DatasetItems { get; }
    public IAccessShareRepository AccessShares { get; }
    public IOrganizationUnitRepository OrganizationUnits { get; }
    public IUserPositionRepository UserPositions { get; }

    public UnitOfWork(
        ApplicationContext context,
        IDocumentRepository documents,
        IDocumentJobRepository documentJobs,
        ILogMessageRepository logMessages,
        IDatasetRepository datasets,
        IDatasetItemRepository datasetItems,
        IAccessShareRepository accessShares,
        IOrganizationUnitRepository organizationUnits,
        IUserPositionRepository userPositions)
    {
        _context = context;
        Documents = documents;
        DocumentJobs = documentJobs;
        LogMessages = logMessages;
        Datasets = datasets;
        DatasetItems = datasetItems;
        AccessShares = accessShares;
        OrganizationUnits = organizationUnits;
        UserPositions = userPositions;
    }

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public async Task BeginTransactionAsync()
    {
        _transaction = await _context.Database.BeginTransactionAsync();
    }

    public async Task CommitTransactionAsync()
    {
        if (_transaction != null)
        {
            await _transaction.CommitAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackTransactionAsync()
    {
        if (_transaction != null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _transaction?.Dispose();
    }
}
