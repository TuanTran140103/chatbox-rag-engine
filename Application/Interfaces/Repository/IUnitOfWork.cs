using MarkdownGenQAs.Application.Interfaces.Repository;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IUnitOfWork : IDisposable
{
    IDocumentRepository Documents { get; }
    IDocumentJobRepository DocumentJobs { get; }
    ILogMessageRepository LogMessages { get; }
    IDatasetRepository Datasets { get; }
    IDatasetItemRepository DatasetItems { get; }
    IAccessShareRepository AccessShares { get; }
    IOrganizationUnitRepository OrganizationUnits { get; }
    IUserPositionRepository UserPositions { get; }

    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}
