namespace GenQAServer.Options;

public class LlmProviderItem
{
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public double Temperature { get; set; } = 0.5;
    public int MaxTokens { get; set; } = 8192;
    public float TopP { get; set; } = 1.0f;
    public bool HaveThinking { get; set; } = false;
    public int MaxConcurrency { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 600;
}

public class LlmProviderSettings
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public List<LlmProviderItem> Models { get; set; } = new();
}

public class LlmProviderOptions
{
    public const string SectionName = "LlmProviders";
    public Dictionary<string, LlmProviderSettings> Providers { get; set; } = new();
}
