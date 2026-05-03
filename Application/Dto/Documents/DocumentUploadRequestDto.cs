namespace MarkdownGenQAs.Application.Dto.Documents;

public record DocumentUploadRequestDto
{
    public required Stream FileStream { get; set; }
    public required string FileName { get; set; }
    public string? ContentType { get; set; }

    public Guid? CategoryId { get; set; }
}
