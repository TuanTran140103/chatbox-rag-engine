using MarkdownGenQAs.Models;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;

namespace MarkdownGenQAs.Application.Interfaces.Services;
public interface IMarkdownService
{
    /// <summary>
    /// Thực hiện tạo chunk dựa vào headerLevel và số lượng token (default maxChunkSize: 8192).
    /// Logic chunk thuần túy, không liên quan đến xử lý table.
    /// </summary>
    /// <param name="source">Nội dung markdown đầu vào</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Danh sách chunk theo header/token</returns>
    Task<List<ChunkInfo>> CreateChunkAsync(string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo table chunks từ toàn bộ source.
    /// Dùng hybrid heuristic (μ±σ thresholds) + AI fallback (grey zone) để merge continuation tables.
    /// Hoàn toàn độc lập với CreateChunkAsync.
    /// </summary>
    /// <param name="source">Nội dung markdown đầu vào</param>
    /// <param name="parentHierarchy">Hierarchy từ chunk cha (optional)</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Danh sách chunk table đã được merge</returns>
    Task<List<ChunkInfo>> CreateChunkTableAsync(string source, Stack<KeyValuePair<int, string>>? parentHierarchy = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chuyển đổi các image references trong markdown sang MinIO public links với thông tin pages từ OCR response.
    /// Xử lý từng page riêng lẻ để tránh xung đột khi các page có image path trùng nhau.
    /// Hỗ trợ cả 2 format image reference: Markdown ![alt](key) và HTML &lt;img src="key" /&gt;
    /// </summary>
    /// <param name="markdownContent">Nội dung markdown hoàn chỉnh (đã nối từ các pages) - không sử dụng, sẽ tự nối từ pages</param>
    /// <param name="documentId">Document ID</param>
    /// <param name="pages">Danh sách pages từ OCR response (PageOcrResult với Dictionary images)</param>
    /// <param name="uploadSemaphore">Optional: semaphore to limit concurrent uploads</param>
    /// <returns>Markdown với tất cả images đã được thay thế bằng MinIO public links</returns>
    Task<string> TransformPagesImagesToMinioLinkAsync(string markdownContent, string documentId, List<PageOcrResult> pages, SemaphoreSlim? uploadSemaphore = null);

    /// <summary>
    /// Split document by headers (H1 → H2 → ...) until each chunk ≤ maxTokensPerChunk.
    /// Used for processing summaries of very long documents.
    /// </summary>
    Task<List<SummaryChunk>> SplitDocumentForSummaryAsync(string source, int maxTokensPerChunk, CancellationToken cancellationToken = default);

    /// <summary>
    /// Split document only at H1 level (or H2 if no H1 found). Does not recurse deeper.
    /// Used for step 1b (QA summary) where only top-level splits are needed.
    /// </summary>
    Task<List<SummaryChunk>> SplitDocumentTopLevelAsync(string source, int maxTokensPerChunk, CancellationToken cancellationToken = default);
}