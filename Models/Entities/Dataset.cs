using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class Dataset : BaseEntity, IAuditUser, IAuditDelete
{
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }

    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public int CountDocument { get; set; }

    public required Guid OwnerUserId { get; set; }

    public Guid? DepartmentId { get; set; }

    public Guid? TemplateMetadataId { get; set; }
    public TemplateMetadata? TemplateMetadata { get; set; }

    public List<DatasetItem>? Items { get; set; }
    public List<AccessShare>? AccessShares { get; set; }
}
