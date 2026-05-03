namespace MarkdownGenQAs.Application.Dto.Admin.Org;

/// <summary>
/// Flattened user info with OU name embedded for admin view.
/// </summary>
public record OrgUserDto(
    Guid UserId,
    string Email,
    string UserName,
    Guid OUId,
    string OUName,
    string Role,
    bool IsPrimary,
    DateTime JoinedAt,
    string? ManagerName
);
