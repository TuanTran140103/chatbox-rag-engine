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

                    // Cascade restore: DatasetItem restored → restore Document too
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

                                var ouId = GetOUIdFromDocument(context, doc.Id);
                                UpdateDocumentStatistics(context, ouId, 1);
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

                        if (entry.Entity is Document doc)
                        {
                            var ouId = GetOUIdFromDocument(context, doc.Id);
                            UpdateDocumentStatistics(context, ouId, -1);
                        }

                        // Cascade soft-delete: DatasetItem deleted → soft-delete its Document too
                        if (entry.Entity is DatasetItem di && di.DocumentId.HasValue)
                        {
                            var docEntry = context.ChangeTracker.Entries<Document>()
                                .FirstOrDefault(e => e.Entity.Id == di.DocumentId.Value);

                            // Skip if Document is already being soft-deleted (handled above)
                            if (docEntry?.State != EntityState.Deleted)
                            {
                                var relatedDoc = docEntry?.Entity ?? context.Find<Document>(di.DocumentId.Value);
                                if (relatedDoc != null && !relatedDoc.IsDeleted)
                                {
                                    relatedDoc.IsDeleted = true;
                                    relatedDoc.DeletedAt = now;
                                    relatedDoc.DeletedBy = userId;

                                    var ouId = GetOUIdFromDocument(context, relatedDoc.Id);
                                    UpdateDocumentStatistics(context, ouId, -1);
                                }
                            }
                        }
                    }
                    break;
            }
        }
    }

    public static void UpdateDocumentStatistics(DbContext context, Guid? ouId, int delta)
    {
        if (context == null || !ouId.HasValue) return;

        context.Database.ExecuteSqlRaw(
            @"UPDATE ""SystemStatistics""
              SET ""TotalDocuments"" = ""TotalDocuments"" + CAST(@p0 AS integer),
                  ""UpdatedAt"" = CURRENT_TIMESTAMP
              WHERE ""OUId"" = CAST(@p1 AS uuid)",
            delta, ouId.Value);
    }

    private static Guid? GetOUIdFromDocument(DbContext context, Guid documentId)
    {
        try
        {
            var ouId = context.Database
                .SqlQueryRaw<Guid?>(
                    @"SELECT o.""Id""
                      FROM ""OrganizationUnits"" o
                      JOIN ""Datasets"" d ON d.""OUId"" = o.""Id""
                      JOIN ""DatasetItems"" di ON di.""DatasetId"" = d.""Id""
                      WHERE di.""DocumentId"" = {0}
                      LIMIT 1",
                    documentId)
                .FirstOrDefault();

            return ouId;
        }
        catch
        {
            return null;
        }
    }
}