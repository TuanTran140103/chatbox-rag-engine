namespace MarkdownGenQAs.Models;

public class SummaryChunk
{
    public required string Content { get; set; }
    public string HierarchyPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int TokensCount { get; set; }
}
