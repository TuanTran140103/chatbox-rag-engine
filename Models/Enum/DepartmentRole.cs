using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.Enum;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepartmentRole
{
    Staff,
    Manager
}
