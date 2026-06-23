using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models.Entities;
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
                        entry.Property("CreatedAt").IsModified = false;
                    }
                    if (entry.Entity is IAuditUser auditUserMod)
                    {
                        auditUserMod.ModifiedBy = userId;
                        entry.Property("CreatedBy").IsModified = false;
                    }
                    if (entry.Entity is IAuditDelete auditDeleteMod && auditDeleteMod.IsDeleted)
                    {
                        auditDeleteMod.IsDeleted = false;
                        auditDeleteMod.DeletedAt = null;
                        auditDeleteMod.DeletedBy = null;
                    }

                    if (entry.Entity is DatasetItem diRestore && diRestore.DocumentId.HasValue)
                    {
                        var isDeletedProp = entry.Property(nameof(IAuditDelete.IsDeleted));
                        if (isDeletedProp.IsModified && isDeletedProp.OriginalValue is true && isDeletedProp.CurrentValue is false)
                        {
                            var doc = context.ChangeTracker.Entries<Document>()
                                .FirstOrDefault(e => e.Entity.Id == diRestore.DocumentId.Value)
                                ?.Entity ?? context.Find<Document>(diRestore.DocumentId.Value);

                            if (doc != null && doc.IsDeleted)
                            {
                                doc.IsDeleted = false;
                                doc.DeletedAt = null;
                                doc.DeletedBy = null;
                            }
                        }
                    }
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is IAuditDelete auditDelete)
                    {
                        auditDelete.DeletedAt = now;
                        auditDelete.DeletedBy = userId;
                        auditDelete.IsDeleted = true;

                        entry.State = EntityState.Modified;

                        if (entry.Entity is DatasetItem di && di.DocumentId.HasValue)
                        {
                            var docEntry = context.ChangeTracker.Entries<Document>()
                                .FirstOrDefault(e => e.Entity.Id == di.DocumentId.Value);

                            if (docEntry?.State != EntityState.Deleted)
                            {
                                var relatedDoc = docEntry?.Entity ?? context.Find<Document>(di.DocumentId.Value);
                                if (relatedDoc != null && !relatedDoc.IsDeleted)
                                {
                                    relatedDoc.IsDeleted = true;
                                    relatedDoc.DeletedAt = now;
                                    relatedDoc.DeletedBy = userId;
                                }
                            }
                        }
                    }
                    break;
            }
        }
    }
}