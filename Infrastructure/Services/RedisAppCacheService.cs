using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using System.Text.Json;

namespace MarkdownGenQAs.Infrastructure.Services;

public class RedisAppCacheService : IAppCacheService
{
    private readonly IDistributedCache _cache;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisAppCacheService> _logger;
    private const string Prefix = "appcache:";

    public RedisAppCacheService(
        IDistributedCache cache,
        IConnectionMultiplexer multiplexer,
        ILogger<RedisAppCacheService> logger)
    {
        _cache = cache;
        _multiplexer = multiplexer;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var fullKey = BuildKey(key);
            var cached = await _cache.GetStringAsync(fullKey);
            if (string.IsNullOrEmpty(cached)) return default;
            return JsonSerializer.Deserialize<T>(cached);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache GET error for key {Key}", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null)
    {
        try
        {
            var fullKey = BuildKey(key);
            var serialized = JsonSerializer.Serialize(value);
            var options = new DistributedCacheEntryOptions();
            if (ttl.HasValue) options.AbsoluteExpirationRelativeToNow = ttl;
            await _cache.SetStringAsync(fullKey, serialized, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache SET error for key {Key}", key);
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await _cache.RemoveAsync(BuildKey(key));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache REMOVE error for key {Key}", key);
        }
    }

    public async Task RemoveByPrefixAsync(string prefix)
    {
        try
        {
            var endpoints = _multiplexer.GetEndPoints();
            var server = _multiplexer.GetServer(endpoints.First());
            var pattern = $"MarkdownGenQAs:{Prefix}{prefix}*";

            var keys = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                keys.Add(key);
            }

            if (keys.Count == 0) return;

            var db = _multiplexer.GetDatabase();
            await db.KeyDeleteAsync(keys.ToArray());

            _logger.LogInformation("Cache flushed {Count} keys with prefix {Prefix}", keys.Count, prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache REMOVE_BY_PREFIX error for prefix {Prefix}", prefix);
        }
    }

    private static string BuildKey(string key) => $"{Prefix}{key}";
}
