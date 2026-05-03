using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class UserPosition : BaseEntity, IAuditDelete
{
    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public required Guid OUId { get; set; }
    public OrganizationUnit OrganizationUnit { get; set; } = null!;

    public OrganizationRole Role { get; set; } = OrganizationRole.Staff;
    public bool IsPrimary { get; set; }

    public Guid? ManagerId { get; set; }
    public ApplicationUser? Manager { get; set; }
}
