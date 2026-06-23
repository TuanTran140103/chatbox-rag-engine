using System.Runtime.CompilerServices;

namespace MarkdownGenQAs.Utils;

public static class UserContextLogger
{
    public static void LogUserDepartments(
        this ILogger logger,
        Guid? userId,
        IReadOnlyCollection<Guid> departmentIds,
        Guid? requestedDepartmentId = null,
        [CallerMemberName] string? action = null)
    {
        var ids = departmentIds.Count > 0
            ? string.Join(", ", departmentIds)
            : "<empty>";

        if (requestedDepartmentId.HasValue)
        {
            var match = departmentIds.Contains(requestedDepartmentId.Value);
            logger.LogInformation(
                "[{Action}] userId={UserId} departments=[{Ids}] requested={Requested} match={Match}",
                action, userId ?? Guid.Empty, ids, requestedDepartmentId.Value, match);
        }
        else
        {
            logger.LogInformation(
                "[{Action}] userId={UserId} departments=[{Ids}]",
                action, userId ?? Guid.Empty, ids);
        }
    }
}
