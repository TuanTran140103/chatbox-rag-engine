using MarkdownGenQAs.Application.Dto.Admin.Dataset;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Models;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class AdminDatasetService
{
    private readonly IUnitOfWork _uow;
    private readonly ApplicationContext _context;
    private readonly ILogger<AdminDatasetService> _logger;

    public AdminDatasetService(
        IUnitOfWork uow,
        ApplicationContext context,
        ILogger<AdminDatasetService> logger)
    {
        _uow = uow;
        _context = context;
        _logger = logger;
    }

    public async Task<List<DatasetOverviewDto>> GetAllDatasetsAsync(int page = 1, int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var skip = (page - 1) * pageSize;

        var datasets = await _context.Datasets
            .Include(d => d.Owner)
            .Include(d => d.OrganizationUnit)
            .Include(d => d.Items)
            .OrderByDescending(d => d.UpdatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        var result = new List<DatasetOverviewDto>();
        foreach (var ds in datasets)
        {
            var itemCount = ds.Items?.Count ?? 0;
            var docCount = ds.Items?.Count(i => i.DocumentId.HasValue) ?? 0;

            var totalStorage = 0L;

            result.Add(new DatasetOverviewDto(
                ds.Id,
                ds.Name,
                ds.Owner?.UserName ?? "Unknown",
                ds.OrganizationUnit?.Name,
                itemCount,
                docCount,
                FormatBytes(totalStorage),
                ds.IsPublicToUnit,
                ds.CreatedAt,
                ds.UpdatedAt
            ));
        }

        return result;
    }

    public async Task<int> GetTotalDatasetsCountAsync()
    {
        return await _context.Datasets.CountAsync();
    }

    public async Task<List<DatasetItemDto>> GetDatasetItemsAsync(Guid datasetId, Guid? parentId = null)
    {
        var items = (await _uow.DatasetItems
            .FindAsync(di => di.DatasetId == datasetId && di.ParentId == parentId)).ToList();

        var result = new List<DatasetItemDto>();
        foreach (var item in items)
        {
            var hasChildren = await _context.DatasetItems
                .AnyAsync(di => di.DatasetId == datasetId && di.ParentId == item.Id);

            long? sizeBytes = null;

            var childCount = await _context.DatasetItems
                .CountAsync(di => di.DatasetId == datasetId && di.ParentId == item.Id);

            result.Add(new DatasetItemDto(
                item.Id,
                item.Name,
                item.ItemType.ToString(),
                hasChildren,
                sizeBytes.HasValue ? FormatBytes(sizeBytes.Value) : null,
                sizeBytes,
                childCount
            ));
        }

        return result.OrderBy(i => i.ItemType == "Folder" ? 0 : 1).ThenBy(i => i.Name).ToList();
    }

    public async Task<ServiceResult> TransferOwnershipAsync(Guid datasetId, Guid newOwnerUserId)
    {
        var dataset = await _context.Datasets.FindAsync(datasetId);
        if (dataset == null)
        {
            _logger.LogWarning("Dataset {DatasetId} not found", datasetId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = "Dataset not found" };
        }

        var newOwner = await _context.Users.FindAsync(newOwnerUserId);
        if (newOwner == null)
        {
            _logger.LogWarning("User {UserId} not found", newOwnerUserId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = "New owner user not found" };
        }

        dataset.OwnerUserId = newOwnerUserId;
        dataset.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        _logger.LogInformation("Dataset {DatasetId} ownership transferred to {UserId}", datasetId, newOwnerUserId);

        return new ServiceResult { IsSuccess = true };
    }

    public async Task<List<AccessShareDto>> GetDatasetSharesAsync(Guid datasetId)
    {
        var shares = await _context.AccessShares
            .Include(s => s.ShareToUser)
            .Include(s => s.ShareToOU)
            .Include(s => s.Grantor)
            .Where(s => s.DatasetId == datasetId)
            .ToListAsync();

        return shares.Select(s => new AccessShareDto(
            s.Id,
            s.DatasetId,
            s.DatasetItemId,
            s.ShareToUserId,
            s.ShareToUser?.UserName,
            s.ShareToOUId,
            s.ShareToOU?.Name,
            s.PermissionMask,
            FormatPermissions(s.PermissionMask),
            s.GrantedBy,
            s.Grantor?.UserName ?? "Unknown",
            s.CreatedAt
        )).ToList();
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private static string FormatPermissions(DatasetPermissions permissions)
    {
        var parts = new List<string>();
        if (permissions.HasFlag(DatasetPermissions.Read)) parts.Add("Read");
        if (permissions.HasFlag(DatasetPermissions.Update)) parts.Add("Update");
        if (permissions.HasFlag(DatasetPermissions.Delete)) parts.Add("Delete");
        if (permissions.HasFlag(DatasetPermissions.Share)) parts.Add("Share");
        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }
}
