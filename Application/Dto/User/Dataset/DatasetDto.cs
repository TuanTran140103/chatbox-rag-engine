namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public record DatasetDto(
    Guid Id,
    string Name,
    string? Description,
    int ItemCount,
    int DocumentCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TemplateMetadataId = null,
    string? TemplateMetadataName = null
);
