namespace MarkdownGenQAs.Application.Dto.Documents;

public record DocumentPagedDto
{
    public List<DocumentListDto> Items { get; set; } = new();
    public DateTime? NextCursorUpdatedAt { get; set; }
    public Guid? NextCursorId { get; set; }
    public bool HasMore { get; set; }
}