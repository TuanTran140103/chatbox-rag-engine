namespace MarkdownGenQAs.Models.Interfaces;

/// <summary>
/// User audit fields - entities implementing this track who created/modified.
/// NO time fields - those are in IAuditTime.
/// </summary>
public interface IAuditUser
{
    Guid? CreatedBy { get; set; }
    Guid? ModifiedBy { get; set; }
}