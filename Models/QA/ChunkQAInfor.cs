using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.QA;

public class ChunkQA : QA
{
    [Description("Thể loại câu hỏi")]
    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

public class ChunkQAInfor
{
    [JsonPropertyName("chunk_infor")]
    public required ChunkInfo ChunkInfo { get; set; }
    [JsonPropertyName("qas")]
    public required List<ChunkQA> QAs { get; set; }
}

