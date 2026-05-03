
namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface ICacheService
{
    /// <summary>
    /// Lấy Job ID đang xử lý của một file
    /// </summary>
    Task<string?> GetActiveGenQAJobIdAsync(Guid ocrFileId);

    /// <summary>
    /// Lưu Job ID đang xử lý cho file
    /// </summary>
    Task SetActiveGenQAJobIdAsync(Guid ocrFileId, string jobId, TimeSpan? expiration = null);

    /// <summary>
    /// Xóa Job ID khi hoàn thành hoặc bị hủy
    /// </summary>
    Task RemoveActiveGenQAJobIdAsync(Guid ocrFileId);

    /// <summary>
    /// Xóa Job ID chỉ khi nó khớp với Job ID mong đợi (để tránh xóa nhầm Job mới)
    /// </summary>
    Task<bool> TryClearActiveGenQAJobIdAsync(Guid ocrFileId, string expectedJobId);

    /// <summary>
    /// Lấy tất cả các Job ID đang active (dùng cho cleanup khi shutdown)
    /// </summary>
    Task<Dictionary<Guid, string>> GetAllActiveGenQAJobsAsync();

    /// <summary>
    /// Push event log to job-specific stream
    /// </summary>
    Task PushOcrEventAsync(string jobId, object eventData);
}
