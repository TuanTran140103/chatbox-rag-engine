using System.Security.Claims;

namespace MarkdownGenQAs.Infrastructure.Middleware;

public class GatewayUserMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _gatewaySecret;
    private readonly IWebHostEnvironment _env;

    public GatewayUserMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IWebHostEnvironment env)
    {
        _next = next;
        _gatewaySecret = configuration["Internal:GatewaySecret"];
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var userIdHeader = context.Request.Headers["X-User-Id"].FirstOrDefault();
            if (Guid.TryParse(userIdHeader, out var userId) && IsTrusted(context))
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, userId.ToString()),
                };
                var identity = new ClaimsIdentity(claims, "GatewayUser");
                context.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(context);
    }

    private bool IsTrusted(HttpContext context)
    {
        if (_env.IsDevelopment())
            return true;

        if (string.IsNullOrEmpty(_gatewaySecret))
            return false;

        var secret = context.Request.Headers["X-Gateway-Secret"].FirstOrDefault();
        return secret == _gatewaySecret;
    }
}
