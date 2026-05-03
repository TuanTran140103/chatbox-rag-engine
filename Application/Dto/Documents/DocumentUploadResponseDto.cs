namespace MarkdownGenQAs.Application.Dto.Documents;

public record DocumentUploadResponseDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public DateTime CreatedAt { get; set; }
}
