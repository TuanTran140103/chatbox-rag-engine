namespace MarkdownGenQAs.Application.Dto.Documents;

public class InitUploadResponseDto
{
    public Guid DocumentId { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string PresignedUrl { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
