namespace MarkdownGenQAs.Application.Dto.Search;

public class DocumentSearchItem
{
    public Guid DocumentId { get; set; }
    public Guid DatasetId { get; set; }
    public string FileName { get; set; } = string.Empty;
}
