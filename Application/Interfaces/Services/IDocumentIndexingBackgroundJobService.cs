using Hangfire.Server;

namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IDocumentIndexingBackgroundJobService
{
    Task ProcessIndexing(Guid documentId, CancellationToken cancellationToken, PerformContext? context = null);
}
