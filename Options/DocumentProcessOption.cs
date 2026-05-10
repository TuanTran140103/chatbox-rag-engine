namespace GenQAServer.Options;

public class DocumentProcessOption
{
    public const string NameSection = "DocumentProcess";
    public int MaxChunkSize { get; set; } = 8192;
    public int MaxHeaderDepth { get; set; } = 5;
    public int SummaryChunkMaxTokens { get; set; } = 100000;
    public int MaxExtractionPages { get; set; } = 30;
    public int MetadataExtractionPageBatchSize { get; set; } = 10;
    public required string SelectionModel { get; set; }
    public required string ExtractionModel { get; set; }
}