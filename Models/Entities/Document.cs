using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class Document : BaseEntity, IAuditUser, IAuditDelete
{
    // IAuditUser
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }

    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required string FileName { get; set; }

    public required string ObjectKeyFilePdf { get; set; }

    public StatusDocument Status { get; set; } = StatusDocument.Uploaded;
    public int ProcessingTimeOcr { get; set; }
    public int ProcessingTimeGenQa { get; set; }
    public bool IsOcred { get; set; }
    public bool IsQaGenerated { get; set; }
    public int OcrCount { get; set; }
    public int GenQaCount { get; set; }
    public Guid? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public DateTime? OcrStartedAt { get; set; }
    public DateTime? OcrCompletedAt { get; set; }
    public DateTime? GenQaStartedAt { get; set; }
    public DateTime? GenQaCompletedAt { get; set; }

    public Guid? DatasetItemId { get; set; }
    public DatasetItem? DatasetItem { get; set; }
    public LogMessage? LogMessage { get; set; }
    public DocumentJob? DocumentJob { get; set; }

    public string? OcrContent { get; set; }
    public string? QaContent { get; set; }
    public string? SummaryContent { get; set; }
    public string? QaSummaryContent { get; set; }
    public string? MetadataContent { get; set; }
    public bool IsMetadataExtracted { get; set; }
    public string? MetadataError { get; set; }
}
