namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IAuditUserAccessor
{
    Guid? GetCurrentUserId();
    bool IsAdmin();
}
