using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Application.Service;
using MarkdownGenQAs.Models.Constants;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using MarkdownGenQAs.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Infrastructure.Services;

/// <summary>
/// Thực hiện cleanup hệ thống và khởi tạo dữ liệu mặc định (Roles, Root OU, Admin) ngay khi Ứng dụng Start.
/// </summary>
public class SystemInitializationService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemInitializationService> _logger;
    private readonly InitialSettings _initialSettings;

    public SystemInitializationService(
        IServiceProvider serviceProvider,
        ILogger<SystemInitializationService> logger,
        IOptions<InitialSettings> initialSettings)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _initialSettings = initialSettings.Value;
    }

    private Distance ParseDistance(string? distance) => distance switch
    {
        "Euclid" => Distance.Euclid,
        "Dot" => Distance.Dot,
        _ => Distance.Cosine
    };

    private ShardingMethod? ParseShardingMethod(string? method) => method switch
    {
        "Custom" => ShardingMethod.Custom,
        "Auto" => ShardingMethod.Auto,
        _ => null
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SystemInitialization] Starting system initialization on app startup...");

        using (var scope = _serviceProvider.CreateScope())
        {
            try
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationContext>();

                // 0. Apply pending migrations (auto-creates database if not exists)
                await context.Database.MigrateAsync(cancellationToken);
                _logger.LogInformation("[SystemInitialization] Database migrations applied.");

                // 1. Cleanup Redis
                var concurrencyService = scope.ServiceProvider.GetRequiredService<IConcurrencyService>();
                await concurrencyService.ClearAllModelsAsync();
                await concurrencyService.ClearAllStreamsAsync();
                _logger.LogInformation("[SystemInitialization] Cleanup successful.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Fatal error during initialization.");
                throw;
            }

            // 2. Initialize Qdrant collections — FATAL: app không thể hoạt động nếu Qdrant down
            await InitializeQdrantCollectionsAsync(scope);

            // 3. Indexing Job Recovery — non-fatal: chỉ log lỗi, app vẫn chạy
            try
            {
                var documentService = scope.ServiceProvider.GetRequiredService<DocumentService>();
                var recoveryResult = await documentService.RecoverStuckIndexingJobsAsync();

                if (recoveryResult.IsSuccess)
                {
                    _logger.LogInformation("Indexing recovery completed. Processed {Count} documents.", recoveryResult.Data);
                }
                else
                {
                    _logger.LogWarning("Indexing recovery issue: {Message}", recoveryResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Indexing job recovery.");
            }

            // 4. Seed Data — non-fatal
            try
            {
                await SeedRolesAsync(scope);
                var rootOu = await SeedRootOrganizationUnitAsync(scope);
                await SeedAdminUserAsync(scope, rootOu);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SystemInitialization] Error during seed data.");
            }

            _logger.LogInformation("[SystemInitialization] System initialization completed successfully.");
        }
    }

    private async Task SeedRolesAsync(IServiceScope scope)
    {
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        
        string[] roles = { RoleNames.Admin, RoleNames.User };
        foreach (var roleName in roles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                _logger.LogInformation("Seeding role: {RoleName}", roleName);
                await roleManager.CreateAsync(new ApplicationRole { Name = roleName });
            }
        }
    }

    private async Task<OrganizationUnit?> SeedRootOrganizationUnitAsync(IServiceScope scope)
    {
        var context = scope.ServiceProvider.GetRequiredService<ApplicationContext>();
        
        var rootOu = await context.OrganizationUnits.FirstOrDefaultAsync(o => o.ParentId == null && !o.IsDeleted);
        if (rootOu == null)
        {
            _logger.LogInformation("Seeding root organization unit...");
            rootOu = new OrganizationUnit
            {
                Id = Guid.NewGuid(),
                Name = "Hệ thống Root",
                Code = "ROOT",
                Path = string.Empty,
                Level = 0
            };
            rootOu.Path = rootOu.Id.ToString();
            
            context.OrganizationUnits.Add(rootOu);
            await context.SaveChangesAsync();
        }
        return rootOu;
    }

    private async Task SeedAdminUserAsync(IServiceScope scope, OrganizationUnit? rootOu)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationContext>();
        
        if (!await userManager.Users.AnyAsync())
        {
            var adminConfig = _initialSettings.AdminUser;
            _logger.LogInformation("Seeding admin user: {Email}", adminConfig.Email);
            
            var adminUser = new ApplicationUser
            {
                UserName = adminConfig.UserName,
                Email = adminConfig.Email,
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(adminUser, adminConfig.Password);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(adminUser, RoleNames.Admin);
                
                if (rootOu != null)
                {
                    _logger.LogInformation("Assigning admin user to root OU...");
                    context.UserPositions.Add(new UserPosition
                    {
                        UserId = adminUser.Id,
                        OUId = rootOu.Id,
                        Role = OrganizationRole.Manager,
                        IsPrimary = true
                    });
                    await context.SaveChangesAsync();
                }
            }
            else
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger.LogError("Failed to seed admin user: {Errors}", errors);
            }
        }
    }

    private async Task InitializeQdrantCollectionsAsync(IServiceScope scope)
    {
        var qdrantService = scope.ServiceProvider.GetRequiredService<IQdrantService>();
        var qdrantOptions = scope.ServiceProvider.GetRequiredService<IOptions<QdrantOptions>>().Value;

        var collectionName = "documents";

        if (!await qdrantService.CollectionExistsAsync(collectionName))
        {
            var distance = ParseDistance(qdrantOptions.Embedding.Distance);
            var shardingMethod = ParseShardingMethod(qdrantOptions.DefaultCollection.ShardingMethod);

            await qdrantService.CreateCollectionAsync(
                collectionName,
                new VectorParams
                {
                    Size = (ulong)qdrantOptions.Embedding.Dimension,
                    Distance = distance
                },
                shardNumber: qdrantOptions.DefaultCollection.ShardNumber,
                replicationFactor: qdrantOptions.DefaultCollection.ReplicationFactor,
                writeConsistencyFactor: qdrantOptions.DefaultCollection.WriteConsistencyFactor,
                onDiskPayload: qdrantOptions.DefaultCollection.OnDiskPayload,
                shardingMethod: shardingMethod);

            _logger.LogInformation(
                "[Qdrant] Collection '{CollectionName}' created: dim={Dimension}, distance={Distance}, shards={Shards}, sharding={ShardingMethod}",
                collectionName,
                qdrantOptions.Embedding.Dimension,
                distance,
                qdrantOptions.DefaultCollection.ShardNumber,
                qdrantOptions.DefaultCollection.ShardingMethod ?? "default");
        }
        else
        {
            _logger.LogInformation("[Qdrant] Collection '{CollectionName}' already exists.", collectionName);
        }

        // Create indexes for common filter fields
        try
        {
            await qdrantService.CreatePayloadIndexAsync(collectionName, "documentId", PayloadSchemaType.Keyword);
            _logger.LogInformation("[Qdrant] Payload index for 'documentId' created.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Qdrant] Failed to create payload index for 'documentId' (may already exist)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
