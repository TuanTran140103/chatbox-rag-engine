namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IAppCacheService
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null);
    Task RemoveAsync(string key);
    Task RemoveByPrefixAsync(string prefix);
}
