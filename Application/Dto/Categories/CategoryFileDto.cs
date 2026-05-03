using MarkdownGenQAs.Application.Dto.Documents;

namespace MarkdownGenQAs.Application.Dto.Categories;

public record CategoryFileDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<DocumentListDto>? Documents { get; set; }
}
