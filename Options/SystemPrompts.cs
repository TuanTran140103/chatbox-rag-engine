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
    public Prompt GenQAsSummary { get; set; } = new Prompt();
    public Prompt GenQAsText { get; set; } = new Prompt();
    public Prompt GenQAsTable { get; set; } = new Prompt();
    public Prompt GenSummaryDocument { get; set; } = new Prompt();
}