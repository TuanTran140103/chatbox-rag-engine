using System.Security.Claims;
using System.Text.Json;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Enum;
using Microsoft.Extensions.Logging;

namespace MarkdownGenQAs.Utils;

public static class ClaimsPrincipalExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static List<KongUserDepartment> GetDepartments(this ClaimsPrincipal user, ILogger? logger = null)
    {
        var departmentsJson = user.FindFirstValue("departments");
        if (string.IsNullOrEmpty(departmentsJson))
        {
            logger?.LogWarning("[GetDepartments] 'departments' claim is missing or empty on user {UserId}",
                user.FindFirstValue(ClaimTypes.NameIdentifier));
            return [];
        }

        try
        {
            var result = JsonSerializer.Deserialize<List<KongUserDepartment>>(departmentsJson, JsonOptions) ?? [];
            logger?.LogDebug("[GetDepartments] parsed {Count} department(s) from claim (length={Length}, preview={Preview})",
                result.Count, departmentsJson.Length, Preview(departmentsJson));
            return result;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex,
                "[GetDepartments] failed to deserialize 'departments' claim for user {UserId} (length={Length}, preview={Preview}): {Error}",
                user.FindFirstValue(ClaimTypes.NameIdentifier), departmentsJson.Length, Preview(departmentsJson), ex.Message);
            return [];
        }
    }

    public static List<Guid> GetDepartmentIds(this ClaimsPrincipal user, ILogger? logger = null)
    {
        return user.GetDepartments(logger).Select(d => d.Id).ToList();
    }

    public static bool IsInDepartment(this ClaimsPrincipal user, Guid departmentId, ILogger? logger = null)
    {
        return user.GetDepartments(logger).Any(d => d.Id == departmentId);
    }

    public static DepartmentRole? GetDepartmentRole(this ClaimsPrincipal user, Guid departmentId, ILogger? logger = null)
    {
        return user.GetDepartments(logger).FirstOrDefault(d => d.Id == departmentId)?.Role;
    }

    private static string Preview(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
