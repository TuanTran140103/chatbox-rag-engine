using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models;

public record NotificationMessage
{
    public Guid DocumentId { get; set; }
    public string Timestamp { get; set; } = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
    public string Message { get; set; } = string.Empty;
    public required string Status { get; set; }
    public string? ProcessType { get; set; }
    public double? ProcessingTime { get; set; }

    /// <summary>
    /// Stage hiện tại: "OCR" hoặc "GenQA"
    /// </summary>
    public string Stage { get; set; } = "OCR";

    [JsonIgnore]
    public string? EntryId { get; set; }
}
