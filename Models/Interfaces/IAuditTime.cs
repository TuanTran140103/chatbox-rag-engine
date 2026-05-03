namespace MarkdownGenQAs.Models.Interfaces;

/// <summary>
/// Time audit fields - all entities should implement this.
/// </summary>
public interface IAuditTime
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}