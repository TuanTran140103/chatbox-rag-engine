namespace MarkdownGenQAs.Application.Dto.Admin.Org;

public class UpdateOrgRequestDto
{
    public required string Name { get; set; }
    public string? Code { get; set; }
}
