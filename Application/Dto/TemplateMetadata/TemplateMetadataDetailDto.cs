namespace MarkdownGenQAs.Application.Dto.TemplateMetadata;

public record TemplateMetadataDetailDto(
    Guid Id,
    string Name,
    string? Description,
    string JsonSchema,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy
);
