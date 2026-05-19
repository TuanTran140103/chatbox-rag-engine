namespace MarkdownGenQAs.Application.Dto.Thread;

public record ThreadListDto
{
    public Guid Id { get; set; }
    public string ThreadId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
