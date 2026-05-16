namespace MarkdownGenQAs.Application.Dto.Thread;

public record CreateThreadRequestDto
{
    public required string Title { get; set; }
}
