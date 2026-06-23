using System.Text.Json.Serialization;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Models;

public class ChunkInfo
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TypeChunk Type { get; set; }
    [JsonPropertyName("tokensCount")]
    public int TokensCount { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; } = string.Empty;
    [JsonPropertyName("titleHierarchy")]
    public string? TitleHierarchy { get; set; } = string.Empty;
    [JsonPropertyName("content")]
    public required string Content { get; set; }
    // only set when type is Summary
    [JsonPropertyName("contentSummary")]
    public string? ContentSummary { get; set; }

    [JsonIgnore]
    public bool NeedsSummary { get; set; }

    [JsonIgnore]
    public int SourceStart { get; set; }

    [JsonIgnore]
    public int SourceEnd { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }
}