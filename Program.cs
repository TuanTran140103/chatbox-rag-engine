using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Options;
using DotNetEnv;
using StackExchange.Redis;
using Hangfire;
using Hangfire.Redis.StackExchange;
using Scalar.AspNetCore;
using Serilog;
using GenQAServer.Options;
using System.Text.Json.Serialization;
using GenQAServer.Infrastructure;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Infrastructure;

Env.Load();
var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .CreateLogger();

builder.Host.UseSerilog();

Log.Information("Application starting up...");

// Forwarded Headers Configuration for Nginx
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Hangfire Configuration
var hangfireOptions = builder.Configuration
    .GetRequiredSection(HangfireOptions.SectionName)
    .Get<HangfireOptions>()
    ?? throw new ArgumentNullException("Hangfire configuration is missing");


Log.Information("Configuration loaded successfully");

// Log các nguồn cấu hình để kiểm tra EnvironmentVariables có được nạp không
foreach (var source in ((IConfigurationRoot)builder.Configuration).Providers)
{
    Log.Debug("Config Source: {Source}", source.ToString());
}

// Service
Log.Information("Registering services...");
builder.Services.AddInfrastructureServices(builder.Configuration);
// Redis Connection for Pub/Sub and Cache
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(hangfireOptions.RedisConnection));

// Redis Cache
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = hangfireOptions.RedisConnection;
    options.InstanceName = "MarkdownGenQAs:";
});

Log.Information("Configuring Hangfire with Redis storage: {RedisConnection}", hangfireOptions.RedisConnection);

builder.Services.AddHangfire(config =>
{
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseRedisStorage(hangfireOptions.RedisConnection, new RedisStorageOptions
        {
            Prefix = "hangfire:markdowngenqas:",
            ExpiryCheckInterval = TimeSpan.FromHours(1),
            InvisibilityTimeout = TimeSpan.FromHours(1)
        });
});

// Tắt retry mặc định của Hangfire (mặc định 10 lần)
GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();

var workerCount = hangfireOptions.WorkerCount > 0
    ? hangfireOptions.WorkerCount
    : Environment.ProcessorCount * 2;

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = workerCount;
    options.ServerName = $"{Environment.MachineName}:MarkdownGenQAs";
    options.Queues = new[] { "critical", "default" };
});

Log.Information("Hangfire server configured with {WorkerCount} workers", workerCount);

// Add services to the container.
builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    options.AddPolicy("AllowAll",
        builder =>
        {
            builder.WithOrigins(allowedOrigins)
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .AllowCredentials();
        });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = false;
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<ApplicationContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "MarkdownGenQAs.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/api/auth/login";
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    var authority = Environment.GetEnvironmentVariable("AUTHENTIK__AUTHORITY")
        ?? throw new InvalidOperationException("AUTHENTIK__AUTHORITY is not set");
    var clientId = Environment.GetEnvironmentVariable("AUTHENTIK__CLIENTID")
        ?? throw new InvalidOperationException("AUTHENTIK__CLIENTID is not set");
    var clientSecret = Environment.GetEnvironmentVariable("AUTHENTIK__CLIENTSECRET")
        ?? throw new InvalidOperationException("AUTHENTIK__CLIENTSECRET is not set");

    Log.Information("Configuring OIDC with Authority: {Authority}", authority);

    options.Authority = authority;
    options.ClientId = clientId;
    options.ClientSecret = clientSecret;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SaveTokens = true;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.RequireHttpsMetadata = false;
    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";
    options.SignedOutRedirectUri = builder.Configuration["Auth:FrontendBaseUrl"] + "/login";
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = false,
        ValidateLifetime = true,
        NameClaimType = "preferred_username"
    };

    options.Events = new OpenIdConnectEvents
    {
        OnSignedOutCallbackRedirect = context =>
        {
            var frontendUrl = builder.Configuration["Auth:FrontendBaseUrl"]?.TrimEnd('/') + "/login";
            Log.Information("SignOut Callback reached. Redirecting to Frontend: {Url}", frontendUrl);

            context.Response.ContentType = "text/html";
            var html = $@"
                <html>
                <head><title>Logging out...</title></head>
                <body>
                    <script>
                        window.top.location.href = '{frontendUrl}';
                    </script>
                </body>
                </html>";
            
            return context.Response.WriteAsync(html);
        }
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.Requirements.Add(new MarkdownGenQAs.Infrastructure.Authorization.AdminRequirement()));
});

builder.Services.AddScoped<IAuthorizationHandler, MarkdownGenQAs.Infrastructure.Authorization.AdminRequirementHandler>();

// Auth Options
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// Cấu hình thời gian chờ shutdown ggraceful
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

Log.Information("All services registered successfully");

var app = builder.Build();

Log.Information("Application built successfully");

// Use Forwarded Headers for Nginx Proxy
app.UseForwardedHeaders();

// Initialize S3 Buckets
using (var scope = app.Services.CreateScope())
{
    var s3Service = scope.ServiceProvider.GetRequiredService<IS3Service>();
    await s3Service.InitializeBucketsAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    Log.Information("Running in Development environment");
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}
else
{
    Log.Information("Running in {Environment} environment", app.Environment.EnvironmentName);
}

app.UseSerilogRequestLogging();
app.UseCors("AllowAll");
// app.UseHttpsRedirection(); // Disabled for development
app.UseAuthentication();
app.UseMiddleware<MarkdownGenQAs.Infrastructure.Middleware.GatewayUserMiddleware>();
app.UseAuthorization();

app.MapControllers();

// Hangfire Dashboard
app.UseHangfireDashboard(hangfireOptions.DashboardPath, new DashboardOptions
{
    DashboardTitle = hangfireOptions.DashboardTitle,
    AppPath = "/",
    StatsPollingInterval = 5000
});

Log.Information("Hangfire Dashboard available at: {DashboardPath}", hangfireOptions.DashboardPath);

// Register Recurring Jobs
RecurringJob.RemoveIfExists("cleanup-old-logs"); // Remove old job that references deleted types
RecurringJob.AddOrUpdate("cleanup-old-cache", () => DocumentHelper.CleanupOldCache(), Cron.Daily);

Log.Information("Starting web application...");
Log.Information("Application is running. Press Ctrl+C to shut down.");

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.Information("Application shutting down...");
    Log.CloseAndFlush();
}
