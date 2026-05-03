using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;

namespace MarkdownGenQAs.Models.Entities;

public enum DatasetItemType
{
    Folder = 0,
    Document = 1
}

public class DatasetItem : BaseEntity, IAuditUser, IAuditDelete
{
    // IAuditUser
    public Guid? CreatedBy { get; set; }
    public Guid? ModifiedBy { get; set; }

    // IAuditDelete
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public bool IsDeleted { get; set; }

    public required Guid DatasetId { get; set; }
    public Dataset Dataset { get; set; } = null!;

    public required string Name { get; set; }
    public required DatasetItemType ItemType { get; set; }

    public required string Path { get; set; }
    public required int Level { get; set; }

    public Guid? ParentId { get; set; }
    public DatasetItem? Parent { get; set; }

    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }

    public int SortOrder { get; set; }
}