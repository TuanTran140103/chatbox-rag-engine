using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.Entities;

public class LogEvent
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
    [JsonPropertyName("status")]
    public required string Status { get; set; }
    [JsonPropertyName("message")]
    public required string Message { get; set; }
    [JsonPropertyName("time")]
    public required string Time { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    [JsonPropertyName("processingTime")]
    public double? ProcessingTime { get; set; }
}


public class LogMessage : BaseEntity
{
    [Column(TypeName = "jsonb")]
    public List<LogEvent>? LogsOcr { get; set; }
    [Column(TypeName = "jsonb")]
    public List<LogEvent>? LogsGenQa { get; set; }
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }
}
