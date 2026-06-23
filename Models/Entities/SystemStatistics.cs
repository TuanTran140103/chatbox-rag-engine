using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public class SystemStatistics : BaseEntity, IAuditDelete
{
    public Guid? DepartmentId { get; set; }

    public int TotalDatasets { get; set; }
    public int TotalDocuments { get; set; }
    public long TotalStorageUsage { get; set; }

    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }
}