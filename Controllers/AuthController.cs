using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MarkdownGenQAs.Options;
using MarkdownGenQAs.Application.Dto.Auth;
using MarkdownGenQAs.Application.Service;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ILogger<AuthController> _logger;
    private readonly AuthOptions _authOptions;
    private readonly AuthService _authService;

    public AuthController(
        ILogger<AuthController> logger, 
        IOptions<AuthOptions> authOptions,
        AuthService authService)
    {
        _logger = logger;
        _authOptions = authOptions.Value;
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto dto)
    {
        var result = await _authService.RegisterAsync(dto);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }
        return Ok(result.Data);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto dto)
    {
        var result = await _authService.LoginAsync(dto);
        if (!result.IsSuccess)
        {
            return Unauthorized(new { error = result.ErrorMessage });
        }
        return Ok(result.Data);
    }

    [HttpGet("login-oidc")]
    public IActionResult LoginOidc(string? returnUrl = null)
    {
        var redirectUri = Url.Action(nameof(LoginCallback), "Auth", new { returnUrl }, Request.Scheme);
        return Challenge(new AuthenticationProperties { RedirectUri = redirectUri }, OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("signin-oidc")] // This remains for the OIDC middleware redirect
    public async Task<IActionResult> LoginCallback(string? returnUrl = null)
    {
        var result = await _authService.HandleExternalLoginCallbackAsync();
        
        if (!result.IsSuccess)
        {
            _logger.LogError("External authentication failed: {Message}", result.ErrorMessage);
            return Redirect($"{_authOptions.FrontendBaseUrl.TrimEnd('/')}/login?error={Uri.EscapeDataString(result.ErrorMessage ?? "Unknown error")}");
        }

        var frontendBaseUrl = _authOptions.FrontendBaseUrl.TrimEnd('/');
        string redirectUrl;

        if (!string.IsNullOrEmpty(returnUrl))
        {
            if (returnUrl.StartsWith("http://") || returnUrl.StartsWith("https://"))
                redirectUrl = returnUrl;
            else
                redirectUrl = returnUrl.StartsWith('/')
                    ? $"{frontendBaseUrl}{returnUrl}"
                    : $"{frontendBaseUrl}/{returnUrl}";
        }
        else
        {
            redirectUrl = frontendBaseUrl;
        }

        return Redirect(redirectUrl);
    }

    [HttpGet("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var frontendUrl = $"{_authOptions.FrontendBaseUrl.TrimEnd('/')}/login";
        var idTokenHint = User.FindFirst("id_token_hint")?.Value;
        _logger.LogDebug("ID token hint: {IdTokenHint}", idTokenHint);
        if (!string.IsNullOrEmpty(idTokenHint))
        {
            return SignOut(
                new AuthenticationProperties { RedirectUri = frontendUrl },
                OpenIdConnectDefaults.AuthenticationScheme,
                IdentityConstants.ApplicationScheme
            );
        }

        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Redirect(frontendUrl);
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult GetCurrentUser()
    {
        return Ok(new
        {
            user = User.Identity?.Name,
            email = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Email)?.Value,
            isAuthenticated = User.Identity?.IsAuthenticated,
            roles = User.Claims
                .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList(),
            claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList()
        });
    }

    [HttpGet("signout-callback-oidc")]
    public IActionResult SignoutCallback()
    {
        var frontendBaseUrl = _authOptions.FrontendBaseUrl.TrimEnd('/');
        return Redirect($"{frontendBaseUrl}/login");
    }
}
