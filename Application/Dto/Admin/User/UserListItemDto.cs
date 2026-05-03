namespace MarkdownGenQAs.Application.Dto.Admin.User;

public record UserListItemDto(
    Guid UserId,
    string Email,
    string UserName,
    bool EmailConfirmed,
    List<string> OrganizationUnitNames
);
