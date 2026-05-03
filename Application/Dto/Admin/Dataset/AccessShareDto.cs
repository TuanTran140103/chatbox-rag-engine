using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Application.Dto.Admin.Dataset;

/// <summary>
/// Access share details for admin dataset oversight.
/// </summary>
public record AccessShareDto(
    Guid Id,
    Guid DatasetId,
    Guid? DatasetItemId,
    Guid? ShareToUserId,
    string? ShareToUserName,
    Guid? ShareToOUId,
    string? ShareToOUName,
    DatasetPermissions PermissionMask,
    string PermissionDisplay,
    Guid GrantedBy,
    string GrantorName,
    DateTime GrantedAt
);
