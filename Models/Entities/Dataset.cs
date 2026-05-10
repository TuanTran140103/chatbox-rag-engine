using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class Dataset : BaseEntity, IAuditUser, IAuditDelete
{
    // IAuditUser
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }

    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public int CountDocument { get; set; }

    public required Guid OwnerUserId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;

    public Guid? OUId { get; set; }
    public OrganizationUnit? OrganizationUnit { get; set; }

    public bool IsPublicToUnit { get; set; }

    public Guid? TemplateMetadataId { get; set; }
    public TemplateMetadata? TemplateMetadata { get; set; }

    public List<DatasetItem>? Items { get; set; }
    public List<AccessShare>? AccessShares { get; set; }
}
