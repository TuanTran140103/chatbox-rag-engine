using Amazon.S3;
using GenQAServer.Infrastructure.Factories;
using GenQAServer.Options;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Infrastructure.Repositories;
using MarkdownGenQAs.Infrastructure.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Options;
using MarkdownGenQAs.Application.Service;
using MarkdownGenQAs.Infrastructure.Services;
using Polly;
using Polly.Extensions.Http;

using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using MarkdownGenQAs.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenAI;
using Microsoft.Extensions.Options;
using System.ClientModel;
using MarkdownGenQAs.Infrastructure.Interceptors;

namespace GenQAServer.Infrastructure;

public static class DependencyInjection
{
    public static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExternalServiceOptions>(configuration.GetSection(ExternalServiceOptions.SectionName));
        services.Configure<LlmProviderOptions>(configuration.GetSection(LlmProviderOptions.SectionName));
        services.Configure<HangfireOptions>(configuration.GetSection(HangfireOptions.SectionName));
        services.Configure<DocumentProcessOption>(configuration.GetSection(DocumentProcessOption.NameSection));
        services.Configure<MinioOptions>(configuration.GetSection(MinioOptions.SectionName));
        services.Configure<SystemPrompts>(configuration.GetSection(SystemPrompts.SectionName));
        services.Configure<InitialSettings>(configuration.GetSection(InitialSettings.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SystemPrompts>>().Value);
    }
    public static void AddLlmClients(IServiceCollection services, IConfiguration configuration)
    {
        var llmOptions = new LlmProviderOptions();
        configuration.GetSection(LlmProviderOptions.SectionName).Bind(llmOptions);

        // Register the Factory (no interface)
        services.AddSingleton<LlmClientFactory>();

        foreach (var providerKv in llmOptions.Providers)
        {
            var providerName = providerKv.Key;
            var settings = providerKv.Value;

            foreach (var model in settings.Models)
            {
                var key = $"{providerName}__{model.ModelName}";
                var baseUrl = !string.IsNullOrEmpty(model.BaseUrl) ? model.BaseUrl : settings.BaseUrl;

                var apiKey = !string.IsNullOrEmpty(model.ApiKey) ? model.ApiKey : settings.ApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    apiKey = Environment.GetEnvironmentVariable($"LlmProviders__{providerName}__ApiKey") ?? string.Empty;
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    var envVarName = $"LlmProviders__{providerName}__ApiKey";
                    throw new InvalidOperationException(
                        $"ApiKey for provider '{providerName}', model '{model.ModelName}' is missing. " +
                        $"Set it in .env file as: {envVarName}=your_key");
                }

                services.AddKeyedSingleton<IChatClient>(key, (sp, k) =>
                {
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        throw new InvalidOperationException($"BaseUrl for model '{model.ModelName}' in provider '{providerName}' is not configured.");
                    }

                    var timeout = TimeSpan.FromSeconds(model.TimeoutSeconds);
                    var openAIClient = new OpenAIClient(
                        new ApiKeyCredential(apiKey),
                        new OpenAIClientOptions 
                        { 
                            Endpoint = new Uri(baseUrl),
                            NetworkTimeout = timeout
                        });
                    
                    return openAIClient.GetChatClient(model.ModelName).AsIChatClient();
                });
            }
        }
    }

    public static void AddRepositories(IServiceCollection services)
    {
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<ILogMessageRepository, LogMessageRepository>();
        services.AddScoped<IDocumentJobRepository, DocumentJobRepository>();
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddScoped<IDatasetItemRepository, DatasetItemRepository>();
        services.AddScoped<IAccessShareRepository, AccessShareRepository>();
        services.AddScoped<IOrganizationUnitRepository, OrganizationUnitRepository>();
        services.AddScoped<IUserPositionRepository, UserPositionRepository>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<NotificationService>();
        services.AddScoped<IGenQaBackgroundJobService, GenQaBackgroundJobService>();

        return services;
    }

    public static void AddHttpClients(IServiceCollection services, IConfiguration configuration)
    {
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        services.AddHttpClient<IOCRService, OCRService>((sp, client) =>
        {
            var options = configuration.GetSection(ExternalServiceOptions.SectionName).Get<ExternalServiceOptions>();
            if (options?.OCRService == null || string.IsNullOrEmpty(options.OCRService.BaseUrl))
            {
                throw new ArgumentNullException("OCRService:BaseUrl is missing in configuration");
            }
            client.BaseAddress = new Uri(options.OCRService.BaseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddPolicyHandler(retryPolicy);

        services.AddHttpClient<ITokenCountService, TokenCountService>((sp, client) =>
        {
            var options = configuration.GetSection(ExternalServiceOptions.SectionName).Get<ExternalServiceOptions>();
            if (options?.TokenCountService == null || string.IsNullOrEmpty(options.TokenCountService.BaseUrl))
            {
                throw new ArgumentNullException("TokenCountService:BaseUrl is missing in configuration");
            }
            client.BaseAddress = new Uri(options.TokenCountService.BaseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddPolicyHandler(retryPolicy);
    }

    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register DbContext
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        }

        // Audit services
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditUserAccessor, AuditUserAccessor>();
        services.AddSingleton<AuditEntityInterceptor>();

        // Register DbContext with audit interceptor + Npgsql connection retry on transient failure
        // AddDbContextPool reuses DbContext instances across requests (reduces allocation overhead)
        services.AddDbContextPool<ApplicationContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.EnableRetryOnFailure();
            });
            var interceptor = sp.GetRequiredService<AuditEntityInterceptor>();
            options.AddInterceptors(interceptor);
        });

        // AWS S3
        var awsOptions = configuration.GetAWSOptions("AWS");
        if (awsOptions != null)
        {
            services.AddDefaultAWSOptions(awsOptions);
            services.AddAWSService<IAmazonS3>();
        }

        AddOptions(services, configuration);
        AddLlmClients(services, configuration);
        AddRepositories(services);
        AddApplicationServices(services);
        AddHttpClients(services, configuration);

        // Background services
        services.AddHostedService<OcrResultConsumer>();
        services.AddHostedService<SystemInitializationService>();

        services.AddSingleton<IProcessBroadcaster, StreamBroadcaster>();
        services.AddSingleton<ICacheService, RedisCacheService>();
        services.AddSingleton<IConcurrencyService, RedisConcurrencyService>();
        services.AddSingleton<IAppCacheService, RedisAppCacheService>();

        // LLM Service (concrete, no interface abstraction)
        services.AddSingleton<LlmService>();

        // AWS
        services.AddHttpContextAccessor();
        services.AddScoped<IS3Service, S3Service>();
        services.AddScoped<GenQAsService>();
        services.AddScoped<IMarkdownService, MarkdownService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<IAccessControlService, AccessControlService>();
        services.AddScoped<AuthService>();

        // Admin Services
        services.AddScoped<AdminOrgService>();
        services.AddScoped<AdminStatsService>();
        services.AddScoped<AdminDatasetService>();

        // User Dataset Services
        services.AddScoped<DatasetService>();

        // User Services
        services.AddScoped<UserService>();
        services.AddScoped<UserInformationService>();

        return services;
    }
}
