namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public class DatasetItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public Guid? DocumentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DatasetItemDocumentDto? Item { get; set; }
}

public record DatasetItemDocumentDto(
    string FileName,
    string Status,
    bool IsOcred,
    bool IsQaGenerated,
    DocumentJobBriefDto? Job
);

public record DocumentJobBriefDto(
    string? OcrJobId,
    string? GenQaJobId,
    string? StatusOcr,
    string? StatusGenQa
);
