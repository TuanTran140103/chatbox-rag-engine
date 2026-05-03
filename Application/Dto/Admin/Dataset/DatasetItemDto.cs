namespace MarkdownGenQAs.Application.Dto.Admin.Dataset;

/// <summary>
/// Dataset item (folder/document) tree node with HasChildren indicator.
/// </summary>
public record DatasetItemDto(
    Guid Id,
    string Name,
    string ItemType,
    bool HasChildren,
    string? SizeDisplay,
    long? SizeBytes,
    int ChildCount
);
