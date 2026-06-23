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
using Microsoft.AspNetCore.HttpOverrides;
using MarkdownGenQAs.Infrastructure.Middleware;

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
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();

builder.Services.AddAuthentication("Kong").AddScheme<KongAuthenticationSchemeOptions, KongAuthenticationHandler>("Kong", null);

// Cấu hình thời gian chờ shutdown graceful
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
// app.UseHttpsRedirection(); // Disabled for development
app.UseMiddleware<KongUserMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Hangfire Dashboard
app.UseHangfireDashboard(hangfireOptions.DashboardPath, new DashboardOptions
{
    DashboardTitle = hangfireOptions.DashboardTitle,
    AppPath = "/",
    StatsPollingInterval = 5000
});

Log.Information("Hangfire Dashboard available at: {DashboardPath}", hangfireOptions.DashboardPath);

// Register Recurring Jobs
RecurringJob.RemoveIfExists("cleanup-old-logs");
RecurringJob.AddOrUpdate("cleanup-old-cache", () => DocumentHelper.CleanupOldCache(), Cron.Daily);

var orphanCleanupJobId = "cleanup-orphan-files";
RecurringJob.RemoveIfExists(orphanCleanupJobId);
RecurringJob.AddOrUpdate<IOrphanFileCleanupService>(orphanCleanupJobId,
    x => x.CleanupOrphanFilesAsync(CancellationToken.None), Cron.Daily);

var stuckUploadsJobId = "cleanup-stuck-uploads";
RecurringJob.RemoveIfExists(stuckUploadsJobId);
RecurringJob.AddOrUpdate<IOrphanFileCleanupService>(stuckUploadsJobId,
    x => x.CleanupStuckUploadingDocumentsAsync(CancellationToken.None), Cron.Hourly);

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