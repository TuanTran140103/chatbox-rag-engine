namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public class UpdateDatasetRequestDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? IsPublicToUnit { get; set; }
    public Guid? TemplateMetadataId { get; set; }
}
