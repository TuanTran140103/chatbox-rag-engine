

namespace MarkdownGenQAs.Options;

public class ExternalServiceOptions
{
    public const string SectionName = "ExternalServices";
    public TokenCountServiceOptions TokenCountService { get; set; } = new();
    public OCRServiceOptions OCRService { get; set; } = new();
    public EmbeddingServiceOptions EmbeddingService { get; set; } = new();
}

public class TokenCountServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
}

public class OCRServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string DefaultModelId { get; set; } = "chandraocr";
}

public class EmbeddingServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = "BAAI/bge-m3";
    public string? ApiKey { get; set; }
    public int Dimension { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 30;
}