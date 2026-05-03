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
    /// Parse source để lấy content của từng header và tự xây dựng hierarchy.
    /// Với mỗi content-header, tạo 1 task riêng gọi AI để merge table vào chunk.
    /// Tất cả task chạy song song (Task.WhenAll) — không cần wrapper concurrency
    /// vì đây là request chat choice (rất nhanh).
    /// Hoàn toàn độc lập với CreateChunkAsync.
    /// </summary>
    /// <param name="source">Nội dung markdown đầu vào</param>
    /// <param name="parentHierarchy">Hierarchy từ chunk cha (optional)</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Danh sách chunk table đã được AI merge</returns>
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
}