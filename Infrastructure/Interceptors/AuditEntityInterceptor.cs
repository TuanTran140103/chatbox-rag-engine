using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace MarkdownGenQAs.Infrastructure.Interceptors;

public class AuditEntityInterceptor : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AuditEntityInterceptor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        SetAuditFields(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        SetAuditFields(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void SetAuditFields(DbContext? context)
    {
        if (context == null) return;

        using var scope = _scopeFactory.CreateScope();
        var auditUserAccessor = scope.ServiceProvider.GetRequiredService<IAuditUserAccessor>();

        var userId = auditUserAccessor.GetCurrentUserId();
        var now = DateTime.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is IAuditTime auditTimeAdd)
                    {
                        auditTimeAdd.CreatedAt = now;
                        auditTimeAdd.UpdatedAt = now;
                    }
                    if (entry.Entity is IAuditUser auditUserAdd)
                    {
                        auditUserAdd.CreatedBy = userId;
                        auditUserAdd.ModifiedBy = userId;
                    }
                    break;

                case EntityState.Modified:
                    if (entry.Entity is IAuditTime auditTimeMod)
                    {
                        auditTimeMod.UpdatedAt = now;
                        // Ngăn chặn việc ghi đè CreatedAt khi update
                        entry.Property("CreatedAt").IsModified = false;
                    }
                    if (entry.Entity is IAuditUser auditUserMod)
                    {
                        auditUserMod.ModifiedBy = userId;
                        // Ngăn chặn việc ghi đè CreatedBy khi update
                        entry.Property("CreatedBy").IsModified = false;
                    }
                    // Khôi phục nếu entity đang bị xóa ảo nhưng lại được update (nếu cần)
                    if (entry.Entity is IAuditDelete auditDeleteMod && auditDeleteMod.IsDeleted)
                    {
                        auditDeleteMod.IsDeleted = false;
                        auditDeleteMod.DeletedAt = null;
                        auditDeleteMod.DeletedBy = null;
                    }
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is IAuditDelete auditDelete)
                    {
                        var isAdmin = auditUserAccessor.IsAdmin();

                        if (!isAdmin)
                        {
                            // Nếu KHÔNG PHẢI Admin -> Chuyển sang xóa ảo (SOFT DELETE)
                            auditDelete.DeletedAt = now;
                            auditDelete.DeletedBy = userId;
                            auditDelete.IsDeleted = true;

                            // Chuyển trạng thái từ DELETE sang MODIFIED để EF thực hiện lệnh Update
                            entry.State = EntityState.Modified;
                        }
                        // Nếu LÀ Admin -> Để nguyên trạng thái Deleted để EF thực hiện Hard Delete
                    }
                    break;
            }
        }
    }
}