using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.Enum;

public enum TypeChunk
{
    [Description("Table")]
    [JsonStringEnumMemberName("table")]
    Table,
    [Description("Text a chunk")]
    [JsonStringEnumMemberName("allContent")]
    Text,
    [Description("Summary")]
    [JsonStringEnumMemberName("summary")]
    Summary
}