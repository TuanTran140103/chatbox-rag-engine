namespace MarkdownGenQAs.Application.Dto.Admin.Org;

public record UserManagerDto(
    Guid ManagerId,
    string ManagerName,
    string ManagerEmail,
    Guid OUId,
    string OUName
);
