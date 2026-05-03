namespace MarkdownGenQAs.Application.Dto.Admin.Stats;

/// <summary>
/// Per-OU storage breakdown for chart visualization.
/// </summary>
public record StorageChartDto(
    Guid? OUId,
    string OUName,
    int DatasetCount,
    int DocumentCount,
    string StorageDisplay,
    long StorageBytes,
    double Percentage
);
