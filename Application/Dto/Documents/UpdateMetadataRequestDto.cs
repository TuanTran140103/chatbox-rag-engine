namespace MarkdownGenQAs.Application.Dto.Documents;

public class UpdateMetadataRequestDto
{
    public required string MetadataContent { get; set; }
    public bool IsExtracted { get; set; }
}
