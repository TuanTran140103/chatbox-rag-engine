using System.ComponentModel.DataAnnotations;

namespace MarkdownGenQAs.Application.Dto.TemplateMetadata;

public class CreateTemplateMetadataRequestDto
{
    [Required]
    [MaxLength(255)]
    public required string Name { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Required]
    public required string JsonSchema { get; set; }
}
