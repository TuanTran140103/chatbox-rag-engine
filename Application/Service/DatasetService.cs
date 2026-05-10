using System.Text;
using MarkdownGenQAs.Application.Dto.User.Dataset;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Infrastructure.Interceptors;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class DatasetService
{
    private readonly ApplicationContext _context;
    private readonly IUnitOfWork _uow;
    private readonly IAccessControlService _accessControl;
    private readonly IS3Service _s3Service;
    private readonly ILogger<DatasetService> _logger;

    public DatasetService(
        ApplicationContext context,
        IUnitOfWork uow,
        IAccessControlService accessControl,
        IS3Service s3Service,
        ILogger<DatasetService> logger)
    {
        _context = context;
        _uow = uow;
        _accessControl = accessControl;
        _s3Service = s3Service;
        _logger = logger;
    }

    public async Task<ServiceResult<PagedResponse<DatasetListDto>>> GetMyDatasetsAsync(Guid userId, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var accessibleIds = await _accessControl.GetAccessibleDatasetIdsAsync(userId);

        var query = _context.Datasets
            .Include(d => d.OrganizationUnit)
            .Include(d => d.Items)
            .Include(d => d.TemplateMetadata)
            .AsNoTracking()
            .Where(d => accessibleIds.Contains(d.Id));

        var totalCount = await query.CountAsync();

        var datasets = await query
            .OrderByDescending(d => d.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = datasets.Select(d => new DatasetListDto(
            d.Id,
            d.Name,
            d.OrganizationUnit?.Name,
            d.OUId,
            d.Items?.Count ?? 0,
            d.Items?.Count(i => i.DocumentId.HasValue) ?? 0,
            d.IsPublicToUnit,
            d.CreatedAt,
            d.UpdatedAt,
            d.TemplateMetadataId,
            d.TemplateMetadata?.Name
        )).ToList();

        return new ServiceResult<PagedResponse<DatasetListDto>>
        {
            IsSuccess = true,
            Data = new PagedResponse<DatasetListDto>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            }
        };
    }

    public async Task<ServiceResult<DatasetDetailDto>> GetDatasetByIdAsync(Guid userId, Guid datasetId)
    {
        var dataset = await _context.Datasets
            .Include(d => d.Owner)
            .Include(d => d.OrganizationUnit)
            .Include(d => d.Items)
            .Include(d => d.TemplateMetadata)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanViewDatasetAsync(userId, dataset))
            return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        return new ServiceResult<DatasetDetailDto>
        {
            IsSuccess = true,
            Data = new DatasetDetailDto(
                dataset.Id,
                dataset.Name,
                dataset.Description,
                dataset.Owner?.UserName ?? "Unknown",
                dataset.OrganizationUnit?.Name,
                dataset.OUId,
                dataset.Items?.Count ?? 0,
                dataset.Items?.Count(i => i.DocumentId.HasValue) ?? 0,
                dataset.IsPublicToUnit,
                dataset.CreatedAt,
                dataset.UpdatedAt,
                dataset.TemplateMetadataId,
                dataset.TemplateMetadata?.Name
            )
        };
    }

    public async Task<ServiceResult<DatasetDetailDto>> CreateDatasetAsync(Guid userId, CreateDatasetRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset name is required" };

        if (dto.Name.Length > 255)
            return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset name must not exceed 255 characters" };

        if (dto.OUId.HasValue)
        {
            var inOU = await _accessControl.IsInOUAsync(userId, dto.OUId.Value);
            var isManagerOrAbove = await _accessControl.IsManagerOrAboveOfOUAsync(userId, dto.OUId.Value);
            if (!inOU && !isManagerOrAbove)
                return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "You do not belong to the specified organization unit" };
        }

        var templateExists = await _context.TemplateMetadatas
            .AnyAsync(t => t.Id == dto.TemplateMetadataId);
        if (!templateExists)
            return new ServiceResult<DatasetDetailDto>
            {
                IsSuccess = false,
                ErrorMessage = "Template metadata not found"
            };

        var dataset = new Dataset
        {
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim(),
            OwnerUserId = userId,
            OUId = dto.OUId,
            IsPublicToUnit = dto.IsPublicToUnit,
            TemplateMetadataId = dto.TemplateMetadataId,
            CountDocument = 0
        };

        await _uow.Datasets.AddAsync(dataset);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Dataset {DatasetId} created by user {UserId}", dataset.Id, userId);

        return await GetDatasetByIdAsync(userId, dataset.Id);
    }

    public async Task<ServiceResult<DatasetDetailDto>> UpdateDatasetAsync(Guid userId, Guid datasetId, UpdateDatasetRequestDto dto)
    {
        var dataset = await _context.Datasets
            .Include(d => d.Owner)
            .Include(d => d.OrganizationUnit)
            .Include(d => d.Items)
            .Include(d => d.TemplateMetadata)
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (dto.Name != null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset name cannot be empty" };
            if (dto.Name.Length > 255)
                return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Dataset name must not exceed 255 characters" };
            dataset.Name = dto.Name.Trim();
        }

        if (dto.Description != null)
        {
            if (dto.Description.Length > 1000)
                return new ServiceResult<DatasetDetailDto> { IsSuccess = false, ErrorMessage = "Description must not exceed 1000 characters" };
            dataset.Description = dto.Description.Trim();
        }

        if (dto.IsPublicToUnit.HasValue)
        {
            dataset.IsPublicToUnit = dto.IsPublicToUnit.Value;
        }

        if (dto.TemplateMetadataId.HasValue)
        {
            if (dataset.TemplateMetadataId.HasValue)
                return new ServiceResult<DatasetDetailDto>
                {
                    IsSuccess = false,
                    ErrorMessage = "Cannot change template metadata once assigned"
                };

            var templateExists = await _context.TemplateMetadatas
                .AnyAsync(t => t.Id == dto.TemplateMetadataId.Value);
            if (!templateExists)
                return new ServiceResult<DatasetDetailDto>
                {
                    IsSuccess = false,
                    ErrorMessage = "Template metadata not found"
                };
            dataset.TemplateMetadataId = dto.TemplateMetadataId;
        }

        dataset.UpdatedAt = DateTime.UtcNow;

        _uow.Datasets.Update(dataset);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Dataset {DatasetId} updated by user {UserId}", dataset.Id, userId);

        return new ServiceResult<DatasetDetailDto>
        {
            IsSuccess = true,
            Data = new DatasetDetailDto(
                dataset.Id,
                dataset.Name,
                dataset.Description,
                dataset.Owner?.UserName ?? "Unknown",
                dataset.OrganizationUnit?.Name,
                dataset.OUId,
                dataset.Items?.Count ?? 0,
                dataset.Items?.Count(i => i.DocumentId.HasValue) ?? 0,
                dataset.IsPublicToUnit,
                dataset.CreatedAt,
                dataset.UpdatedAt,
                dataset.TemplateMetadataId,
                dataset.TemplateMetadata?.Name
            )
        };
    }

    public async Task<ServiceResult> DeleteDatasetAsync(Guid userId, Guid datasetId)
    {
        var dataset = await _context.Datasets
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanDeleteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            _uow.Datasets.Delete(dataset);

            if (dataset.OUId.HasValue)
            {
                var ouStats = await _context.SystemStatistics
                    .FirstOrDefaultAsync(s => s.OUId == dataset.OUId);
                if (ouStats != null)
                    ouStats.TotalDatasets = Math.Max(0, ouStats.TotalDatasets - 1);
            }

            await _uow.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        _logger.LogInformation("Dataset {DatasetId} soft-deleted by user {UserId}", datasetId, userId);
        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult<DatasetItemsResponseDto>> GetDatasetItemsAsync(Guid userId, Guid datasetId, Guid? parentId)
    {
        var dataset = await _context.Datasets
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<DatasetItemsResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanViewDatasetAsync(userId, dataset))
            return new ServiceResult<DatasetItemsResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        string parentPath;
        int parentLevel;

        if (parentId == null)
        {
            parentPath = "/";
            parentLevel = 0;
        }
        else
        {
            var parent = await _context.DatasetItems
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == parentId);

            if (parent == null || parent.DatasetId != datasetId)
                return new ServiceResult<DatasetItemsResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

            parentPath = parent.Path;
            parentLevel = parent.Level;
        }

        var items = await _context.DatasetItems
            .AsNoTracking()
            .Where(i => i.DatasetId == datasetId && i.ParentId == parentId && !_context.DatasetItems
                .Any(d => d.IsDeleted && d.ItemType == DatasetItemType.Folder && i.Path.StartsWith(d.Path) && i.Path != d.Path))
            .OrderBy(i => i.ItemType)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToListAsync();

        var itemIds = items.Select(i => i.Id).ToList();
        var childCounts = await _context.DatasetItems
            .Where(i => i.DatasetId == datasetId && itemIds.Contains(i.ParentId!.Value))
            .GroupBy(i => i.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count);

        var documentIds = items.Where(i => i.DocumentId.HasValue).Select(i => i.DocumentId!.Value).ToList();
        var documents = await _context.Documents
            .Include(d => d.DocumentJob)
            .Where(d => documentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d);

        var hasChildren = childCounts.Count > 0;

        var resultItems = items.Select(i =>
        {
            Document? doc = null;
            if (i.DocumentId.HasValue)
                documents.TryGetValue(i.DocumentId.Value, out doc);
            return new DatasetItemDto
            {
                Id = i.Id,
                Name = i.Name,
                ItemType = i.ItemType.ToString(),
                DocumentId = i.DocumentId,
                CreatedAt = i.CreatedAt,
                Item = doc != null ? new DatasetItemDocumentDto(
                    doc.FileName,
                    doc.Status.ToString(),
                    doc.IsOcred,
                    doc.IsQaGenerated,
                    doc.DocumentJob != null ? new DocumentJobBriefDto(
                        doc.DocumentJob.OcrJobId,
                        doc.DocumentJob.GenQaJobId,
                        doc.DocumentJob.StatusOcr.ToString(),
                        doc.DocumentJob.StatusGenQa.ToString()
                    ) : null
                ) : null
            };
        }).ToList();

        return new ServiceResult<DatasetItemsResponseDto>
        {
            IsSuccess = true,
            Data = new DatasetItemsResponseDto
            {
                Path = parentPath,
                Level = parentLevel,
                HasChildren = hasChildren,
                ChildCount = resultItems.Count,
                Items = resultItems
            }
        };
    }

    public async Task<ServiceResult<CreateItemResponseDto>> CreateItemAsync(
        Guid userId, Guid datasetId, int type, string? name,
        Guid? parentId, Stream? fileStream, string? fileName, string? contentType)
    {
        var dataset = await _context.Datasets
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        string parentPath;
        int parentLevel;

        if (parentId == null)
        {
            parentPath = "/";
            parentLevel = -1;
        }
        else
        {
            var parent = await _context.DatasetItems
                .FirstOrDefaultAsync(i => i.Id == parentId && i.DatasetId == datasetId);

            if (parent == null)
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Parent folder not found" };

            if (parent.ItemType != DatasetItemType.Folder)
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Parent must be a folder" };

            parentPath = parent.Path;
            parentLevel = parent.Level;
        }

        var maxSortOrder = await _context.DatasetItems
            .Where(i => i.DatasetId == datasetId && i.ParentId == parentId)
            .MaxAsync(i => (int?)i.SortOrder) ?? -1;

        if (type == 0)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Folder name is required" };

            if (name.Length > 255)
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Folder name must not exceed 255 characters" };

            var itemName = name.Trim();
            var itemPath = parentPath == "/" ? $"/{itemName}/" : $"{parentPath}{itemName}/";

            var item = new DatasetItem
            {
                DatasetId = datasetId,
                Name = itemName,
                ItemType = DatasetItemType.Folder,
                Path = itemPath,
                Level = parentLevel + 1,
                ParentId = parentId,
                SortOrder = maxSortOrder + 1
            };

            await _uow.DatasetItems.AddAsync(item);
            await _uow.SaveChangesAsync();

            _logger.LogInformation("Folder {ItemName} created in dataset {DatasetId} by user {UserId}", itemName, datasetId, userId);

            return new ServiceResult<CreateItemResponseDto>
            {
                IsSuccess = true,
                Data = new CreateItemResponseDto
                {
                    ItemId = item.Id,
                    DocumentId = null,
                    Name = item.Name,
                    ItemType = "Folder",
                    Path = item.Path,
                    Level = item.Level,
                    SortOrder = item.SortOrder,
                    CreatedAt = item.CreatedAt
                }
            };
        }
        else if (type == 1)
        {
            if (fileStream == null || string.IsNullOrEmpty(fileName))
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "File is required for Document type" };

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension != ".pdf")
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Only PDF files are supported" };

            var buffer = new byte[4];
            long currentPosition = 0;
            if (fileStream.CanSeek)
            {
                currentPosition = fileStream.Position;
                await fileStream.ReadExactlyAsync(buffer, 0, 4);
                fileStream.Position = currentPosition;
            }
            else
            {
                await fileStream.ReadExactlyAsync(buffer, 0, 4);
            }

            var signature = Encoding.ASCII.GetString(buffer);
            if (signature != "%PDF")
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Invalid PDF file content" };

            var existingFile = (await _uow.Documents.FindAsync(f => f.FileName == fileName)).FirstOrDefault();
            if (existingFile != null)
                return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = $"A file with name '{fileName}' already exists" };

            var document = new Document
            {
                FileName = fileName,
                ObjectKeyFilePdf = "",
                Status = StatusDocument.Uploaded,
                ProcessingTimeOcr = 0
            };

            await _uow.Documents.AddAsync(document);
            await _uow.SaveChangesAsync();

            using var memoryStream = new MemoryStream();
            fileStream.Position = 0;
            await fileStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var s3Stream = new MemoryStream();
            using var cacheStream = new MemoryStream();
            await memoryStream.CopyToAsync(s3Stream);
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(cacheStream);

            s3Stream.Position = 0;
            cacheStream.Position = 0;

            var s3Task = _s3Service.UploadFileAsync(s3Stream, fileName, S3BucketName.OCRUploadPdf, contentType ?? "application/pdf");
            var cacheTask = DocumentHelper.SaveToCacheAsync(document.Id, DocumentHelper.BucketUploads, ".pdf", cacheStream);

            await Task.WhenAll(s3Task, cacheTask);

            document.ObjectKeyFilePdf = await s3Task;
            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

            var documentName = document.FileName;
            var itemPath = parentPath == "/" ? $"/{documentName}/" : $"{parentPath}{documentName}/";

            var item = new DatasetItem
            {
                DatasetId = datasetId,
                Name = documentName,
                ItemType = DatasetItemType.Document,
                Path = itemPath,
                Level = parentLevel + 1,
                ParentId = parentId,
                DocumentId = document.Id,
                SortOrder = maxSortOrder + 1
            };

            await _uow.DatasetItems.AddAsync(item);
            await _uow.SaveChangesAsync();

            document.DatasetItemId = item.Id;
            _uow.Documents.Update(document);
            await _uow.SaveChangesAsync();

            AuditEntityInterceptor.UpdateDocumentStatistics(_context, dataset.OUId, 1);

            _logger.LogInformation("Document {FileName} added to dataset {DatasetId} by user {UserId}", documentName, datasetId, userId);

            return new ServiceResult<CreateItemResponseDto>
            {
                IsSuccess = true,
                Data = new CreateItemResponseDto
                {
                    ItemId = item.Id,
                    DocumentId = document.Id,
                    Name = item.Name,
                    ItemType = "Document",
                    Path = item.Path,
                    Level = item.Level,
                    SortOrder = item.SortOrder,
                    CreatedAt = item.CreatedAt,
                    Item = new DatasetItemDocumentDto(
                        document.FileName,
                        document.Status.ToString(),
                        document.IsOcred,
                        document.IsQaGenerated,
                        null
                    )
                }
            };
        }
        else
        {
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Invalid type. 0 = Folder, 1 = Document" };
        }
    }

    public async Task<ServiceResult> DeleteItemAsync(Guid userId, Guid datasetId, Guid itemId)
    {
        var dataset = await _context.Datasets
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        var item = await _context.DatasetItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.DatasetId == datasetId);

        if (item == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        _context.DatasetItems.Remove(item);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("DatasetItem {ItemId} soft-deleted by user {UserId}", itemId, userId);
        return new ServiceResult { IsSuccess = true };
    }
}
