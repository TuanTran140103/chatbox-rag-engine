using System.Text.Json.Serialization;
using MarkdownGenQAs.Models.Enum;

namespace MarkdownGenQAs.Models;

public class KongUserDepartment
{
    [JsonPropertyName("Id")]
    public Guid Id { get; set; }

    [JsonPropertyName("IsPrimary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("Role")]
    public DepartmentRole Role { get; set; }
}
