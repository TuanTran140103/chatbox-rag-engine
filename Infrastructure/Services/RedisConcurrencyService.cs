using System.Collections.Concurrent;
using System.Text.Json;
using MarkdownGenQAs.Application.Interfaces.Services;
using StackExchange.Redis;

namespace MarkdownGenQAs.Infrastructure.Services;

/// <summary>
/// Redis-backed implementation của <see cref="IConcurrencyService"/>.
/// Dùng Lua scripts để đảm bảo atomic slot management và Redis Stream event publishing.
///
/// Redis Hash key pattern: "genqa:model:{modelId}"
/// Field layout:
///   {documentId}         → { "allowSlot": int, "used": int, "remainingWork": int, "totalWork": int }
///   "__config_total_max" → int
/// </summary>
public class RedisConcurrencyService : IConcurrencyService
{
    private readonly IDatabase _db;
    private readonly ILogger<RedisConcurrencyService> _logger;

    // Cache nội dung script Lua — load lazy lần đầu, thread-safe
    private static readonly ConcurrentDictionary<string, string> _scriptCache = new();

    private readonly string _scriptPath;

    public RedisConcurrencyService(IConnectionMultiplexer redis, ILogger<RedisConcurrencyService> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;

        // Primary: cạnh assembly đã publish
        _scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Infrastructure", "LuaScripts");

        // Fallback: project root (dotnet run local)
        if (!Directory.Exists(_scriptPath))
            _scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Infrastructure", "LuaScripts");
    }

    // ─── Key Helper ───────────────────────────────────────────────────────────

    /// <summary>"genqa:model:{modelId}" — 1 hash per LLM model.</summary>
    private static string GetModelKey(string modelId) => $"genqa:model:{modelId}";

    // ─── Lua Script Loading ───────────────────────────────────────────────────

    /// <summary>
    /// Load Lua script từ disk lần đầu; lần sau trả về cached string.
    /// GetOrAdd an toàn concurrent: tối đa 1 extra read, cả hai đều valid.
    /// </summary>
    private async Task<string> GetScriptAsync(string fileName)
    {
        if (_scriptCache.TryGetValue(fileName, out var cached))
            return cached;

        var fullPath = Path.Combine(_scriptPath, fileName);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Lua script not found: {fullPath}");

        var script = await File.ReadAllTextAsync(fullPath);
        return _scriptCache.GetOrAdd(fileName, script);
    }

    // ─── Public Methods ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string?> AllocateSlotsAsync(
        string modelId,
        string documentId,
        int totalMaxConcurrency,
        string? workerDataJson = null)
    {
        try
        {
            var script = await GetScriptAsync("allocate_slots.lua");
            var result = await _db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { documentId, totalMaxConcurrency, workerDataJson ?? string.Empty });

            return result.IsNull ? null : result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error executing allocate_slots.lua for model {ModelId}, document {DocumentId}",
                modelId, documentId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> IncrementUsedAsync(string modelId, string documentId)
    {
        try
        {
            var script = await GetScriptAsync("increment_used.lua");
            var result = await _db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { documentId });

            if (result.IsNull)
            {
                // Job đã bị cancel hoặc expired — báo caller dừng retry
                throw new InvalidOperationException("Job not found in Redis (cancelled or expired)");
            }

            return result.ToString();
        }
        catch (RedisServerException ex) when (ex.Message.Contains("No slots available"))
        {
            // Quota tạm hết — caller back-off và retry
            return null;
        }
        catch (Exception ex) when (ex.Message.Contains("Job not found"))
        {
            // Re-throw để background job xử lý cancel
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error executing increment_used.lua for model {ModelId}, document {DocumentId}",
                modelId, documentId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> DecrementUsedAsync(string modelId, string documentId)
    {
        try
        {
            var script = await GetScriptAsync("decrement_used.lua");
            var result = await _db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { documentId });

            return result.IsNull ? null : result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error executing decrement_used.lua for model {ModelId}, document {DocumentId}",
                modelId, documentId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> RemoveWorkerAsync(string modelId, string documentId)
    {
        try
        {
            var script = await GetScriptAsync("remove_worker.lua");
            var result = await _db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { documentId });

            return result.IsNull ? null : result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error executing remove_worker.lua for model {ModelId}, document {DocumentId}",
                modelId, documentId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CanIncrementAsync(string modelId, string documentId)
    {
        try
        {
            var dataRaw = await _db.HashGetAsync(GetModelKey(modelId), documentId);
            if (dataRaw.IsNull) return false;

            using var doc = JsonDocument.Parse(dataRaw.ToString());
            var root = doc.RootElement;

            int used = root.GetProperty("used").GetInt32();
            int allowSlot = root.GetProperty("allowSlot").GetInt32();

            return used < allowSlot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error checking capacity for model {ModelId}, document {DocumentId}",
                modelId, documentId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task ClearAllWorkersAsync(string modelId)
    {
        var key = GetModelKey(modelId);
        await _db.KeyDeleteAsync(key);
        _logger.LogInformation(
            "Cleared all jobs for model {ModelId} from Redis key {Key}",
            modelId, key);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveWorkerFromAllModelsAsync(string documentId)
    {
        bool anyRemoved = false;
        try
        {
            var endpoints = _db.Multiplexer.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = _db.Multiplexer.GetServer(endpoint);

                await foreach (var key in server.KeysAsync(pattern: "genqa:model:*"))
                {
                    bool removed = await _db.HashDeleteAsync(key, documentId);
                    if (removed)
                    {
                        _logger.LogInformation(
                            "Removed document {DocumentId} from Redis key {Key}",
                            documentId, key);
                        anyRemoved = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error scanning/removing document {DocumentId} from all models",
                documentId);
        }
        return anyRemoved;
    }

    /// <inheritdoc/>
    public async Task ClearAllModelsAsync()
    {
        try
        {
            var endpoints = _db.Multiplexer.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = _db.Multiplexer.GetServer(endpoint);
                await foreach (var key in server.KeysAsync(pattern: "genqa:model:*"))
                {
                    await _db.KeyDeleteAsync(key);
                    _logger.LogInformation("Deleted model key: {Key}", key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing all genqa model keys");
        }
    }

    /// <inheritdoc/>
    public async Task ClearAllStreamsAsync()
    {
        try
        {
            var endpoints = _db.Multiplexer.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = _db.Multiplexer.GetServer(endpoint);

                await foreach (var key in server.KeysAsync(pattern: "genqa:stream:*"))
                {
                    await _db.KeyDeleteAsync(key);
                    _logger.LogInformation("Deleted stream key: {Key}", key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing all genqa stream keys");
        }
    }
}
