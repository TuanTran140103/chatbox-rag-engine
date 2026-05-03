namespace MarkdownGenQAs.Models.Entities;

/// <summary>
/// System-wide statistics for dashboard. One row per OU + 1 row for company-wide (OUId = NULL).
/// </summary>
public class SystemStatistics
{
    public int Id { get; set; }

    /// <summary>
    /// NULL = Company-wide statistics
    /// </summary>
    public Guid? OUId { get; set; }

    public int TotalDatasets { get; set; }
    public int TotalDocuments { get; set; }
    public long TotalStorageUsage { get; set; } // Byte

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public OrganizationUnit? OU { get; set; }
}
