using System.Security.Claims;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Utils;

namespace MarkdownGenQAs.Infrastructure.Services;

public class AuditUserAccessor : IAuditUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? GetCurrentUserId()
    {
        var claim = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    public bool IsAdmin()
    {
        return _httpContextAccessor.HttpContext?.User.IsInRole("Admin") ?? false;
    }

    public List<Guid> GetUserDepartmentIds()
    {
        return _httpContextAccessor.HttpContext?.User.GetDepartmentIds() ?? [];
    }
}