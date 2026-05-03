using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class AccessShare : BaseEntity, IAuditUser, IAuditDelete
{
    // IAuditUser
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }

    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required Guid DatasetId { get; set; }
    public Dataset Dataset { get; set; } = null!;

    public Guid? DatasetItemId { get; set; }
    public DatasetItem? DatasetItem { get; set; }

    public Guid? ShareToUserId { get; set; }
    public ApplicationUser? ShareToUser { get; set; }

    public Guid? ShareToOUId { get; set; }
    public OrganizationUnit? ShareToOU { get; set; }

    public DatasetPermissions PermissionMask { get; set; } = DatasetPermissions.Read;

    public required Guid GrantedBy { get; set; }
    public ApplicationUser? Grantor { get; set; }
}
