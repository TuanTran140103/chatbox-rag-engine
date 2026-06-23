using System.Security.Claims;
using System.Text.Json;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Infrastructure.Middleware;

public class KongUserMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<KongUserMiddleware> _logger;

    public KongUserMiddleware(RequestDelegate next, ILogger<KongUserMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Sub", out var subValues) && subValues.Count > 0)
        {
            var sub = subValues[0];
            if (Guid.TryParse(sub, out var userId))
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, userId.ToString()),
                    new(ClaimTypes.Name, context.Request.Headers["X-Name"].FirstOrDefault() ?? ""),
                    new(ClaimTypes.Email, context.Request.Headers["X-Email"].FirstOrDefault() ?? ""),
                    new("jti", context.Request.Headers["X-Jti"].FirstOrDefault() ?? "")
                };

                var rolesValues = context.Request.Headers["X-Role"];
                foreach (var roleValue in rolesValues)
                {
                    if (string.IsNullOrEmpty(roleValue)) continue;

                    // Try JSON array first (e.g., ["Admin","User"])
                    if (roleValue.StartsWith('[') && roleValue.EndsWith(']'))
                    {
                        try
                        {
                            var parsed = JsonSerializer.Deserialize<List<string>>(roleValue);
                            if (parsed != null)
                            {
                                foreach (var role in parsed)
                                    claims.Add(new(ClaimTypes.Role, role));
                                continue;
                            }
                        }
                        catch (JsonException) { }
                    }

                    // Fallback: comma-separated
                    foreach (var role in roleValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        claims.Add(new(ClaimTypes.Role, role));
                    }
                }

                var departmentsJson = context.Request.Headers["X-Departments"].FirstOrDefault();
                if (!string.IsNullOrEmpty(departmentsJson))
                {
                    claims.Add(new("departments", departmentsJson));
                }

                var identity = new ClaimsIdentity(claims, "Kong");
                context.User = new ClaimsPrincipal(identity);

                var roleClaims = claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
                // _logger.LogWarning(
                //     "Kong user set: {UserId} / {Name} | X-Role raw: {RawRole} | Parsed roles: {Roles} | X-Departments raw: {DepartmentsRaw} | departments claim added: {HasClaim}",
                //     userId,
                //     context.Request.Headers["X-Name"].FirstOrDefault(),
                //     context.Request.Headers["X-Role"].FirstOrDefault(),
                //     string.Join(", ", roleClaims),
                //     departmentsJson ?? "<missing>",
                //     !string.IsNullOrEmpty(departmentsJson));
            }
            else
            {
                _logger.LogWarning("Invalid X-Sub header value: {Sub}", sub);
            }
        }

        await _next(context);
    }
}
