namespace MarkdownGenQAs.Application.Dto.Documents;

public class CreateFolderRequestDto
{
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
}
