using System.Text.Json;

namespace MarkdownGenQAs.Application.Dto.Search;

public class VectorSearchRequest
{
    public required string QueryText { get; set; }
    public List<Guid>? DatasetIds { get; set; }
    public Dictionary<string, JsonElement>? MetadataFilter { get; set; }
    public float ScoreThreshold { get; set; } = 0.3f;
    public int TopK { get; set; } = 10;
}
