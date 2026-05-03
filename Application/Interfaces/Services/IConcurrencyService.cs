namespace MarkdownGenQAs.Application.Interfaces.Services;

/// <summary>
/// Quản lý phân bổ slot LLM động giữa các job GenQA đang chạy song song.
///
/// Data structure (Redis Hash):
///   Key   : "genqa:model:{modelId}"
///   Field : {documentId}  →  { "allowSlot": int, "used": int, "remainingWork": int, "totalWork": int }
///   Field : "__config_total_max"  →  int  (global max concurrent LLM calls cho model này)
///
/// Flow per job (2 phase):
///   Phase 1 start  → AllocateSlots(remainingWork = 2)          // GenSummary + GenQASummary
///   Phase 1 loop   → IncrementUsedAsync / DecrementUsedAsync × 2
///   Phase 2 start  → AllocateSlots(remainingWork = chunks.Count)  // overwrite, tái phân bổ
///   Phase 2 loop   → IncrementUsedAsync / DecrementUsedAsync × N
///   Job done       → RemoveWorkerAsync  (finally)
/// </summary>
public interface IConcurrencyService
{
    /// <summary>
    /// Đăng ký hoặc cập nhật job và tái phân bổ allowSlot cho tất cả job đang hoạt động.
    /// Gọi 2 lần per job: đầu Phase 1 (remainingWork=2) và đầu Phase 2 (remainingWork=chunks.Count).
    /// </summary>
    /// <param name="modelId">LLM model identifier (provider:modelName).</param>
    /// <param name="documentId">Định danh job/document.</param>
    /// <param name="totalMaxConcurrency">Global max concurrent LLM calls cho model này.</param>
    /// <param name="workerDataJson">
    ///   JSON: { "allowSlot":0, "used":0, "remainingWork": N, "totalWork": N }
    /// </param>
    /// <returns>JSON string của worker data đã cập nhật, hoặc null nếu lỗi.</returns>
    Task<string?> AllocateSlotsAsync(string modelId, string documentId, int totalMaxConcurrency, string? workerDataJson = null);

    /// <summary>
    /// Tăng used counter nếu còn slot. Gọi trước mỗi LLM call.
    /// Retry khi trả về null (slot tạm hết). Throw khi job không còn tồn tại (đã cancel).
    /// </summary>
    /// <exception cref="InvalidOperationException">Job không còn trong Redis (đã bị cancel/expired).</exception>
    /// <returns>JSON string sau khi tăng, hoặc null nếu chưa có slot trống.</returns>
    Task<string?> IncrementUsedAsync(string modelId, string documentId);

    /// <summary>
    /// Giảm used và remainingWork sau khi 1 LLM call hoàn thành. Tái phân bổ slot nếu cần.
    /// Gọi trong finally của mỗi LLM call.
    /// </summary>
    /// <returns>JSON string sau khi giảm, hoặc null nếu lỗi.</returns>
    Task<string?> DecrementUsedAsync(string modelId, string documentId);

    /// <summary>
    /// Xóa job khỏi hash và tái phân bổ slot cho các job còn lại.
    /// Gọi trong finally của job (cả success lẫn failure).
    /// </summary>
    /// <returns>JSON array của các job còn lại, hoặc null nếu không còn job nào.</returns>
    Task<string?> RemoveWorkerAsync(string modelId, string documentId);

    /// <summary>
    /// Kiểm tra job còn slot trống (used &lt; allowSlot) mà không thay đổi state.
    /// </summary>
    Task<bool> CanIncrementAsync(string modelId, string documentId);

    /// <summary>
    /// Xóa toàn bộ hash key của một model (dùng cho cleanup/reset).
    /// </summary>
    Task ClearAllWorkersAsync(string modelId);

    /// <summary>
    /// Scan tất cả model key và xóa một documentId cụ thể khỏi mọi hash.
    /// </summary>
    /// <returns>True nếu tìm thấy và xóa được ít nhất 1 key.</returns>
    Task<bool> RemoveWorkerFromAllModelsAsync(string documentId);

    /// <summary>
    /// Quét và xóa toàn bộ các hash key khớp với "genqa:model:*".
    /// Dùng cho Global Cleanup lúc App Startup.
    /// </summary>
    Task ClearAllModelsAsync();

    /// <summary>
    /// Xóa tất cả Redis Stream key khớp với "genqa:stream:*".
    /// </summary>
    Task ClearAllStreamsAsync();
}
