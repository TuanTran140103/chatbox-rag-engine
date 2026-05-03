using System.Runtime.CompilerServices;
using System.Text.Json;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using StackExchange.Redis;

namespace MarkdownGenQAs.Infrastructure.Services;

public class StreamBroadcaster : IProcessBroadcaster
{
    private readonly IConnectionMultiplexer _redis;

    public StreamBroadcaster(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private static RedisKey GetStreamKey(string processType, Guid documentId)
        => $"notifications:{processType}:{documentId}";

    public async ValueTask PublishAsync(string processType, NotificationMessage message)
    {
        var db = _redis.GetDatabase();
        message.ProcessType = processType;
        var payload = JsonSerializer.Serialize(message);
        var key = GetStreamKey(processType, message.DocumentId);

        var entryId = await db.StreamAddAsync(key, new NameValueEntry[]
        {
            new("data", payload)
        });

        message.EntryId = entryId;

        await db.KeyExpireAsync(key, TimeSpan.FromHours(1), CommandFlags.FireAndForget);
    }

    public async IAsyncEnumerable<NotificationMessage> SubscribeAsync(
        string processType,
        Guid documentId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var _ in SubscribeInternalAsync(processType, documentId, null, ct))
        {
            yield return _;
        }
    }

    public IAsyncEnumerable<NotificationMessage> SubscribeWithResumeAsync(
        string processType,
        Guid documentId,
        string? afterId,
        CancellationToken ct)
    {
        return SubscribeInternalAsync(processType, documentId, afterId, ct);
    }

    private async IAsyncEnumerable<NotificationMessage> SubscribeInternalAsync(
        string processType,
        Guid documentId,
        string? afterId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var key = GetStreamKey(processType, documentId);

        var start = string.IsNullOrEmpty(afterId) ? "-" : afterId;

        var entries = await db.StreamRangeAsync(key, start, "+");
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(afterId) && entry.Id.ToString() == afterId)
                continue;
            var msg = Deserialize(entry);
            if (msg != null) yield return msg;
        }

        var lastEntry = entries.LastOrDefault();
        var lastId = lastEntry.Id.IsNullOrEmpty ? "0-0" : lastEntry.Id.ToString();
        while (!ct.IsCancellationRequested)
        {
            var result = await db.StreamReadAsync(key, lastId, count: 10);
            foreach (var entry in result)
            {
                var msg = Deserialize(entry);
                if (msg != null)
                {
                    lastId = entry.Id;
                    yield return msg;
                }
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    public async Task ClearHistoryAsync(string processType, Guid documentId)
    {
        var db = _redis.GetDatabase();
        var key = GetStreamKey(processType, documentId);
        await db.KeyDeleteAsync(key);
    }

    private static NotificationMessage? Deserialize(StreamEntry entry)
    {
        var dataValue = entry.Values
            .FirstOrDefault(v => v.Name == "data")
            .Value;

        if (dataValue.IsNullOrEmpty) return null;

        var msg = JsonSerializer.Deserialize<NotificationMessage>(dataValue.ToString());
        if (msg != null) msg.EntryId = entry.Id;
        return msg;
    }
}
