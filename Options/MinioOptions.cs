namespace MarkdownGenQAs.Options;

public class MinioOptions
{
    public const string SectionName = "MinIO";

    /// <summary>
    /// Public URL endpoint for accessing MinIO objects
    /// Example: http://192.168.1.4:9000
    /// </summary>
    public string PublicEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Credentials for the OCR server's MinIO read-only user.
    /// This user can only GetObject from the ocr-upload-pdf bucket.
    /// </summary>
    public MinioOcrUserOptions OcrUser { get; set; } = new();
}

public class MinioOcrUserOptions
{
    public string AccessKey { get; set; } = "ocr-reader";
    public string SecretKey { get; set; } = string.Empty;
}
