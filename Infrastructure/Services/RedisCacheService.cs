using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using System.Text.Json;

namespace MarkdownGenQAs.Infrastructure.Services;

public class RedisCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisCacheService> _logger;
    private const string InstancePrefix = "MarkdownGenQAs:";

    public RedisCacheService(
        IDistributedCache cache,
        IConnectionMultiplexer multiplexer,
        ILogger<RedisCacheService> logger)
    {
        _cache = cache;
        _multiplexer = multiplexer;
        _logger = logger;
    }

    public async Task<string?> GetActiveGenQAJobIdAsync(Guid ocrFileId)
    {
        return await GetAsync<string>(GetJobKey(ocrFileId));
    }

    public async Task SetActiveGenQAJobIdAsync(Guid ocrFileId, string jobId, TimeSpan? expiration = null)
    {
        await SetAsync(GetJobKey(ocrFileId), jobId, expiration ?? TimeSpan.FromHours(12));
    }

    public async Task RemoveActiveGenQAJobIdAsync(Guid ocrFileId)
    {
        await RemoveAsync(GetJobKey(ocrFileId));
    }

    public async Task<bool> TryClearActiveGenQAJobIdAsync(Guid ocrFileId, string expectedJobId)
    {
        var key = GetJobKey(ocrFileId);
        var cachedJobId = await GetActiveGenQAJobIdAsync(ocrFileId);
        if (cachedJobId == expectedJobId)
        {
            await RemoveAsync(key);
            return true;
        }
        return false;
    }

    public async Task<Dictionary<Guid, string>> GetAllActiveGenQAJobsAsync()
    {
        var result = new Dictionary<Guid, string>();

        try
        {
            var endpoints = _multiplexer.GetEndPoints();
            var server = _multiplexer.GetServer(endpoints.First());
            var pattern = $"{InstancePrefix}job:*";

            _logger.LogInformation("Scanning Redis for active jobs with pattern: {Pattern}", pattern);

            int count = 0;
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                var keyStr = key.ToString();
                // Extract GUID from "MarkdownGenQAs:job:{guid}"
                if (keyStr.StartsWith($"{InstancePrefix}job:"))
                {
                    var guidPart = keyStr.Substring($"{InstancePrefix}job:".Length);
                    if (Guid.TryParse(guidPart, out var ocrFileId))
                    {
                        var jobId = await GetActiveGenQAJobIdAsync(ocrFileId);
                        if (!string.IsNullOrEmpty(jobId))
                        {
                            result[ocrFileId] = jobId;
                            count++;
                        }
                    }
                }
            }
            _logger.LogInformation("Found {Count} active job keys in Redis", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning Redis for active GenQA jobs");
        }

        return result;
    }

    public async Task PushOcrEventAsync(string jobId, object eventData)
    {
        try
        {
            var db = _multiplexer.GetDatabase();
            var key = $"markdowngenqas:ocr:stream:{jobId}";
            var payload = JsonSerializer.Serialize(eventData);

            // Add to stream
            await db.StreamAddAsync(key, "log", payload);

            // Publish to channel for real-time subscribers
            await _multiplexer.GetSubscriber().PublishAsync(RedisChannel.Literal(key), payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing OCR event for jobId {JobId}", jobId);
        }
    }

    #region Private Helpers

    private string GetJobKey(Guid ocrFileId) => $"job:{ocrFileId}";

    private async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var cachedData = await _cache.GetStringAsync(key);
            if (string.IsNullOrEmpty(cachedData))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(cachedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting key {Key} from Redis", key);
            return default;
        }
    }

    private async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null)
    {
        try
        {
            var serializedData = JsonSerializer.Serialize(value!);
            var options = new DistributedCacheEntryOptions();

            if (ttl.HasValue)
            {
                options.AbsoluteExpirationRelativeToNow = ttl;
            }

            await _cache.SetStringAsync(key, serializedData, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting key {Key} in Redis", key);
        }
    }

    private async Task RemoveAsync(string key)
    {
        try
        {
            await _cache.RemoveAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing key {Key} from Redis", key);
        }
    }

    #endregion
}
