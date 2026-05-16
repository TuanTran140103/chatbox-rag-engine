namespace MarkdownGenQAs.Application.Dto.Search;

public class VectorSearchItem
{
    public Guid DocumentId { get; set; }
    public Guid DatasetId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ChunkType { get; set; }
    public float Score { get; set; }
}
