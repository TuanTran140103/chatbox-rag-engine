namespace MarkdownGenQAs.Application.Dto.Admin.Dataset;

/// <summary>
/// Dataset overview for admin list view.
/// </summary>
public record DatasetOverviewDto(
    Guid Id,
    string Name,
    string OwnerName,
    string? OUName,
    int ItemCount,
    int DocumentCount,
    string StorageDisplay,
    bool IsPublicToUnit,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TemplateMetadataId = null,
    string? TemplateMetadataName = null
);
