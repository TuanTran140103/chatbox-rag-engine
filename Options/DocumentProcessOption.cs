namespace GenQAServer.Options;

public class DocumentProcessOption
{
    public const string NameSection = "DocumentProcess";
    public int MaxChunkSize { get; set; } = 8192;
    public int MaxHeaderDepth { get; set; } = 5;
    public required string SelectionModel { get; set; }
    public required string ExtractionModel { get; set; }
}