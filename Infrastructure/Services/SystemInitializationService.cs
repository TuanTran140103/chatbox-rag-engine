using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Application.Service;
using MarkdownGenQAs.Models.Constants;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🚀 [SystemInitialization] Starting system initialization on app startup...");

        using (var scope = _serviceProvider.CreateScope())
        {
            try
            {
                // 1. Cleanup Redis
                var concurrencyService = scope.ServiceProvider.GetRequiredService<IConcurrencyService>();
                await concurrencyService.ClearAllModelsAsync();
                await concurrencyService.ClearAllStreamsAsync();
                _logger.LogInformation("✅ [SystemInitialization] Cleanup successful.");

                // 2. GenQA Job Recovery - Recover jobs stuck in ProcessingGenQa after app crash
                try
                {
                    var documentService = scope.ServiceProvider.GetRequiredService<DocumentService>();
                    var recoveryResult = await documentService.RecoverStuckGenQAJobsAsync();

                    if (recoveryResult.IsSuccess)
                    {
                        _logger.LogInformation("GenQA recovery completed. Processed {Count} documents.", recoveryResult.Data);
                    }
                    else
                    {
                        _logger.LogWarning("GenQA recovery issue: {Message}", recoveryResult.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during GenQA job recovery.");
                }

                // 3. Seed Data ---> in dev then run once and comment
                await SeedRolesAsync(scope);
                var rootOu = await SeedRootOrganizationUnitAsync(scope);
                await SeedSystemStatisticsAsync(scope);
                await SeedAdminUserAsync(scope, rootOu);

                _logger.LogInformation("✅ [SystemInitialization] System initialization completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [SystemInitialization] Error during system initialization.");
            }
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

    private async Task SeedSystemStatisticsAsync(IServiceScope scope)
    {
        var context = scope.ServiceProvider.GetRequiredService<ApplicationContext>();
        
        var globalStats = await context.SystemStatistics.FirstOrDefaultAsync(s => s.OUId == null);
        if (globalStats == null)
        {
            _logger.LogInformation("Seeding global system statistics...");
            context.SystemStatistics.Add(new SystemStatistics
            {
                OUId = null,
                TotalDatasets = 0,
                TotalDocuments = 0,
                TotalStorageUsage = 0
            });
            await context.SaveChangesAsync();
        }
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
