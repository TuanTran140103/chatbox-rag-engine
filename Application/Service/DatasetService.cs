using MarkdownGenQAs.Application.Dto.Documents;
using MarkdownGenQAs.Application.Dto.User.Dataset;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Utils;
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
    private readonly IAuditUserAccessor _auditUserAccessor;
    private readonly IS3Service _s3Service;
    private readonly IQdrantService _qdrantService;
    private readonly ILogger<DatasetService> _logger;

    public DatasetService(
        ApplicationContext context,
        IUnitOfWork uow,
        IAccessControlService accessControl,
        IAuditUserAccessor auditUserAccessor,
        IS3Service s3Service,
        IQdrantService qdrantService,
        ILogger<DatasetService> logger)
    {
        _context = context;
        _uow = uow;
        _accessControl = accessControl;
        _auditUserAccessor = auditUserAccessor;
        _s3Service = s3Service;
        _qdrantService = qdrantService;
        _logger = logger;
    }

    public async Task<ServiceResult<PagedResponse<DatasetDto>>> GetMyDatasetsAsync(Guid userId, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var accessibleIds = await _accessControl.GetAccessibleDatasetIdsAsync(userId);

        var query = _context.Datasets
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

        var items = datasets.Select(d => new DatasetDto(
            d.Id,
            d.Name,
            d.Description,
            d.Items?.Count ?? 0,
            d.Items?.Count(i => i.DocumentId.HasValue) ?? 0,
            d.CreatedAt,
            d.UpdatedAt,
            d.TemplateMetadataId,
            d.TemplateMetadata?.Name
        )).ToList();

        return new ServiceResult<PagedResponse<DatasetDto>>
        {
            IsSuccess = true,
            Data = new PagedResponse<DatasetDto>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            }
        };
    }

    public async Task<ServiceResult<DatasetDto>> GetDatasetByIdAsync(Guid userId, Guid datasetId)
    {
        var dataset = await _context.Datasets
            .Include(d => d.Items)
            .Include(d => d.TemplateMetadata)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanViewDatasetAsync(userId, dataset))
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        return new ServiceResult<DatasetDto>
        {
            IsSuccess = true,
            Data = new DatasetDto(
                dataset.Id,
                dataset.Name,
                dataset.Description,
                dataset.Items?.Count ?? 0,
                dataset.Items?.Count(i => i.DocumentId.HasValue) ?? 0,
                dataset.CreatedAt,
                dataset.UpdatedAt,
                dataset.TemplateMetadataId,
                dataset.TemplateMetadata?.Name
            )
        };
    }

    public async Task<ServiceResult<DatasetDto>> CreateDatasetAsync(Guid userId, CreateDatasetRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset name is required" };

        if (dto.Name.Length > 255)
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset name must not exceed 255 characters" };

        var userDepartmentIds = _auditUserAccessor.GetUserDepartmentIds();
        _logger.LogUserDepartments(userId, userDepartmentIds, dto.DepartmentId);

        if (dto.DepartmentId.HasValue && !userDepartmentIds.Contains(dto.DepartmentId.Value))
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "You are not a member of this department" };

        var templateExists = await _context.TemplateMetadatas
            .AnyAsync(t => t.Id == dto.TemplateMetadataId);
        if (!templateExists)
            return new ServiceResult<DatasetDto>
            {
                IsSuccess = false,
                ErrorMessage = "Template metadata not found"
            };

        var dataset = new Dataset
        {
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim(),
            OwnerUserId = userId,
            DepartmentId = dto.DepartmentId,
            TemplateMetadataId = dto.TemplateMetadataId,
            CountDocument = 0
        };

        using var transaction = await _context.Database.BeginTransactionAsync();
        await _uow.Datasets.AddAsync(dataset);
        await _uow.SaveChangesAsync();

        try
        {
            await _qdrantService.CreateShardKeyAsync("documents", dataset.Id);
            _logger.LogInformation("[Qdrant] Created shard key for dataset {DatasetId}", dataset.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Qdrant] Failed to create shard key for dataset {DatasetId}, rolling back", dataset.Id);
            throw;
        }

        await transaction.CommitAsync();

        _logger.LogInformation("Dataset {DatasetId} created by user {UserId}", dataset.Id, userId);

        return await GetDatasetByIdAsync(userId, dataset.Id);
    }

    public async Task<ServiceResult<DatasetDto>> UpdateDatasetAsync(Guid userId, Guid datasetId, UpdateDatasetRequestDto dto)
    {
        var dataset = await _context.Datasets
            .Include(d => d.Items)
            .Include(d => d.TemplateMetadata)
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        var userDepartmentIds = _auditUserAccessor.GetUserDepartmentIds();
        if (dataset.DepartmentId.HasValue && !userDepartmentIds.Contains(dataset.DepartmentId.Value))
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (dto.Name != null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset name cannot be empty" };
            if (dto.Name.Length > 255)
                return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Dataset name must not exceed 255 characters" };
            dataset.Name = dto.Name.Trim();
        }

        if (dto.Description != null)
        {
            if (dto.Description.Length > 1000)
                return new ServiceResult<DatasetDto> { IsSuccess = false, ErrorMessage = "Description must not exceed 1000 characters" };
            dataset.Description = dto.Description.Trim();
        }

        dataset.UpdatedAt = DateTime.UtcNow;

        _uow.Datasets.Update(dataset);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Dataset {DatasetId} updated by user {UserId}", dataset.Id, userId);

        return new ServiceResult<DatasetDto>
        {
            IsSuccess = true,
            Data = new DatasetDto(
                dataset.Id,
                dataset.Name,
                dataset.Description,
                dataset.Items?.Count ?? 0,
                dataset.Items?.Count(i => i.DocumentId.HasValue) ?? 0,
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

        var userDepartmentIds = _auditUserAccessor.GetUserDepartmentIds();
        if (dataset.DepartmentId.HasValue && !userDepartmentIds.Contains(dataset.DepartmentId.Value))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanDeleteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            _uow.Datasets.Delete(dataset);
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
                    doc.IsIndexed,
                    doc.DocumentJob != null ? new DocumentJobBriefDto(
                        doc.DocumentJob.OcrJobId,
                        doc.DocumentJob.IndexingJobId,
                        doc.DocumentJob.StatusOcr.ToString(),
                        doc.DocumentJob.StatusIndexing.ToString()
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

    public async Task<ServiceResult<CreateItemResponseDto>> CreateFolderAsync(
        Guid userId, Guid datasetId, string name, Guid? parentId)
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

        if (string.IsNullOrWhiteSpace(name))
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Folder name is required" };

        if (name.Length > 255)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Folder name must not exceed 255 characters" };

        var maxSortOrder = await _context.DatasetItems
            .Where(i => i.DatasetId == datasetId && i.ParentId == parentId)
            .MaxAsync(i => (int?)i.SortOrder) ?? -1;

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

    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    public async Task<ServiceResult<InitUploadResponseDto>> InitUploadAsync(
        Guid userId, Guid datasetId, string fileName, long fileSize, Guid? parentId, string? contentType)
    {
        if (fileSize <= 0)
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "FileSize must be greater than 0" };

        if (fileSize > MaxFileSizeBytes)
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = $"File size exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)}MB" };

        var dataset = await _context.Datasets
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension != ".pdf")
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Only PDF files are supported" };

        var existingFiles = await _uow.Documents.FindAsync(f => f.FileName == fileName);
        var existingFile = existingFiles.FirstOrDefault();
        if (existingFile != null)
        {
            if (existingFile.Status == StatusDocument.Uploading)
            {
                var objectKey = !string.IsNullOrEmpty(existingFile.ObjectKeyFilePdf)
                    ? existingFile.ObjectKeyFilePdf
                    : S3Helper.NormalizeObjectKey(fileName);

                var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
                    objectKey, S3BucketName.OCRUploadPdf, contentType ?? "application/pdf", TimeSpan.FromHours(1));

                existingFile.ObjectKeyFilePdf = objectKey;
                existingFile.FileSize = fileSize;
                existingFile.ContentType = contentType ?? "application/pdf";
                _uow.Documents.Update(existingFile);
                await _uow.SaveChangesAsync();

                return new ServiceResult<InitUploadResponseDto>
                {
                    IsSuccess = true,
                    Data = new InitUploadResponseDto
                    {
                        DocumentId = existingFile.Id,
                        ObjectKey = objectKey,
                        PresignedUrl = presignedUrl,
                        ExpiresAt = DateTime.UtcNow.AddHours(1)
                    }
                };
            }

            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = $"A file with name '{fileName}' already exists" };
        }

        var document = new Document
        {
            FileName = fileName,
            ObjectKeyFilePdf = "",
            Status = StatusDocument.Uploading,
            ProcessingTimeOcr = 0,
            UserId = userId,
            FileSize = fileSize,
            ContentType = contentType ?? "application/pdf"
        };

        await _uow.Documents.AddAsync(document);
        await _uow.SaveChangesAsync();

        var newObjectKey = S3Helper.NormalizeObjectKey(fileName);
        var newPresignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
            newObjectKey, S3BucketName.OCRUploadPdf, contentType ?? "application/pdf", TimeSpan.FromHours(1));

        document.ObjectKeyFilePdf = newObjectKey;
        _uow.Documents.Update(document);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Init upload for document {FileName} (size={FileSize}) (id={DocId}) in dataset {DatasetId} by user {UserId}",
            fileName, fileSize, document.Id, datasetId, userId);

        return new ServiceResult<InitUploadResponseDto>
        {
            IsSuccess = true,
            Data = new InitUploadResponseDto
            {
                DocumentId = document.Id,
                ObjectKey = newObjectKey,
                PresignedUrl = newPresignedUrl,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            }
        };
    }

    public async Task<ServiceResult<InitUploadBulkResponseDto>> InitUploadBulkAsync(
        Guid userId, Guid datasetId, List<BulkFileInfoDto> files)
    {
        if (files == null || files.Count == 0)
            return new ServiceResult<InitUploadBulkResponseDto> { IsSuccess = false, ErrorMessage = "At least one file is required" };

        if (files.Count > 50)
            return new ServiceResult<InitUploadBulkResponseDto> { IsSuccess = false, ErrorMessage = "Maximum 50 files per request" };

        var dataset = await _context.Datasets
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<InitUploadBulkResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<InitUploadBulkResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        var errors = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (string.IsNullOrWhiteSpace(file.FileName))
                errors.Add($"Item #{i + 1}: FileName is required");

            if (file.FileSize <= 0)
                errors.Add($"Item #{i + 1}: FileSize must be greater than 0");

            if (file.FileSize > MaxFileSizeBytes)
                errors.Add($"Item #{i + 1}: File size exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)}MB");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".pdf")
                errors.Add($"Item #{i + 1}: Only PDF files are supported");
        }

        var duplicateNames = files.GroupBy(f => f.FileName).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var name in duplicateNames)
            errors.Add($"Duplicate file name: '{name}'");

        if (errors.Count > 0)
            return new ServiceResult<InitUploadBulkResponseDto> { IsSuccess = false, ErrorMessage = string.Join("; ", errors) };

        var existingDocs = await _uow.Documents.FindAsync(f => files.Select(fi => fi.FileName).Contains(f.FileName));
        var existingUploading = existingDocs.Where(d => d.Status == StatusDocument.Uploading).ToDictionary(d => d.FileName);
        var existingOtherNames = existingDocs.Where(d => d.Status != StatusDocument.Uploading).Select(d => d.FileName).ToHashSet();

        foreach (var name in existingOtherNames)
            errors.Add($"A file with name '{name}' already exists");

        if (errors.Count > 0)
            return new ServiceResult<InitUploadBulkResponseDto> { IsSuccess = false, ErrorMessage = string.Join("; ", errors) };

        var results = new List<InitUploadResponseDto>();
        foreach (var file in files)
        {
            Document document;
            InitUploadResponseDto result;

            if (existingUploading.TryGetValue(file.FileName, out var existingDoc))
            {
                document = existingDoc;
                document.FileSize = file.FileSize;
                document.ContentType = file.ContentType ?? "application/pdf";

                var objectKey = !string.IsNullOrEmpty(document.ObjectKeyFilePdf)
                    ? document.ObjectKeyFilePdf
                    : S3Helper.NormalizeObjectKey(file.FileName);

                var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
                    objectKey, S3BucketName.OCRUploadPdf, file.ContentType ?? "application/pdf", TimeSpan.FromHours(1));

                document.ObjectKeyFilePdf = objectKey;
                _uow.Documents.Update(document);
                await _uow.SaveChangesAsync();

                result = new InitUploadResponseDto
                {
                    DocumentId = document.Id,
                    ObjectKey = objectKey,
                    PresignedUrl = presignedUrl,
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                };
            }
            else
            {
                document = new Document
                {
                    FileName = file.FileName,
                    ObjectKeyFilePdf = "",
                    Status = StatusDocument.Uploading,
                    ProcessingTimeOcr = 0,
                    UserId = userId,
                    FileSize = file.FileSize,
                    ContentType = file.ContentType ?? "application/pdf"
                };

                await _uow.Documents.AddAsync(document);
                await _uow.SaveChangesAsync();

                var objectKey = S3Helper.NormalizeObjectKey(file.FileName);
                var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
                    objectKey, S3BucketName.OCRUploadPdf, file.ContentType ?? "application/pdf", TimeSpan.FromHours(1));

                document.ObjectKeyFilePdf = objectKey;
                _uow.Documents.Update(document);
                await _uow.SaveChangesAsync();

                result = new InitUploadResponseDto
                {
                    DocumentId = document.Id,
                    ObjectKey = objectKey,
                    PresignedUrl = presignedUrl,
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                };
            }

            results.Add(result);
        }

        _logger.LogInformation("Bulk init upload: {Count} files in dataset {DatasetId} by user {UserId}", files.Count, datasetId, userId);

        return new ServiceResult<InitUploadBulkResponseDto>
        {
            IsSuccess = true,
            Data = new InitUploadBulkResponseDto { Documents = results }
        };
    }

    public async Task<ServiceResult<CreateItemResponseDto>> CompleteUploadAsync(
        Guid userId, Guid datasetId, Guid documentId, Guid? parentId)
    {
        var dataset = await _context.Datasets
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        var document = await _uow.Documents.GetByIdAsync(documentId);
        if (document == null)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Document not found" };

        if (document.Status != StatusDocument.Uploading)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = $"Invalid document status: {document.Status}" };

        if (string.IsNullOrEmpty(document.ObjectKeyFilePdf))
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "Document has no object key" };

        var fileExists = await _s3Service.FileExistsAsync(document.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);
        if (!fileExists)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "File not found in storage. Upload may have failed." };

        var metadata = await _s3Service.GetFileMetadataAsync(document.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);
        if (document.FileSize.HasValue && metadata.ContentLength != document.FileSize.Value)
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = $"File size mismatch: expected {document.FileSize.Value} bytes, got {metadata.ContentLength} bytes" };

        var head = await _s3Service.ReadFileHeadAsync(document.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf, 16);
        if (!ValidateFileUtil.IsValidPdf(head))
            return new ServiceResult<CreateItemResponseDto> { IsSuccess = false, ErrorMessage = "File is not a valid PDF (invalid magic bytes)" };

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

        document.Status = StatusDocument.Uploaded;
        document.DatasetItemId = item.Id;
        _uow.Documents.Update(document);
        await _uow.SaveChangesAsync();

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
                    document.IsIndexed,
                    null
                )
            }
        };
    }

    public async Task<ServiceResult<InitUploadResponseDto>> RenewUploadUrlAsync(
        Guid userId, Guid datasetId, Guid documentId)
    {
        var dataset = await _context.Datasets
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Dataset not found" };

        var document = await _uow.Documents.GetByIdAsync(documentId);
        if (document == null)
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Document not found" };

        if (document.Status != StatusDocument.Uploading)
            return new ServiceResult<InitUploadResponseDto> { IsSuccess = false, ErrorMessage = "Cannot renew URL for document in this status" };

        var objectKey = !string.IsNullOrEmpty(document.ObjectKeyFilePdf)
            ? document.ObjectKeyFilePdf
            : S3Helper.NormalizeObjectKey(document.FileName);

        var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
            objectKey, S3BucketName.OCRUploadPdf, "application/pdf", TimeSpan.FromHours(1));

        return new ServiceResult<InitUploadResponseDto>
        {
            IsSuccess = true,
            Data = new InitUploadResponseDto
            {
                DocumentId = document.Id,
                ObjectKey = objectKey,
                PresignedUrl = presignedUrl,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            }
        };
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
