namespace MarkdownGenQAs.Application.Dto.Documents;

public record DocumentListDto
{
    public Guid Id { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public bool IsOcred { get; set; }
    public bool IsIndexed { get; set; }
    public string StatusDocument { get; set; } = string.Empty;
    public int OcrCount { get; set; }
    public string? CategoryName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}