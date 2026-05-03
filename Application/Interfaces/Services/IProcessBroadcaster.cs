using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Application.Interfaces.Services;
public interface IProcessBroadcaster
{
    ValueTask PublishAsync(string processType, NotificationMessage message);
    IAsyncEnumerable<NotificationMessage> SubscribeAsync(string processType, Guid documentId, CancellationToken ct);
    IAsyncEnumerable<NotificationMessage> SubscribeWithResumeAsync(string processType, Guid documentId, string? afterId, CancellationToken ct);
    Task ClearHistoryAsync(string processType, Guid documentId);
}