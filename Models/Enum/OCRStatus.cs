namespace MarkdownGenQAs.Models.Enum;


// "pending | processing | success | failed | canceled",
public enum OCRStatus
{
    Pending,
    Triggered,
    Processing,
    Success,
    Failed,
    Canceled
}