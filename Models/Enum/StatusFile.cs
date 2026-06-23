namespace MarkdownGenQAs.Models.Enum;

public enum StatusDocument
{
    Uploading,
    Uploaded,
    Failed,
    Succeeded,
    ProcessingOcr,
    ProcessingGenQa,
    ProcessingIndexing,
    ProcessingMetadata,
    Canceled
}