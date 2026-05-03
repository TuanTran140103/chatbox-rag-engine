namespace MarkdownGenQAs.Application.Dto.Admin.Org;

/// <summary>
/// Recursive OU tree structure for admin view.
/// </summary>
public record OrgTreeDto(
    Guid Id,
    string Name,
    string? Code,
    int Level,
    int TotalUsers,
    int TotalDatasets,
    List<OrgTreeDto> Children
);
