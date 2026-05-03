using System.Text.Json.Serialization;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Models;

public class ChunkInfo
{
    [JsonPropertyName("type")]
    public TypeChunk Type { get; set; }
    [JsonPropertyName("tokens_count")]
    public int TokensCount { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; } = string.Empty;
    [JsonPropertyName("tittle_hirarchy")]
    public string? TittleHirarchy { get; set; } = string.Empty;
    [JsonPropertyName("content")]
    public required string Content { get; set; }
    // only set when type is Summary
    [JsonPropertyName("content_summary")]
    public string? ContentSummary { get; set; }

    [JsonPropertyName("table_chunks")]
    public List<ChunkInfo> TableChunks { get; set; } = new();
}