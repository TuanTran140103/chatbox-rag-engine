namespace MarkdownGenQAs.Application.Dto.Admin.User;

public record UserListResponse
{
    public List<UserListItemDto> Items { get; set; } = [];
    public DateTime? NextCursorCreatedAt { get; set; }
    public Guid? NextCursorId { get; set; }
    public bool HasMore { get; set; }
}
