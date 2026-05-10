using MarkdownGenQAs.Application.Dto.TemplateMetadata;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class TemplateMetadataService : ITemplateMetadataService
{
    private readonly ApplicationContext _context;
    private readonly IAccessControlService _accessControl;
    private readonly ILogger<TemplateMetadataService> _logger;

    public TemplateMetadataService(
        ApplicationContext context,
        IAccessControlService accessControl,
        ILogger<TemplateMetadataService> logger)
    {
        _context = context;
        _accessControl = accessControl;
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
            JsonSchema = dto.JsonSchema
        };

        _context.TemplateMetadatas.Add(template);
        await _context.SaveChangesAsync();

        _logger.LogInformation("TemplateMetadata {TemplateId} created by admin {UserId}", template.Id, userId);

        return new ServiceResult<TemplateMetadataDetailDto>
        {
            IsSuccess = true,
            Data = new TemplateMetadataDetailDto(
                template.Id,
                template.Name,
                template.Description,
                template.JsonSchema,
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

        template.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("TemplateMetadata {TemplateId} updated by admin {UserId}", id, userId);

        return new ServiceResult<TemplateMetadataDetailDto>
        {
            IsSuccess = true,
            Data = new TemplateMetadataDetailDto(
                template.Id,
                template.Name,
                template.Description,
                template.JsonSchema,
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

        _context.TemplateMetadatas.Remove(template);
        await _context.SaveChangesAsync();

        _logger.LogInformation("TemplateMetadata {TemplateId} deleted by admin {UserId}", id, userId);

        return new ServiceResult { IsSuccess = true };
    }
}
