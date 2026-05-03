using Microsoft.AspNetCore.Identity;

namespace MarkdownGenQAs.Models.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
