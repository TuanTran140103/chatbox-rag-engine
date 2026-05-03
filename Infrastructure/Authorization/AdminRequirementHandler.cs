using System.Security.Claims;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;

namespace MarkdownGenQAs.Infrastructure.Authorization;

public class AdminRequirementHandler : AuthorizationHandler<AdminRequirement>
{
    private readonly IAccessControlService _accessControl;
    private readonly ILogger<AdminRequirementHandler> _logger;

    public AdminRequirementHandler(
        IAccessControlService accessControl,
        ILogger<AdminRequirementHandler> logger)
    {
        _accessControl = accessControl;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRequirement requirement)
    {
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userIdClaim))
        {
            _logger.LogDebug("Admin authorization failed: NameIdentifier claim not found");
            return;
        }

        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            _logger.LogDebug("Admin authorization failed: Invalid NameIdentifier format");
            return;
        }

        var isAdmin = await _accessControl.IsAdminAsync(userId);
        if (isAdmin)
        {
            _logger.LogDebug("Admin authorization succeeded for user {UserId}", userId);
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogDebug("Admin authorization failed: User {UserId} is not admin", userId);
        }
    }
}
