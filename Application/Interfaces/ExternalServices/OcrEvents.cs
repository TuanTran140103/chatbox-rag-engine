using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.ExternalServices;

/// <summary>
/// Base class cho tất cả OCR events từ Redis Stream
/// </summary>
public class OcrEventBase
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("processingTime")]
    public double? ProcessingTime { get; set; }
}

/// <summary>
/// Event type: Logging - dùng để broadcast progress
/// </summary>
public class OcrLoggingEvent : OcrEventBase
{
}

/// <summary>
/// Event type: GetMarkdown - phát khi kết quả OCR sẵn sàng để download
/// Theo docs V2: dataJson = "{\"url\":\"get-markdown/{taskId}\"}" (JSON string)
/// Client parse string để lấy URL, sau đó gọi GET /api/ocr/get-markdown/{taskId}
/// </summary>
public class OcrGetMarkdownEvent : OcrEventBase
{
    /// <summary>
    /// Raw dataJson string từ event. Format: "{\"url\":\"get-markdown/{taskId}\"}"
    /// Parse bằng cách deserialize string này để lấy URL.
    /// </summary>
    [JsonPropertyName("dataJson")]
    public string? DataJson { get; set; }

    /// <summary>
    /// URL extracted từ DataJson. Ví dụ: "get-markdown/serverabc-xxx"
    /// </summary>
    [JsonIgnore]
    public string? MarkdownUrl => TryParseDataJson();

    private string? TryParseDataJson()
    {
        if (string.IsNullOrEmpty(DataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(DataJson);
            return doc.RootElement.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Event type: SaveLog - chứa danh sách log events khi job kết thúc
/// Theo docs V2: dataJson là STRING chứa JSON array: "[{...},{...},...]"
/// Client phải deserialize từ string để lấy danh sách LogEvent.
/// </summary>
public class OcrSaveLogEvent : OcrEventBase
{
    /// <summary>
    /// Raw dataJson string từ event. Format: JSON array serialized string.
    /// Phải deserialize từ string để lấy danh sách LogEvent.
    /// </summary>
    [JsonPropertyName("dataJson")]
    public string? DataJson { get; set; }

    /// <summary>
    /// Danh sách log events đã deserialize từ DataJson.
    /// </summary>
    [JsonIgnore]
    public List<LogEvent>? Data => TryParseDataJson();

    private List<LogEvent>? TryParseDataJson()
    {
        if (string.IsNullOrEmpty(DataJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<LogEvent>>(DataJson);
        }
        catch
        {
            return null;
        }
    }
}
