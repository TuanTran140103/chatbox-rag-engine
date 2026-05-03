namespace MarkdownGenQAs.Application.Dto.DocumentJobs;

public record DocumentJobDto
{
    public Guid DocumentId { get; set; }
    public string? OcrJobId { get; set; }
    public string? GenQaJobId { get; set; }
}
