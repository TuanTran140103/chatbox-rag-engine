namespace MarkdownGenQAs.Application.Dto.Admin.Stats;

/// <summary>
/// System-wide summary for dashboard overview.
/// </summary>
public record SystemStatsSummaryDto(
    int TotalDatasets,
    int TotalDocuments,
    string TotalStorageDisplay,
    int TotalOUs,
    int TotalUsers
);
