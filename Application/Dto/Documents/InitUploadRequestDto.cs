using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Application.Dto.Documents;

public class InitUploadRequestDto
{
    public required string FileName { get; set; }
    public required long FileSize { get; set; }
    public Guid? ParentId { get; set; }
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }
}
