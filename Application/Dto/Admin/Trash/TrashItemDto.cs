namespace MarkdownGenQAs.Application.Dto.Admin.Trash;

public record TrashItemDto(
    Guid Id,
    TrashItemType Type,
    string Name,
    string? ParentInfo,
    DateTime DeletedAt,
    Guid? DeletedBy
);
