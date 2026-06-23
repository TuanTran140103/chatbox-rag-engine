using System.Runtime.CompilerServices;
using System.Text.Json;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using StackExchange.Redis;

namespace MarkdownGenQAs.Infrastructure.Services;

public class StreamBroadcaster : IProcessBroadcaster
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

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
        var payload = JsonSerializer.Serialize(message, _jsonOptions);
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
        var db = _redis.GetDatabase();
        var key = GetStreamKey(processType, documentId);

        // Đọc toàn bộ history từ đầu stream
        var entries = await db.StreamRangeAsync(key, "-", "+");
        foreach (var entry in entries)
        {
            var msg = Deserialize(entry);
            if (msg != null) yield return msg;
        }

        // Poll message mới
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

    public async Task<List<NotificationMessage>> ReadHistoryAsync(string processType, Guid documentId)
    {
        var db = _redis.GetDatabase();
        var key = GetStreamKey(processType, documentId);
        var entries = await db.StreamRangeAsync(key, "-", "+");
        var messages = new List<NotificationMessage>(entries.Length);
        foreach (var entry in entries)
        {
            var msg = Deserialize(entry);
            if (msg != null) messages.Add(msg);
        }
        return messages;
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

        var msg = JsonSerializer.Deserialize<NotificationMessage>(dataValue.ToString(), _jsonOptions);
        if (msg != null) msg.EntryId = entry.Id;
        return msg;
    }
}
