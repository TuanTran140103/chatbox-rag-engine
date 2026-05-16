using MarkdownGenQAs.Application.Dto.TemplateMetadata;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Utils;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class TemplateMetadataService : ITemplateMetadataService
{
    private readonly ApplicationContext _context;
    private readonly IAccessControlService _accessControl;
    private readonly IQdrantService _qdrantService;
    private readonly ILogger<TemplateMetadataService> _logger;

    public TemplateMetadataService(
        ApplicationContext context,
        IAccessControlService accessControl,
        IQdrantService qdrantService,
        ILogger<TemplateMetadataService> logger)
    {
        _context = context;
        _accessControl = accessControl;
        _qdrantService = qdrantService;
        _logger = logger;
    }

    public async Task<List<TemplateMetadataListDto>> GetAllAsync()
    {
        return await _context.TemplateMetadatas
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TemplateMetadataListDto(
                t.Id,
                t.Name,
                t.Description,
                t.CreatedAt
            ))
            .ToListAsync();
    }

    public async Task<ServiceResult<TemplateMetadataDetailDto>> GetByIdAsync(Guid id)
    {
        var template = await _context.TemplateMetadatas
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (template == null)
            return new ServiceResult<TemplateMetadataDetailDto>
            {
                IsSuccess = false,
                ErrorMessage = "Template metadata not found"
            };

        return new ServiceResult<TemplateMetadataDetailDto>
        {
            IsSuccess = true,
            Data = new TemplateMetadataDetailDto(
                template.Id,
                template.Name,
                template.Description,
                template.JsonSchema,
                template.IndexKeys,
                template.CreatedAt,
                template.UpdatedAt,
                template.CreatedBy
            )
        };
    }

    public async Task<ServiceResult<TemplateMetadataDetailDto>> CreateAsync(Guid userId, CreateTemplateMetadataRequestDto dto)
    {
        if (!await _accessControl.IsAdminAsync(userId))
            return new ServiceResult<TemplateMetadataDetailDto>
            {
                IsSuccess = false,
                ErrorMessage = "Only admins can create template metadata"
            };

        var template = new TemplateMetadata
        {
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim(),
            JsonSchema = dto.JsonSchema,
            IndexKeys = dto.IndexKeys
        };

        _context.TemplateMetadatas.Add(template);
        await _context.SaveChangesAsync();

        // Create Qdrant payload indexes for the specified keys
        if (dto.IndexKeys?.Count > 0 && !string.IsNullOrEmpty(dto.JsonSchema))
        {
            foreach (var key in dto.IndexKeys)
            {
                try
                {
                    var schemaType = MetadataSchemaHelper.GetPayloadSchemaType(dto.JsonSchema, key);
                    await _qdrantService.CreatePayloadIndexAsync("documents", key, schemaType);
                    _logger.LogInformation("[Qdrant] Created payload index '{Field}' on collection 'documents'", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Qdrant] Failed to create payload index '{Field}' on collection 'documents'", key);
                }
            }
        }

        _logger.LogInformation("TemplateMetadata {TemplateId} created by admin {UserId}", template.Id, userId);

        return new ServiceResult<TemplateMetadataDetailDto>
        {
            IsSuccess = true,
            Data = new TemplateMetadataDetailDto(
                template.Id,
                template.Name,
                template.Description,
                template.JsonSchema,
                template.IndexKeys,
                template.CreatedAt,
                template.UpdatedAt,
                template.CreatedBy
            )
        };
    }

    public async Task<ServiceResult<TemplateMetadataDetailDto>> UpdateAsync(Guid userId, Guid id, UpdateTemplateMetadataRequestDto dto)
    {
        if (!await _accessControl.IsAdminAsync(userId))
            return new ServiceResult<TemplateMetadataDetailDto>
            {
                IsSuccess = false,
                ErrorMessage = "Only admins can update template metadata"
            };

        var template = await _context.TemplateMetadatas.FindAsync(id);
        if (template == null)
            return new ServiceResult<TemplateMetadataDetailDto>
            {
                IsSuccess = false,
                ErrorMessage = "Template metadata not found"
            };

        if (dto.Name != null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return new ServiceResult<TemplateMetadataDetailDto>
                {
                    IsSuccess = false,
                    ErrorMessage = "Template name cannot be empty"
                };
            template.Name = dto.Name.Trim();
        }

        if (dto.Description != null)
            template.Description = dto.Description.Trim();

        if (dto.JsonSchema != null)
            template.JsonSchema = dto.JsonSchema;

        var oldIndexKeys = dto.IndexKeys != null ? template.IndexKeys?.ToList() : null;

        if (dto.IndexKeys != null)
            template.IndexKeys = dto.IndexKeys;

        template.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Sync Qdrant payload indexes if IndexKeys changed
        if (dto.IndexKeys != null && !string.IsNullOrEmpty(template.JsonSchema))
        {
            var oldKeys = oldIndexKeys ?? new List<string>();
            var newKeys = template.IndexKeys ?? new List<string>();
            var keysToRemove = oldKeys.Except(newKeys).ToList();
            var keysToAdd = newKeys.Except(oldKeys).ToList();

            foreach (var key in keysToRemove)
            {
                try
                {
                    await _qdrantService.DeletePayloadIndexAsync("documents", key);
                    _logger.LogInformation("[Qdrant] Deleted payload index '{Field}' on collection 'documents'", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Qdrant] Failed to delete payload index '{Field}' on collection 'documents'", key);
                }
            }

            foreach (var key in keysToAdd)
            {
                try
                {
                    var schemaType = MetadataSchemaHelper.GetPayloadSchemaType(template.JsonSchema, key);
                    await _qdrantService.CreatePayloadIndexAsync("documents", key, schemaType);
                    _logger.LogInformation("[Qdrant] Created payload index '{Field}' on collection 'documents'", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Qdrant] Failed to create payload index '{Field}' on collection 'documents'", key);
                }
            }
        }

        _logger.LogInformation("TemplateMetadata {TemplateId} updated by admin {UserId}", id, userId);

        return new ServiceResult<TemplateMetadataDetailDto>
        {
            IsSuccess = true,
            Data = new TemplateMetadataDetailDto(
                template.Id,
                template.Name,
                template.Description,
                template.JsonSchema,
                template.IndexKeys,
                template.CreatedAt,
                template.UpdatedAt,
                template.CreatedBy
            )
        };
    }

    public async Task<ServiceResult> DeleteAsync(Guid userId, Guid id)
    {
        if (!await _accessControl.IsAdminAsync(userId))
            return new ServiceResult
            {
                IsSuccess = false,
                ErrorMessage = "Only admins can delete template metadata"
            };

        var template = await _context.TemplateMetadatas.FindAsync(id);
        if (template == null)
            return new ServiceResult
            {
                IsSuccess = false,
                ErrorMessage = "Template metadata not found"
            };

        var datasetUsingTemplate = await _context.Datasets
            .AsNoTracking()
            .AnyAsync(d => d.TemplateMetadataId == id);

        if (datasetUsingTemplate)
            return new ServiceResult
            {
                IsSuccess = false,
                ErrorMessage = "Cannot delete template metadata because it is assigned to one or more datasets"
            };

        _context.TemplateMetadatas.Remove(template);
        await _context.SaveChangesAsync();

        _logger.LogInformation("TemplateMetadata {TemplateId} deleted by admin {UserId}", id, userId);

        return new ServiceResult { IsSuccess = true };
    }
}
