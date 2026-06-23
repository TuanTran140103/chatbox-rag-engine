using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.Enum;

public enum TypeChunk
{
    [JsonStringEnumMemberName("Table")]
    Table,
    [JsonStringEnumMemberName("Text")]
    Text,
    [JsonStringEnumMemberName("Summary")]
    Summary
}