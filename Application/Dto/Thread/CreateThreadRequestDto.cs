namespace MarkdownGenQAs.Application.Dto.Thread;

public record CreateThreadRequestDto
{
    public required string ThreadId { get; set; }
    public required string Title { get; set; }
}
