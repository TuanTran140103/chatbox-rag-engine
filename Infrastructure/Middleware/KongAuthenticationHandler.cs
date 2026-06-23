using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Infrastructure.Middleware;

public class KongAuthenticationHandler : AuthenticationHandler<KongAuthenticationSchemeOptions>
{
    public KongAuthenticationHandler(
        IOptionsMonitor<KongAuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.User.Identity?.IsAuthenticated == true)
        {
            var ticket = new AuthenticationTicket(Context.User, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        Logger.LogWarning("Unauthorized request to {Path} - no valid Kong authentication", Context.Request.Path);
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

public class KongAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
}