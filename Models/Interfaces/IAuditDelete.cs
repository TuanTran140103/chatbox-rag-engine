namespace MarkdownGenQAs.Models.Interfaces;

/// <summary>
/// Soft delete audit fields - entities implementing this support soft delete.
/// Only set when user performs delete action (soft delete by default, hard delete by Admin).
/// </summary>
public interface IAuditDelete
{
    DateTime? DeletedAt { get; set; }
    Guid? DeletedBy { get; set; }
    bool IsDeleted { get; set; }
}