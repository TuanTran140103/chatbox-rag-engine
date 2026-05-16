namespace MarkdownGenQAs.Application.Dto.Search;

public class SearchByNameRequest
{
    public required string QueryText { get; set; }
    public List<Guid>? DatasetIds { get; set; }
}
