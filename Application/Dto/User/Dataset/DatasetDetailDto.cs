namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public record DatasetDetailDto(
    Guid Id,
    string Name,
    string? Description,
    string OwnerName,
    string? OUName,
    Guid? OUId,
    int ItemCount,
    int DocumentCount,
    bool IsPublicToUnit,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
