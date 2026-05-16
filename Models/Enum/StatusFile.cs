namespace MarkdownGenQAs.Models.Enum;

public enum StatusDocument
{
    Uploaded,
    Failed,
    Successed,
    ProcessingOcr,
    ProcessingGenQa,
    ProcessingIndexing,
    ProcessingMetadata,
    Canceled
}