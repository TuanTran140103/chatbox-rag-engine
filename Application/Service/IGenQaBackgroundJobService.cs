using Hangfire.Server;

namespace MarkdownGenQAs.Application.Service;

public interface IGenQaBackgroundJobService
{
    Task ProcessGenChunkQA(Guid documentId, CancellationToken cancellationToken, PerformContext? context = null);
}
