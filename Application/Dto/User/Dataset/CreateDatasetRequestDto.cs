namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public class CreateDatasetRequestDto
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid TemplateMetadataId { get; set; }
}