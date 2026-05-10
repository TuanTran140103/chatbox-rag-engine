using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.QA;

public class ChunkQA : QA
{
    [Description("Thể loại câu hỏi")]
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [Description("Phân loại QA: text hoặc table")]
    [JsonPropertyName("qa_type")]
    public string? QaType { get; set; }
}

public class ChunkQAInfor
{
    [JsonPropertyName("chunk_infor")]
    public required ChunkInfo ChunkInfo { get; set; }
    [JsonPropertyName("qas")]
    public required List<ChunkQA> QAs { get; set; }
}

