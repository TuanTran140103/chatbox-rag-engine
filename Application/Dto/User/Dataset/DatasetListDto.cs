namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public record DatasetListDto(
    Guid Id,
    string Name,
    string? OUName,
    Guid? OUId,
    int ItemCount,
    int DocumentCount,
    bool IsPublicToUnit,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TemplateMetadataId = null,
    string? TemplateMetadataName = null
);
