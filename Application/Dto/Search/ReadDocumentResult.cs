namespace MarkdownGenQAs.Application.Dto.Search;

public class ReadDocumentResult
{
    public Guid DocumentId { get; set; }
    public Guid DatasetId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ContentType { get; set; }
}
