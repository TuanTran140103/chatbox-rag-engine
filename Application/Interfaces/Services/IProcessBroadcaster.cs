using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Application.Interfaces.Services;
public interface IProcessBroadcaster
{
    ValueTask PublishAsync(string processType, NotificationMessage message);
    IAsyncEnumerable<NotificationMessage> SubscribeAsync(string processType, Guid documentId, CancellationToken ct);
    Task<List<NotificationMessage>> ReadHistoryAsync(string processType, Guid documentId);
    Task ClearHistoryAsync(string processType, Guid documentId);
}