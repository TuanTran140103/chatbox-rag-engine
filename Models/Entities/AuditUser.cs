using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

/// <summary>
/// Provides user audit fields only.
/// Time fields are in IAuditTime (implemented by BaseEntity).
/// </summary>
public abstract class AuditUser : IAuditUser
{
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }
}