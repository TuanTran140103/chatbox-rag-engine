namespace MarkdownGenQAs.Application.Dto.TemplateMetadata;

public record TemplateMetadataListDto(
    Guid Id,
    string Name,
    string? Description,
    DateTime CreatedAt
);
