using MarkdownGenQAs.Models.QA;

namespace MarkdownGenQAs.Application.Dto.Documents;

public record DocumentDetailDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ProcessingTimeOcr { get; set; }
    public int ProcessingTimeGenQa { get; set; }
    public bool IsOcred { get; set; }
    public bool IsQaGenerated { get; set; }
    public int OcrCount { get; set; }
    public int GenQaCount { get; set; }

    public Guid? UserId { get; set; }
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DocumentContent Content { get; set; } = new();
}

public record DocumentContent
{
    public string? OcrMarkdown { get; set; }
    public List<ChunkQAInfor>? QAs { get; set; }
    public string? Summary { get; set; }
}