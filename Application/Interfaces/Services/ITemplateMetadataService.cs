using MarkdownGenQAs.Application.Dto.TemplateMetadata;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface ITemplateMetadataService
{
    Task<List<TemplateMetadataListDto>> GetAllAsync();
    Task<ServiceResult<TemplateMetadataDetailDto>> GetByIdAsync(Guid id);
    Task<ServiceResult<TemplateMetadataDetailDto>> CreateAsync(Guid userId, CreateTemplateMetadataRequestDto dto);
    Task<ServiceResult<TemplateMetadataDetailDto>> UpdateAsync(Guid userId, Guid id, UpdateTemplateMetadataRequestDto dto);
    Task<ServiceResult> DeleteAsync(Guid userId, Guid id);
}
