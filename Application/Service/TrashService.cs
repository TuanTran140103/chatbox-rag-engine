using MarkdownGenQAs.Application.Dto.Admin.Trash;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class TrashService : ITrashService
{
    private readonly ApplicationContext _context;
    private readonly IAccessControlService _accessControl;
    private readonly ILogger<TrashService> _logger;

    public TrashService(ApplicationContext context, IAccessControlService accessControl, ILogger<TrashService> logger)
    {
        _context = context;
        _accessControl = accessControl;
        _logger = logger;
    }

    public async Task<List<TrashItemDto>> GetTrashItemsAsync(Guid userId)
    {
        var result = new List<TrashItemDto>();
        var isAdmin = await _accessControl.IsAdminAsync(userId);

        // OUs — admin only
        if (isAdmin)
        {
            var deletedOUs = await _context.OrganizationUnits
                .IgnoreQueryFilters()
                .Where(o => o.IsDeleted)
                .AsNoTracking()
                .ToListAsync();

            foreach (var ou in deletedOUs)
            {
                var hasDeletedAncestor = await _context.OrganizationUnits
                    .IgnoreQueryFilters()
                    .AnyAsync(o => o.IsDeleted && ou.Path.StartsWith(o.Path) && o.Id != ou.Id);

                if (!hasDeletedAncestor)
                {
                    result.Add(new TrashItemDto(ou.Id, TrashItemType.OrganizationUnit, ou.Name, null, ou.DeletedAt ?? ou.CreatedAt, ou.DeletedBy));
                }
            }
        }

        // Datasets — filter by accessibility
        var accessibleDatasetIds = await _accessControl.GetAccessibleDatasetIdsAsync(userId);

        var deletedDatasets = await _context.Datasets
            .IgnoreQueryFilters()
            .Where(d => d.IsDeleted && accessibleDatasetIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, d.OUId, d.DeletedAt, d.DeletedBy, OUName = d.OrganizationUnit!.Name })
            .AsNoTracking()
            .ToListAsync();

        foreach (var ds in deletedDatasets)
        {
            var hasDeletedParentOU = ds.OUId.HasValue && await _context.OrganizationUnits
                .IgnoreQueryFilters()
                .AnyAsync(o => o.Id == ds.OUId && o.IsDeleted);

            if (!hasDeletedParentOU)
            {
                result.Add(new TrashItemDto(ds.Id, TrashItemType.Dataset, ds.Name, ds.OUName, ds.DeletedAt ?? DateTime.UtcNow, ds.DeletedBy));
            }
        }

        // Items — filter by accessible dataset IDs
        var deletedItems = await _context.DatasetItems
            .IgnoreQueryFilters()
            .Where(i => i.IsDeleted && accessibleDatasetIds.Contains(i.DatasetId))
            .Select(i => new { i.Id, i.Name, i.ItemType, i.Path, i.DatasetId, i.DeletedAt, i.DeletedBy, DatasetName = i.Dataset.Name })
            .AsNoTracking()
            .ToListAsync();

        foreach (var item in deletedItems)
        {
            var hasDeletedAncestorInDataset = await _context.DatasetItems
                .IgnoreQueryFilters()
                .AnyAsync(d => d.IsDeleted && d.ItemType == DatasetItemType.Folder && item.Path.StartsWith(d.Path) && d.Id != item.Id);

            var hasDeletedDataset = await _context.Datasets
                .IgnoreQueryFilters()
                .AnyAsync(d => d.Id == item.DatasetId && d.IsDeleted);

            if (!hasDeletedAncestorInDataset && !hasDeletedDataset)
            {
                result.Add(new TrashItemDto(
                    item.Id,
                    item.ItemType == DatasetItemType.Folder ? TrashItemType.Folder : TrashItemType.Document,
                    item.Name,
                    item.DatasetName,
                    item.DeletedAt ?? DateTime.UtcNow,
                    item.DeletedBy));
            }
        }

        return result;
    }

    public async Task<ServiceResult> RestoreItemAsync(TrashItemType type, Guid id, Guid userId)
    {
        switch (type)
        {
            case TrashItemType.OrganizationUnit:
                if (!await _accessControl.IsAdminAsync(userId))
                    return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };
                return await RestoreOUAsync(id);
            case TrashItemType.Dataset:
                return await RestoreDatasetAsync(id, userId);
            case TrashItemType.Folder:
            case TrashItemType.Document:
                return await RestoreDatasetItemAsync(id, userId);
            default:
                return new ServiceResult { IsSuccess = false, ErrorMessage = $"Unknown type: {type}" };
        }
    }

    private async Task<ServiceResult> RestoreOUAsync(Guid ouId)
    {
        var ou = await _context.OrganizationUnits
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == ouId);

        if (ou == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "OU not found" };

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            ou.IsDeleted = false;
            ou.DeletedAt = null;
            ou.DeletedBy = null;

            var existingStats = await _context.SystemStatistics
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.OUId == ouId);
            if (existingStats != null)
            {
                existingStats.IsDeleted = false;
                existingStats.DeletedAt = null;
                existingStats.DeletedBy = null;
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        _logger.LogInformation("OU {OUId} restored from trash", ouId);
        return new ServiceResult { IsSuccess = true };
    }

    private async Task<ServiceResult> RestoreDatasetAsync(Guid datasetId, Guid userId)
    {
        var dataset = await _context.Datasets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            dataset.IsDeleted = false;
            dataset.DeletedAt = null;
            dataset.DeletedBy = null;

            if (dataset.OUId.HasValue)
            {
                var ouStats = await _context.SystemStatistics
                    .FirstOrDefaultAsync(s => s.OUId == dataset.OUId);
                if (ouStats != null)
                    ouStats.TotalDatasets += 1;
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        _logger.LogInformation("Dataset {DatasetId} restored from trash", datasetId);
        return new ServiceResult { IsSuccess = true };
    }

    private async Task<ServiceResult> RestoreDatasetItemAsync(Guid itemId, Guid userId)
    {
        var item = await _context.DatasetItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == itemId);

        if (item == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Item not found" };

        var dataset = await _context.Datasets.FindAsync(item.DatasetId);
        if (dataset == null || !await _accessControl.CanWriteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };

        item.IsDeleted = false;
        item.DeletedAt = null;
        item.DeletedBy = null;
        await _context.SaveChangesAsync();

        _logger.LogInformation("DatasetItem {ItemId} restored from trash", itemId);
        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult> EmptyTrashAsync(Guid userId)
    {
        if (!await _accessControl.IsAdminAsync(userId))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };

        var trash = await GetTrashItemsAsync(userId);
        foreach (var item in trash)
        {
            await PermanentDeleteItemAsync(item.Type, item.Id, userId);
        }
        _logger.LogInformation("Trash emptied");
        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult> PermanentDeleteItemAsync(TrashItemType type, Guid id, Guid userId)
    {
        switch (type)
        {
            case TrashItemType.OrganizationUnit:
                if (!await _accessControl.IsAdminAsync(userId))
                    return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };
                return await PermanentDeleteOUAsync(id);
            case TrashItemType.Dataset:
                return await PermanentDeleteDatasetAsync(id, userId);
            case TrashItemType.Folder:
            case TrashItemType.Document:
                return await PermanentDeleteDatasetItemAsync(id, userId);
            default:
                return new ServiceResult { IsSuccess = false, ErrorMessage = $"Unknown type: {type}" };
        }
    }

    private async Task<ServiceResult> PermanentDeleteOUAsync(Guid ouId)
    {
        var ou = await _context.OrganizationUnits
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == ouId);

        if (ou == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "OU not found" };

        if (!ou.IsDeleted)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "OU is not in trash" };

        var descendantIds = await _context.OrganizationUnits
            .IgnoreQueryFilters()
            .Where(o => o.Path.StartsWith(ou.Path) || o.Id == ouId)
            .Select(o => o.Id)
            .ToListAsync();

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Documents"" WHERE ""DatasetItemId"" IN (
                    SELECT ""Id"" FROM ""DatasetItems"" WHERE ""DatasetId"" IN (
                        SELECT ""Id"" FROM ""Datasets"" WHERE ""OUId"" = ANY(@p0)
                    )
                )", descendantIds);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DatasetItems"" WHERE ""DatasetId"" IN (
                    SELECT ""Id"" FROM ""Datasets"" WHERE ""OUId"" = ANY(@p0)
                )", descendantIds);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""AccessShares"" WHERE ""DatasetId"" IN (
                    SELECT ""Id"" FROM ""Datasets"" WHERE ""OUId"" = ANY(@p0)
                )", descendantIds);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Datasets"" WHERE ""OUId"" = ANY(@p0)", descendantIds);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""UserPositions"" WHERE ""OUId"" = ANY(@p0)", descendantIds);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""SystemStatistics"" WHERE ""OUId"" = ANY(@p0)", descendantIds);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""OrganizationUnits"" WHERE ""Id"" = ANY(@p0)", descendantIds);

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        _logger.LogInformation("OU {OUId} and {Count} descendants permanently deleted", ouId, descendantIds.Count);
        return new ServiceResult { IsSuccess = true };
    }

    private async Task<ServiceResult> PermanentDeleteDatasetAsync(Guid datasetId, Guid userId)
    {
        var dataset = await _context.Datasets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == datasetId);

        if (dataset == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };

        if (!dataset.IsDeleted)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset is not in trash" };

        if (!await _accessControl.CanDeleteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };

        var ouId = dataset.OUId;

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Documents"" WHERE ""DatasetItemId"" IN (
                    SELECT ""Id"" FROM ""DatasetItems"" WHERE ""DatasetId"" = CAST(@p0 AS uuid)
                )", datasetId);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DatasetItems"" WHERE ""DatasetId"" = CAST(@p0 AS uuid)", datasetId);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""AccessShares"" WHERE ""DatasetId"" = CAST(@p0 AS uuid)", datasetId);

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Datasets"" WHERE ""Id"" = CAST(@p0 AS uuid)", datasetId);

            if (ouId.HasValue)
            {
                var ouStats = await _context.SystemStatistics
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.OUId == ouId);
                if (ouStats != null)
                    ouStats.TotalDatasets = Math.Max(0, ouStats.TotalDatasets - 1);
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        _logger.LogInformation("Dataset {DatasetId} permanently deleted", datasetId);
        return new ServiceResult { IsSuccess = true };
    }

    private async Task<ServiceResult> PermanentDeleteDatasetItemAsync(Guid itemId, Guid userId)
    {
        var item = await _context.DatasetItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == itemId);

        if (item == null)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Item not found" };

        if (!item.IsDeleted)
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Item is not in trash" };

        var dataset = await _context.Datasets.FindAsync(item.DatasetId);
        if (dataset == null || !await _accessControl.CanDeleteDatasetAsync(userId, dataset))
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Forbidden" };

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            if (item.ItemType == DatasetItemType.Folder)
            {
                await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""Documents"" WHERE ""DatasetItemId"" IN (
                        SELECT ""Id"" FROM ""DatasetItems"" WHERE ""DatasetId"" = CAST(@p0 AS uuid) AND ""Path"" LIKE @p1 || '%'
                    )", item.DatasetId, item.Path);

                await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""DatasetItems"" WHERE ""DatasetId"" = CAST(@p0 AS uuid) AND ""Path"" LIKE @p1 || '%'",
                    item.DatasetId, item.Path);
            }
            else
            {
                await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""Documents"" WHERE ""DatasetItemId"" = CAST(@p0 AS uuid)", itemId);

                await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""DatasetItems"" WHERE ""Id"" = CAST(@p0 AS uuid)", itemId);
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        _logger.LogInformation("DatasetItem {ItemId} permanently deleted", itemId);
        return new ServiceResult { IsSuccess = true };
    }
}
