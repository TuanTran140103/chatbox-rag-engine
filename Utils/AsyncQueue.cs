namespace MarkdownGenQAs.Utils;

/// <summary>
/// Thread-safe queue with async enqueue/dequeue support.
/// Used for per-document sequential processing in OcrResultConsumer.
/// </summary>
public class AsyncQueue<T>
{
    private readonly Queue<T> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed = false;

    /// <summary>
    /// Dequeues an item asynchronously. Waits if queue is empty.
    /// Returns null when Complete() is called and queue is empty.
    /// </summary>
    public async Task<T?> DequeueAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);

        lock (_queue)
        {
            return _queue.Count > 0 ? _queue.Dequeue() : default;
        }
    }

    /// <summary>
    /// Enqueues an item and signals waiting workers.
    /// </summary>
    public Task EnqueueAsync(T item)
    {
        lock (_queue)
        {
            _queue.Enqueue(item);
        }
        _signal.Release();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals that no more items will be added.
    /// Wakes up waiting workers to exit.
    /// </summary>
    public void Complete()
    {
        _completed = true;
        _signal.Release();  // Wake up waiting worker
    }

    /// <summary>
    /// Returns true if the queue is completed and empty.
    /// </summary>
    public bool IsCompletedAndEmpty
    {
        get
        {
            lock (_queue)
            {
                return _completed && _queue.Count == 0;
            }
        }
    }
}
