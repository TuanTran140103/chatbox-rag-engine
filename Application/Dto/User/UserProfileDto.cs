using MarkdownGenQAs.Application.Dto.Admin.Org;

namespace MarkdownGenQAs.Application.Dto.User;

public record UserProfileDto(
    Guid UserId,
    string Email,
    string UserName,
    bool EmailConfirmed,
    List<UserPositionDto> Positions,
    List<UserManagerDto> Managers
);
