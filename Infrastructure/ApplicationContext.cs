using System.Linq.Expressions;
using System.Text.Json;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Models.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarkdownGenQAs.Infrastructure;

public class ApplicationContext : DbContext
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
    public DbSet<SystemStatistics> SystemStatistics { get; set; }
    public DbSet<TemplateMetadata> TemplateMetadatas { get; set; }
    public DbSet<ConversationThread> Threads { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Dataset>(entity =>
        {
            entity.ToTable("Datasets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(1000);

            entity.HasIndex(e => e.Name)
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");

            entity.HasOne(e => e.TemplateMetadata)
                  .WithMany(e => e.Datasets)
                  .HasForeignKey(e => e.TemplateMetadataId)
                  .OnDelete(DeleteBehavior.SetNull);
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

            entity.HasIndex(e => new { e.DatasetId, e.DatasetItemId, e.ShareToUserId, e.ShareToDepartmentId })
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
            entity.Property(e => e.ChunkContent).HasColumnType("text");
            entity.Property(e => e.SummaryContent).HasColumnType("text");
            entity.Property(e => e.QaSummaryContent).HasColumnType("text");
            entity.Property(e => e.MetadataContent).HasColumnType("text");
            entity.Property(e => e.MetadataError).HasColumnType("text");
            entity.Property(e => e.IsMetadataExtracted);

            entity.HasIndex(e => e.FileName)
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");
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

            entity.Property(e => e.LogsIndexing)
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
                    "CK_DocumentJob_OcrBeforeIndexing",
                    @"""StatusIndexing"" IS NULL
                      OR ""StatusOcr"" = 'Succeeded'
                      OR ""StatusIndexing"" NOT IN ('Processing', 'Succeeded')");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.OcrJobId).HasMaxLength(255);
            entity.Property(e => e.GenQaJobId).HasMaxLength(255);
            entity.Property(e => e.IndexingJobId).HasMaxLength(255);
            entity.Property(e => e.StatusOcr).HasConversion<string>();
            entity.Property(e => e.StatusGenQa).HasConversion<string>();
            entity.Property(e => e.StatusIndexing).HasConversion<string>();

            entity.HasOne(j => j.Document)
                  .WithOne(o => o.DocumentJob)
                  .HasForeignKey<DocumentJob>(j => j.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SystemStatistics>(entity =>
        {
            entity.ToTable("SystemStatistics");
            entity.Property(e => e.TotalDatasets).HasDefaultValue(0);
            entity.Property(e => e.TotalDocuments).HasDefaultValue(0);
            entity.Property(e => e.TotalStorageUsage).HasDefaultValue(0L);

            entity.HasIndex(e => e.DepartmentId);
        });

        modelBuilder.Entity<TemplateMetadata>(entity =>
        {
            entity.ToTable("TemplateMetadatas");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.JsonSchema).IsRequired().HasColumnType("text");
            entity.Property(e => e.IndexKeys)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null),
                    new ValueComparer<List<string>>(
                        (c1, c2) => c1 == null && c2 == null || c1 != null && c2 != null && c1.SequenceEqual(c2),
                        c => c == null ? 0 : c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        c => c == null ? new List<string>() : c.ToList()));
        });

        modelBuilder.Entity<ConversationThread>(entity =>
        {
            entity.ToTable("Threads");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ThreadId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);

            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ThreadId).IsUnique();
            entity.HasIndex(e => e.Title)
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(IAuditTime).IsAssignableFrom(clrType))
            {
                var entity = modelBuilder.Entity(clrType);
                entity.Property("CreatedAt").IsRequired();
                entity.Property("UpdatedAt").IsRequired();
            }

            if (typeof(IAuditUser).IsAssignableFrom(clrType))
            {
                var entity = modelBuilder.Entity(clrType);
                entity.Property("CreatedBy").IsRequired(false);
                entity.Property("ModifiedBy").IsRequired(false);
            }

            if (typeof(IAuditDelete).IsAssignableFrom(clrType))
            {
                var entity = modelBuilder.Entity(clrType);
                entity.Property("DeletedBy").IsRequired(false);
                entity.Property("IsDeleted").HasDefaultValue(false);

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
}