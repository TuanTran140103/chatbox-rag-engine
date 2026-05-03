using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Dto.Admin.Org;

/// <summary>
/// User position assignment details.
/// </summary>
public record UserPositionDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string Email,
    Guid OUId,
    string OUName,
    OrganizationRole Role,
    bool IsPrimary,
    DateTime JoinedAt,
    Guid? ManagerId,
    string? ManagerName,
    string? ManagerEmail
);
