namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public class CreateItemResponseDto
{
    public Guid ItemId { get; set; }
    public Guid? DocumentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Level { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DatasetItemDocumentDto? Item { get; set; }
}
