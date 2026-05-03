namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public class CreateDatasetRequestDto
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? OUId { get; set; }
    public bool IsPublicToUnit { get; set; } = false;
}
