using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public abstract class BaseEntity : IAuditTime
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
