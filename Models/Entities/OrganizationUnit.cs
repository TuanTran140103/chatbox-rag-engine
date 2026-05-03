using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class OrganizationUnit : BaseEntity, IAuditUser, IAuditDelete
{
    // IAuditUser
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }

    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required string Name { get; set; }
    public string? Code { get; set; }

    public Guid? ParentId { get; set; }
    public OrganizationUnit? Parent { get; set; }

    public string Path { get; set; } = string.Empty;
    public int Level { get; set; }

    public List<OrganizationUnit>? Children { get; set; }
    public List<UserPosition>? UserPositions { get; set; }
    public List<Dataset>? Datasets { get; set; }
}
