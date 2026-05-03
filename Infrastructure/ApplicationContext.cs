using System.Linq.Expressions;
using System.Text.Json;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarkdownGenQAs.Infrastructure;

public class ApplicationContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public ApplicationContext(DbContextOptions<ApplicationContext> options) : base(options)
    {
    }

    public DbSet<Document> Documents { get; set; }
    public DbSet<Dataset> Datasets { get; set; }
    public DbSet<DatasetItem> DatasetItems { get; set; }
    public DbSet<AccessShare> AccessShares { get; set; }
    public DbSet<LogMessage> LogMessages { get; set; }
    public DbSet<DocumentJob> DocumentJobs { get; set; }
    public DbSet<OrganizationUnit> OrganizationUnits { get; set; }
    public DbSet<UserPosition> UserPositions { get; set; }
    public DbSet<SystemStatistics> SystemStatistics { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("Users");
            entity.HasIndex(e => e.NormalizedUserName).HasDatabaseName("UserNameIndex").IsUnique();
            entity.HasIndex(e => e.NormalizedEmail).HasDatabaseName("EmailIndex");
            entity.HasIndex(e => e.NormalizedEmail)
                .HasDatabaseName("IX_Users_NormalizedEmail_Trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<ApplicationRole>(entity =>
        {
            entity.ToTable("Roles");
            entity.HasIndex(e => e.NormalizedName).HasDatabaseName("RoleNameIndex").IsUnique();
        });

        modelBuilder.Entity<IdentityUserRole<Guid>>(entity =>
        {
            entity.ToTable("UserRoles");
            entity.HasKey(e => new { e.UserId, e.RoleId });
        });

        modelBuilder.Entity<IdentityUserClaim<Guid>>(entity =>
        {
            entity.ToTable("UserClaims");
        });

        modelBuilder.Entity<IdentityUserLogin<Guid>>(entity =>
        {
            entity.ToTable("UserLogins");
            entity.HasKey(e => new { e.ProviderKey, e.LoginProvider });
        });

        modelBuilder.Entity<IdentityUserToken<Guid>>(entity =>
        {
            entity.ToTable("UserTokens");
            entity.HasKey(e => new { e.UserId, e.LoginProvider, e.Name });
        });

        modelBuilder.Entity<IdentityRoleClaim<Guid>>(entity =>
        {
            entity.ToTable("RoleClaims");
        });

        modelBuilder.Entity<OrganizationUnit>(entity =>
        {
            entity.ToTable("OrganizationUnits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Code).HasMaxLength(50);
            entity.Property(e => e.Path).IsRequired().HasMaxLength(1000);

            entity.HasOne(e => e.Parent)
                  .WithMany(e => e.Children)
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserPosition>(entity =>
        {
            entity.ToTable("UserPositions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasConversion<string>().IsRequired();

            entity.HasIndex(e => new { e.UserId, e.OUId }).IsUnique();

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.OrganizationUnit)
                  .WithMany(e => e.UserPositions)
                  .HasForeignKey(e => e.OUId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Manager)
                  .WithMany()
                  .HasForeignKey(e => e.ManagerId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Dataset>(entity =>
        {
            entity.ToTable("Datasets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(1000);

            entity.HasIndex(e => e.Name)
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");

            entity.HasOne(e => e.Owner)
                  .WithMany()
                  .HasForeignKey(e => e.OwnerUserId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.OrganizationUnit)
                  .WithMany(e => e.Datasets)
                  .HasForeignKey(e => e.OUId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DatasetItem>(entity =>
        {
            entity.ToTable("DatasetItems");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Path).IsRequired();
            entity.Property(e => e.ItemType).HasConversion<string>().IsRequired();

            entity.HasIndex(e => e.Path);

            entity.HasIndex(e => new { e.DatasetId, e.Level });

            entity.HasOne(i => i.Dataset)
                  .WithMany(d => d.Items)
                  .HasForeignKey(i => i.DatasetId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.Parent)
                  .WithMany()
                  .HasForeignKey(i => i.ParentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.Document)
                  .WithOne(d => d.DatasetItem)
                  .HasForeignKey<DatasetItem>(i => i.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessShare>(entity =>
        {
            entity.ToTable("AccessShares");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PermissionMask).HasConversion<string>().IsRequired();

            entity.HasIndex(e => new { e.DatasetId, e.DatasetItemId, e.ShareToUserId, e.ShareToOUId })
                  .IsUnique()
                  .HasFilter("\"DatasetItemId\" IS NOT NULL");

            entity.HasOne(p => p.Dataset)
                  .WithMany(d => d.AccessShares)
                  .HasForeignKey(p => p.DatasetId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.DatasetItem)
                  .WithMany()
                  .HasForeignKey(p => p.DatasetItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.ShareToUser)
                  .WithMany()
                  .HasForeignKey(p => p.ShareToUserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.ShareToOU)
                  .WithMany()
                  .HasForeignKey(p => p.ShareToOUId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Grantor)
                  .WithMany()
                  .HasForeignKey(p => p.GrantedBy)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("Documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ObjectKeyFilePdf).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();

            entity.Property(e => e.OcrContent).HasColumnType("text");
            entity.Property(e => e.QaContent).HasColumnType("text");
            entity.Property(e => e.SummaryContent).HasColumnType("text");

            entity.HasIndex(e => e.FileName)
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LogMessage>(entity =>
        {
            entity.ToTable("LogMessages");
            entity.HasKey(e => e.Id);

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            entity.Property(e => e.LogsOcr)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, jsonOptions),
                    v => string.IsNullOrEmpty(v) ? new List<LogEvent>() : JsonSerializer.Deserialize<List<LogEvent>>(v, jsonOptions) ?? new List<LogEvent>(),
                    new ValueComparer<List<LogEvent>>(
                        (c1, c2) => JsonSerializer.Serialize(c1, jsonOptions) == JsonSerializer.Serialize(c2, jsonOptions),
                        c => c == null ? 0 : JsonSerializer.Serialize(c, jsonOptions).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<LogEvent>>(JsonSerializer.Serialize(c, jsonOptions), jsonOptions) ?? new List<LogEvent>()
                    )
                )
                .HasColumnType("jsonb");

            entity.Property(e => e.LogsGenQa)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, jsonOptions),
                    v => string.IsNullOrEmpty(v) ? new List<LogEvent>() : JsonSerializer.Deserialize<List<LogEvent>>(v, jsonOptions) ?? new List<LogEvent>(),
                    new ValueComparer<List<LogEvent>>(
                        (c1, c2) => JsonSerializer.Serialize(c1, jsonOptions) == JsonSerializer.Serialize(c2, jsonOptions),
                        c => c == null ? 0 : JsonSerializer.Serialize(c, jsonOptions).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<LogEvent>>(JsonSerializer.Serialize(c, jsonOptions), jsonOptions) ?? new List<LogEvent>()
                    )
                )
                .HasColumnType("jsonb");

            entity.HasOne(l => l.Document)
                  .WithOne(o => o.LogMessage)
                  .HasForeignKey<LogMessage>(l => l.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentJob>(entity =>
        {
            entity.ToTable("DocumentJobs", t =>
            {
                t.HasCheckConstraint(
                    "CK_DocumentJob_OcrBeforeGenQa",
                    @"""StatusGenQa"" NOT IN ('Processing', 'Succeeded')
                     OR ""StatusOcr"" = 'Succeeded'");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.OcrJobId).HasMaxLength(255);
            entity.Property(e => e.GenQaJobId).HasMaxLength(255);
            entity.Property(e => e.StatusOcr).HasConversion<string>().IsRequired();
            entity.Property(e => e.StatusGenQa).HasConversion<string>().IsRequired();

            entity.HasOne(j => j.Document)
                  .WithOne(o => o.DocumentJob)
                  .HasForeignKey<DocumentJob>(j => j.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SystemStatistics>(entity =>
        {
            entity.ToTable("SystemStatistics");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalDatasets).HasDefaultValue(0);
            entity.Property(e => e.TotalDocuments).HasDefaultValue(0);
            entity.Property(e => e.TotalStorageUsage).HasDefaultValue(0L);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.OU)
                  .WithMany()
                  .HasForeignKey(e => e.OUId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.OUId);
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            // Cấu hình IAuditTime
            if (typeof(IAuditTime).IsAssignableFrom(clrType))
            {
                var entity = modelBuilder.Entity(clrType);
                entity.Property("CreatedAt").IsRequired();
                entity.Property("UpdatedAt").IsRequired();
            }

            // Cấu hình IAuditUser
            if (typeof(IAuditUser).IsAssignableFrom(clrType))
            {
                var entity = modelBuilder.Entity(clrType);
                entity.Property("CreatedBy").IsRequired(false);
                entity.Property("ModifiedBy").IsRequired(false);
            }

            // Cấu hình IAuditDelete - Áp dụng Soft Delete Query Filter
            if (typeof(IAuditDelete).IsAssignableFrom(clrType))
            {
                var entity = modelBuilder.Entity(clrType);
                entity.Property("DeletedBy").IsRequired(false);
                entity.Property("IsDeleted").HasDefaultValue(false);

                // Sử dụng Reflection để gọi hàm Generic giúp code dễ đọc hơn
                var method = typeof(ApplicationContext)
                    .GetMethod(nameof(SetSoftDeleteFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.MakeGenericMethod(clrType);

                method?.Invoke(null, new object[] { modelBuilder });
            }
        }
    }

    private static void SetSoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IAuditDelete
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => !e.IsDeleted);
    }

    // Note: UpdateTimestamps() and SaveChanges overrides removed - handled by AuditEntityInterceptor
}
