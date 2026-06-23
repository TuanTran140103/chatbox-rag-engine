namespace MarkdownGenQAs.Application.Dto.Admin;

public class OrphanFileDto
{
    public string ObjectKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
}

public class OrphanCleanupResultDto
{
    public int DeletedCount { get; set; }
    public List<string> DeletedFiles { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
