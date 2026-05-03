namespace MarkdownGenQAs.Application.Dto.User;

public record UserOrgTreeDto(
    Guid Id,
    string Name,
    string? Code,
    int Level,
    int TotalUsers,
    int TotalDatasets,
    bool IsMember,
    bool IsManager,
    List<UserOrgTreeDto> Children
);
