namespace MarkdownGenQAs.Application.Interfaces;

/// <summary>
/// Manages global LLM concurrency using distributed Redis semaphore
/// </summary>
public interface ILlmConcurrencyManager
{
    /// <summary>
    /// Attempts to acquire a concurrency slot for processing
    /// </summary>
    /// <param name="provider">LLM provider name</param>
    /// <param name="model">Model name</param>
    /// <param name="workerId">Unique worker/job identifier</param>
    /// <param name="maxSlots">Maximum concurrent slots allowed</param>
    /// <returns>True if slot acquired, false if no slots available</returns>
    Task<bool> TryAcquireSlotAsync(string provider, string model, Guid workerId, int maxSlots);

    /// <summary>
    /// Releases a concurrency slot back to the pool
    /// </summary>
    /// <param name="provider">LLM provider name</param>
    /// <param name="model">Model name</param>
    /// <param name="workerId">Unique worker/job identifier</param>
    Task ReleaseSlotAsync(string provider, string model, Guid workerId);

    /// <summary>
    /// Gets current usage statistics
    /// </summary>
    /// <param name="provider">LLM provider name</param>
    /// <param name="model">Model name</param>
    /// <returns>Tuple of (slots used, active workers)</returns>
    Task<(int slotsUsed, int activeWorkers)> GetStatusAsync(string provider, string model);
}
