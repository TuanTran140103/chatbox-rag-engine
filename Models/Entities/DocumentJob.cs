
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class DocumentJob : BaseEntity, IAuditDelete
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public string? OcrJobId { get; set; }
    public string? GenQaJobId { get; set; }
    public StatusJob? StatusOcr { get; set; }
    public StatusJob? StatusGenQa { get; set; }
    public StatusJob? StatusMetadata { get; set; }
    public string? MetadataError { get; set; }

    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }
}
