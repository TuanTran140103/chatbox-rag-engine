using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class ConversationThread : BaseEntity, IAuditDelete
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public required string Title { get; set; }

    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }
}
