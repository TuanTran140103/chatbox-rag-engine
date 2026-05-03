

namespace MarkdownGenQAs.Options;

public class ExternalServiceOptions
{
    public const string SectionName = "ExternalServices";
    public TokenCountServiceOptions TokenCountService { get; set; } = new();
    public OCRServiceOptions OCRService { get; set; } = new();
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