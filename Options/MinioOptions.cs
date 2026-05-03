namespace MarkdownGenQAs.Options;

public class MinioOptions
{
    public const string SectionName = "MinIO";

    /// <summary>
    /// Public URL endpoint for accessing MinIO objects
    /// Example: http://192.168.1.4:9000
    /// </summary>
    public string PublicEndpoint { get; set; } = string.Empty;
}
