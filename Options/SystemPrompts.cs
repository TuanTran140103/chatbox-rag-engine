namespace GenQAServer.Options;

public class Prompt
{
    public string SystemPrompt {get; set; } = string.Empty;
    public string PathTemplatePrompt {get; set; } = string.Empty;
}

public class SystemPrompts
{
    public const string SectionName = "SystemPrompts";
    public Prompt Choice { get; set; } = new Prompt();
    public Prompt GenQAsText { get; set; } = new Prompt();
    public Prompt GenQAsTable { get; set; } = new Prompt();
    public Prompt GenQAsCombined { get; set; } = new Prompt();
    public Prompt GenSummaryDocument { get; set; } = new Prompt();
    public Prompt MergeSummaryDocument { get; set; } = new Prompt();
    public Prompt MetadataExtraction { get; set; } = new Prompt();
}