using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.Auth;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Application.Service;

public class AuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<AuthService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAppCacheService _appCache;
    private readonly AuthOptions _authOptions;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthService> logger,
        IHttpContextAccessor httpContextAccessor,
        IAppCacheService appCache,
        IOptions<AuthOptions> authOptions)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _appCache = appCache;
        _authOptions = authOptions.Value;
    }

    private HttpContext HttpContext => _httpContextAccessor.HttpContext!;

    public async Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginRequestDto dto)
    {
        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user == null)
        {
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = false,
                ErrorMessage = "Invalid login attempt."
            };
        }

        var result = await _signInManager.PasswordSignInAsync(user, dto.Password, dto.RememberMe, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            var userRoles = await _userManager.GetRolesAsync(user);
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = true,
                Data = new AuthResponseDto
                {
                    Email = user.Email!,
                    UserName = user.UserName!,
                    IsAuthenticated = true,
                    Roles = userRoles.ToList()
                }
            };
        }

        return new ServiceResult<AuthResponseDto>
        {
            IsSuccess = false,
            ErrorMessage = "Invalid login attempt."
        };
    }

    private static readonly System.Text.RegularExpressions.Regex _emailRegex = new(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<ServiceResult<AuthResponseDto>> RegisterAsync(RegisterRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email) || !_emailRegex.IsMatch(dto.Email))
        {
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = false,
                ErrorMessage = "Invalid email format. Only English characters are allowed."
            };
        }

        if (dto.Password != dto.ConfirmPassword)
        {
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = false,
                ErrorMessage = "Passwords do not match."
            };
        }

        var existingUser = await _userManager.FindByEmailAsync(dto.Email);
        if (existingUser != null)
        {
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = false,
                ErrorMessage = "Email already exists."
            };
        }

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email
        };

        var result = await _userManager.CreateAsync(user, dto.Password);

        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
            await _appCache.RemoveByPrefixAsync("user:");
            var roles = await _userManager.GetRolesAsync(user);
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = true,
                Data = new AuthResponseDto
                {
                    Email = user.Email,
                    UserName = user.UserName,
                    IsAuthenticated = true,
                    Roles = roles.ToList()
                }
            };
        }

        return new ServiceResult<AuthResponseDto>
        {
            IsSuccess = false,
            ErrorMessage = string.Join(", ", result.Errors.Select(e => e.Description))
        };
    }

    public async Task<ServiceResult<AuthResponseDto>> HandleExternalLoginCallbackAsync()
    {
        var authenticateResult = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        if (authenticateResult.Succeeded && authenticateResult.Principal != null)
        {
            var claims = authenticateResult.Principal.Claims.ToList();

            var email = authenticateResult.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email))
            {
                email = authenticateResult.Principal.FindFirstValue("email");
            }

            var providerKey = authenticateResult.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var providerDisplayName = authenticateResult.Principal.FindFirstValue("name") ?? "Authentik";
            var userName = authenticateResult.Principal.FindFirstValue("preferred_username") ?? email;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(providerKey))
            {
                return new ServiceResult<AuthResponseDto>
                {
                    IsSuccess = false,
                    ErrorMessage = "Email or ProviderKey not found from external provider."
                };
            }

            var user = await _userManager.FindByEmailAsync(email);
            _logger.LogInformation("FindByEmailAsync result: {User}", user == null ? "NULL" : user.UserName);

            var loginProvider = "Authentik";

            if (user == null)
            {
                _logger.LogInformation("Creating new user with email: {Email}", email);
                user = new ApplicationUser
                {
                    UserName = userName ?? email,
                    Email = email
                };
                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    _logger.LogError("Error creating user: {Errors}",
                        string.Join(", ", createResult.Errors.Select(e => e.Description)));
                    return new ServiceResult<AuthResponseDto>
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Error creating user: {string.Join(", ", createResult.Errors.Select(e => e.Description))}"
                    };
                }
                _logger.LogInformation("User created with ID: {Id}", user.Id);
                await _appCache.RemoveByPrefixAsync("user:");

                var addLoginResult = await _userManager.AddLoginAsync(user, new UserLoginInfo(loginProvider, providerKey, providerDisplayName));
                if (!addLoginResult.Succeeded)
                {
                    _logger.LogError("Error adding login: {Errors}",
                        string.Join(", ", addLoginResult.Errors.Select(e => e.Description)));
                }
                else
                {
                    _logger.LogInformation("Login added to new user");
                }
            }
            else
            {
                var logins = await _userManager.GetLoginsAsync(user);
                _logger.LogInformation("User existing logins: {Logins}",
                    string.Join(", ", logins.Select(l => $"{l.LoginProvider}={l.ProviderKey}")));

                if (!logins.Any(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey))
                {
                    var addLoginResult = await _userManager.AddLoginAsync(user, new UserLoginInfo(loginProvider, providerKey, providerDisplayName));
                    if (!addLoginResult.Succeeded)
                    {
                        _logger.LogWarning("Could not add login to existing user: {Errors}",
                            string.Join(", ", addLoginResult.Errors.Select(e => e.Description)));
                    }
                    else
                    {
                        _logger.LogInformation("Login added to existing user");
                    }
                }
            }

            var idToken = authenticateResult.Properties?.GetTokenValue("id_token");

            await _signInManager.ExternalLoginSignInAsync(loginProvider, providerKey, isPersistent: false);

            var userPrincipal = await _signInManager.CreateUserPrincipalAsync(user);
            if (!string.IsNullOrEmpty(idToken))
            {
                var identity = (ClaimsIdentity)userPrincipal.Identity!;
                identity.AddClaim(new Claim("id_token_hint", idToken));
                _logger.LogInformation("id_token_hint added to user claims.");
            }

            await HttpContext.SignInAsync(
                IdentityConstants.ApplicationScheme,
                userPrincipal,
                new AuthenticationProperties { IsPersistent = false });

            _logger.LogInformation("Sign in successful for user: {User}", user.UserName);

            var userRoles = await _userManager.GetRolesAsync(user);
            return new ServiceResult<AuthResponseDto>
            {
                IsSuccess = true,
                Data = new AuthResponseDto
                {
                    Email = user.Email!,
                    UserName = user.UserName!,
                    IsAuthenticated = true,
                    Roles = userRoles.ToList()
                }
            };
        }

        _logger.LogWarning("Identity.External authentication failed: {Failure}", authenticateResult.Failure);
        return new ServiceResult<AuthResponseDto>
        {
            IsSuccess = false,
            ErrorMessage = authenticateResult.Failure?.Message ?? "Error loading external login information."
        };
    }

    public async Task LogoutAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
