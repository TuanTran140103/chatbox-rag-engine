using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models;


public class CountRequest
{
    [Required]
    [JsonPropertyName("text")]
    public required string Text { get; set; }
    [JsonPropertyName("return_tokens")]
    public bool ReturnTokens { get; set; } = false;
}

public class CountResponse
{
    [JsonPropertyName("token_count")]
    public int TokenCount { get; set; }
    [JsonPropertyName("token_ids")]
    public List<int>? TokenIds { get; set; }
}

public class BatchItemRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

public class BatchCountRequest
{
    [Required, MinLength(1), MaxLength(2000)]
    [JsonPropertyName("items")]
    public required List<BatchItemRequest> Items { get; set; }

    [JsonPropertyName("return_tokens")]
    public bool ReturnTokens { get; set; } = true;
}

public class BatchCountItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("token_count")]
    public int TokenCount { get; set; }

    [JsonPropertyName("token_ids")]
    public List<int>? TokenIds { get; set; }
}

public class BatchCountResponse
{
    [JsonPropertyName("results")]
    public required List<BatchCountItem> Results { get; set; }
}