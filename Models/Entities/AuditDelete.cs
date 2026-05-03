using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

/// <summary>
/// Provides soft delete audit fields.
/// Only populated when user performs delete action.
/// </summary>
public abstract class AuditDelete : IAuditDelete
{
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; } = false;
}