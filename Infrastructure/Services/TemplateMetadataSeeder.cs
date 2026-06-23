using System.Text.Json;
using System.Text.Json.Nodes;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Utils;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Services;

/// <summary>
/// Auto-seeds TemplateMetadata records from <c>data/templates/*.template.json</c> on app startup.
/// Idempotent: skips templates whose <c>Name</c> already exists (including soft-deleted).
/// Also creates the corresponding Qdrant payload indexes for each <c>indexKeys</c> entry.
/// </summary>
public class TemplateMetadataSeeder
{
    private readonly ApplicationContext _context;
    private readonly IQdrantService _qdrantService;
    private readonly ILogger<TemplateMetadataSeeder> _logger;

    private const string TemplatesSubFolder = "templates";
    private const string CollectionName = "documents";
    private const string TemplateFilePattern = "*.template.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public TemplateMetadataSeeder(
        ApplicationContext context,
        IQdrantService qdrantService,
        ILogger<TemplateMetadataSeeder> logger)
    {
        _context = context;
        _qdrantService = qdrantService;
        _logger = logger;
    }

    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var templatesDir = Path.Combine(FileUtils.DataDir, TemplatesSubFolder);
        if (!Directory.Exists(templatesDir))
        {
            _logger.LogWarning("[TemplateSeeder] Templates directory not found: {Dir}", templatesDir);
            return new SeedResult(0, 0, 0);
        }

        var files = Directory.GetFiles(templatesDir, TemplateFilePattern);
        if (files.Length == 0)
        {
            _logger.LogInformation("[TemplateSeeder] No template definition files found in {Dir}", templatesDir);
            return new SeedResult(0, 0, 0);
        }

        int created = 0, skipped = 0, failed = 0;

        foreach (var file in files)
        {
            try
            {
                var outcome = await ProcessFileAsync(file, cancellationToken);
                switch (outcome)
                {
                    case ProcessOutcome.Created: created++; break;
                    case ProcessOutcome.Skipped: skipped++; break;
                    case ProcessOutcome.Failed: failed++; break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TemplateSeeder] Failed to process file: {File}", file);
                failed++;
            }
        }

        _logger.LogInformation(
            "[TemplateSeeder] Done. Created: {Created}, Skipped: {Skipped}, Failed: {Failed}",
            created, skipped, failed);
        return new SeedResult(created, skipped, failed);
    }

    private async Task<ProcessOutcome> ProcessFileAsync(string filePath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);

        TemplateDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize<TemplateDefinition>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[TemplateSeeder] Invalid JSON in template file: {File}", filePath);
            return ProcessOutcome.Failed;
        }

        if (def == null
            || string.IsNullOrWhiteSpace(def.Name)
            || def.JsonSchema == null)
        {
            _logger.LogWarning("[TemplateSeeder] Skipped (missing name or jsonSchema): {File}", filePath);
            return ProcessOutcome.Failed;
        }

        var jsonSchemaString = def.JsonSchema.ToJsonString();

        var exists = await _context.TemplateMetadatas
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Name == def.Name, ct);

        if (exists)
        {
            _logger.LogInformation(
                "[TemplateSeeder] Skipped (already exists): '{Name}' from {File}",
                def.Name, Path.GetFileName(filePath));
            return ProcessOutcome.Skipped;
        }

        var entity = new TemplateMetadata
        {
            Name = def.Name.Trim(),
            Description = def.Description?.Trim(),
            JsonSchema = jsonSchemaString,
            IndexKeys = def.IndexKeys
        };
        _context.TemplateMetadatas.Add(entity);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[TemplateSeeder] Created template '{Name}' ({Id}) from {File}",
            entity.Name, entity.Id, Path.GetFileName(filePath));

        if (def.IndexKeys != null)
        {
            foreach (var key in def.IndexKeys)
            {
                try
                {
                    var schemaType = MetadataSchemaHelper.GetPayloadSchemaType(jsonSchemaString, key);
                    await _qdrantService.CreatePayloadIndexAsync(CollectionName, key, schemaType);
                    _logger.LogInformation(
                        "[TemplateSeeder] [Qdrant] Created payload index '{Field}' (type={Type})",
                        key, schemaType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[TemplateSeeder] [Qdrant] Failed to create payload index '{Field}' (may already exist)",
                        key);
                }
            }
        }

        return ProcessOutcome.Created;
    }

    private enum ProcessOutcome { Created, Skipped, Failed }

    public record SeedResult(int Created, int Skipped, int Failed);

    private class TemplateDefinition
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? IndexKeys { get; set; }
        public JsonNode? JsonSchema { get; set; }
    }
}
