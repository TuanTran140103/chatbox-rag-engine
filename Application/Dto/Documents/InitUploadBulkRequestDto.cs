namespace MarkdownGenQAs.Application.Dto.Documents;

public class BulkFileInfoDto
{
    public required string FileName { get; set; }
    public required long FileSize { get; set; }
    public string? ContentType { get; set; }
}

public class InitUploadBulkRequestDto
{
    public required List<BulkFileInfoDto> Files { get; set; }
}
