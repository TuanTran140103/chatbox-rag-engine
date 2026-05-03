namespace MarkdownGenQAs.Application.Dto.User.Dataset;

public class DatasetItemsResponseDto
{
    public string Path { get; set; } = string.Empty;
    public int Level { get; set; }
    public bool HasChildren { get; set; }
    public int ChildCount { get; set; }
    public List<DatasetItemDto> Items { get; set; } = [];
}
