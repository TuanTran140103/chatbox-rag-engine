using System.ComponentModel.DataAnnotations;

namespace MarkdownGenQAs.Application.Dto.TemplateMetadata;

public class UpdateTemplateMetadataRequestDto
{
    [MaxLength(255)]
    public string? Name { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    public string? JsonSchema { get; set; }
}
