using MarkdownGenQAs.Infrastructure.Exceptions;
using MarkdownGenQAs.Models.Enum;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Application.Interfaces.ExternalServices;

public interface IOCRService
{
    /// <summary>
    /// Kiểm tra OCR server còn hoạt động không.
    /// </summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gọi OCR API để xử lý file.
    /// Ném OcrApiException nếu API trả về lỗi.
    /// </summary>
    Task<OcrProcessResponse> ProcessAsync(IFormFile file, string modelId = "deepseekocr");
    
    /// <summary>
    /// Gọi OCR API để xử lý file từ stream.
    /// Ném OcrApiException nếu API trả về lỗi.
    /// </summary>
    Task<OcrProcessResponse> ProcessAsync(Stream fileStream, string fileName, string contentType, string modelId = "deepseekocr");
    
    /// <summary>
    /// Lấy kết quả markdown từ OCR API.
    /// Ném OcrApiException nếu API trả về lỗi.
    /// </summary>
    Task<OcrMarkdownResponse> GetMarkdownAsync(string taskId);

    /// <summary>
    /// Hủy job OCR đang chạy.
    /// Trả về message từ API nếu thành công (HTTP 200), null nếu thất bại.
    /// </summary>
    Task<string?> CancelJobAsync(string taskId);

    /// <summary>
    /// Submit file PDF lên OCR service để xử lý.
    /// Theo API mới: POST /api/ocr/process với multipart/form-data
    /// Fields: File (IFormFile), ModelId (string)
    /// </summary>
    Task<OcrProcessResponse> SubmitPdfAsync(Stream pdfStream, string fileName, string modelId = "deepseekocr");
}

public class OcrProcessResponse
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response từ OCR API mới theo docs: Array of PageOcrResult
/// API trả về trực tiếp một mảng JSON: [{ "pageIndex": 0, "markdown": "...", "images": {} }, ...]
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(List<PageOcrResult>))]
public partial class OcrMarkdownResponseContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

public class OcrMarkdownResponse
{
    public List<PageOcrResult> Pages { get; set; } = new();

    /// <summary>
    /// Implicit conversion từ List&lt;PageOcrResult&gt; (khi deserialize JSON array trực tiếp).
    /// </summary>
    public static implicit operator OcrMarkdownResponse(List<PageOcrResult> pages)
    {
        return new OcrMarkdownResponse { Pages = pages };
    }
}

/// <summary>
/// Đại diện cho một trang OCR result theo API mới.
/// Theo docs: { "pageIndex": 0, "markdown": "...", "images": { "bbox_key": "base64..." } }
/// </summary>
public class PageOcrResult
{
    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }

    [JsonPropertyName("markdown")]
    public string Markdown { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary chứa ảnh đã crop/extract từ trang.
    /// Key: tên file ảnh (format: {x1}_{y1}_{x2}_{y2}.{ext})
    /// Value: Base64-encoded string của dữ liệu ảnh (không có data URI prefix)
    /// </summary>
    [JsonPropertyName("images")]
    public Dictionary<string, string> Images { get; set; } = new();
}

/// <summary>
/// Legacy PageResult - giữ lại để tương thích ngược nếu cần.
/// Sẽ được thay thế bởi PageOcrResult trong code mới.
/// </summary>
[Obsolete("Use PageOcrResult instead. This class is kept for backward compatibility.")]
public class PageResult
{
    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }

    [JsonPropertyName("markdown")]
    public string Markdown { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public List<ImageResult> Images { get; set; } = new();
}

[Obsolete("Use PageOcrResult.Images (Dictionary) instead. This class is kept for backward compatibility.")]
public class ImageResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("base64")]
    public string? Base64 { get; set; }
}
