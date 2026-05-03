namespace MarkdownGenQAs.Application.Dto.Admin.Stats;

public record StorageTreeDto(
    Guid Id,
    string Name,
    string? Code,
    int Level,
    int TotalDatasets,
    int TotalDocuments,
    long TotalStorageBytes,
    string StorageDisplay,
    List<StorageTreeDto> Children
);
