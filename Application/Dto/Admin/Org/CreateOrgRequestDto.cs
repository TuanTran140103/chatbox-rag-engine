namespace MarkdownGenQAs.Application.Dto.Admin.Org;

public class CreateOrgRequestDto
{
    public required string Name { get; set; }
    public string? Code { get; set; }
    public Guid? ParentId { get; set; }
}
