using MarkdownGenQAs.Application.Dto.Admin.Stats;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class AdminStatsService
{
    private readonly ApplicationContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<AdminStatsService> _logger;

    public AdminStatsService(
        ApplicationContext context,
        IUnitOfWork uow,
        ILogger<AdminStatsService> logger)
    {
        _context = context;
        _uow = uow;
        _logger = logger;
    }

    public async Task<SystemStatsSummaryDto> GetSummaryAsync()
    {
        var companyWideStats = await _context.SystemStatistics
            .FirstOrDefaultAsync(s => s.OUId == null);

        if (companyWideStats != null)
        {
            return new SystemStatsSummaryDto(
                companyWideStats.TotalDatasets,
                companyWideStats.TotalDocuments,
                FormatBytes(companyWideStats.TotalStorageUsage),
                await _context.OrganizationUnits.CountAsync(),
                await _context.Users.CountAsync()
            );
        }

        return await CalculateSummaryAsync();
    }

    private async Task<SystemStatsSummaryDto> CalculateSummaryAsync()
    {
        var totalDatasets = await _context.Datasets.CountAsync();
        var totalDocuments = await _context.Documents.CountAsync();
        var totalStorage = 0L;
        var totalOUs = await _context.OrganizationUnits.CountAsync();
        var totalUsers = await _context.Users.CountAsync();

        return new SystemStatsSummaryDto(
            totalDatasets,
            totalDocuments,
            FormatBytes(totalStorage),
            totalOUs,
            totalUsers
        );
    }

    public async Task<List<StorageChartDto>> GetStorageChartAsync()
    {
        var companyTotalStorage = await _context.SystemStatistics
            .Where(s => s.OUId == null)
            .Select(s => s.TotalStorageUsage)
            .FirstOrDefaultAsync();

        var ouStats = await _context.SystemStatistics
            .Where(s => s.OUId != null)
            .Include(s => s.OU)
            .ToListAsync();

        var result = new List<StorageChartDto>();

        foreach (var stat in ouStats)
        {
            if (stat.OU == null) continue;

            result.Add(new StorageChartDto(
                stat.OUId,
                stat.OU.Name,
                stat.TotalDatasets,
                stat.TotalDocuments,
                FormatBytes(stat.TotalStorageUsage),
                stat.TotalStorageUsage,
                companyTotalStorage > 0 ? Math.Round((double)stat.TotalStorageUsage / companyTotalStorage * 100, 2) : 0
            ));
        }

        var usedStorage = ouStats.Sum(s => s.TotalStorageUsage);
        var otherStorage = companyTotalStorage - usedStorage;

        if (otherStorage > 0)
        {
            result.Add(new StorageChartDto(
                null,
                "Uncategorized",
                0,
                0,
                FormatBytes(otherStorage),
                otherStorage,
                companyTotalStorage > 0 ? Math.Round((double)otherStorage / companyTotalStorage * 100, 2) : 0
            ));
        }

        return result.OrderByDescending(s => s.StorageBytes).ToList();
    }

    public async Task<List<StorageTreeDto>> GetStorageTreeAsync()
    {
        var allOUs = await _context.OrganizationUnits.ToListAsync();
        var statsList = await _context.SystemStatistics.ToListAsync();
        var statsLookup = statsList.ToLookup(s => s.OUId);
        var childrenLookup = allOUs.ToLookup(o => o.ParentId);

        return childrenLookup[null]
            .OrderBy(ou => ou.Name)
            .Select(ou => BuildStorageTreeNode(ou, childrenLookup, statsLookup))
            .ToList();
    }

    private StorageTreeDto BuildStorageTreeNode(
        Models.Entities.OrganizationUnit ou,
        ILookup<Guid?, Models.Entities.OrganizationUnit> childrenLookup,
        ILookup<Guid?, Models.Entities.SystemStatistics> statsLookup)
    {
        var stats = statsLookup[ou.Id].FirstOrDefault();

        var children = childrenLookup[ou.Id]
            .OrderBy(child => child.Name)
            .Select(child => BuildStorageTreeNode(child, childrenLookup, statsLookup))
            .ToList();

        return new StorageTreeDto(
            ou.Id,
            ou.Name,
            ou.Code,
            ou.Level,
            stats?.TotalDatasets ?? 0,
            stats?.TotalDocuments ?? 0,
            stats?.TotalStorageUsage ?? 0,
            FormatBytes(stats?.TotalStorageUsage ?? 0),
            children
        );
    }

    public async Task<Models.Entities.SystemStatistics?> GetStatsByOUAsync(Guid ouId)
    {
        return await _context.SystemStatistics
            .FirstOrDefaultAsync(s => s.OUId == ouId);
    }

    public async Task RecalculateStatsAsync()
    {
        _logger.LogInformation("Recalculating system statistics...");

        await UpdateStatsForOUAsync(null);

        var allOUs = await _context.OrganizationUnits.ToListAsync();
        foreach (var ou in allOUs)
        {
            await UpdateStatsForOUAsync(ou.Id);
        }

        _logger.LogInformation("System statistics recalculated successfully");
    }

    public async Task UpdateStatsForOUAsync(Guid? ouId)
    {
        var stats = await _context.SystemStatistics
            .FirstOrDefaultAsync(s => s.OUId == ouId);

        if (stats == null)
        {
            stats = new Models.Entities.SystemStatistics
            {
                OUId = ouId,
                CreatedAt = DateTime.UtcNow
            };
            _context.SystemStatistics.Add(stats);
        }

        if (ouId == null)
        {
            stats.TotalDatasets = await _context.Datasets.CountAsync();
            stats.TotalDocuments = await _context.Documents.CountAsync();
            stats.TotalStorageUsage = 0;
        }
        else
        {
            var ou = await _context.OrganizationUnits.FindAsync(ouId);
            if (ou == null) return;

            var ouIds = await GetOUAndChildrenIdsAsync(ou);
            var datasets = await _context.Datasets
                .Where(d => d.OUId.HasValue && ouIds.Contains(d.OUId.Value))
                .Select(d => d.Id)
                .ToListAsync();

            var datasetItems = await _context.DatasetItems
                .Where(di => datasets.Contains(di.DatasetId) && di.DocumentId.HasValue)
                .Select(di => di.DocumentId!.Value)
                .ToListAsync();

            stats.TotalDatasets = datasets.Count;
            stats.TotalDocuments = datasetItems.Count;
            stats.TotalStorageUsage = 0;
        }

        stats.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    private async Task<List<Guid>> GetOUAndChildrenIdsAsync(Models.Entities.OrganizationUnit parent)
    {
        var result = new List<Guid> { parent.Id };
        var children = await _context.OrganizationUnits
            .Where(o => o.ParentId == parent.Id)
            .Select(o => o.Id)
            .ToListAsync();

        foreach (var childId in children)
        {
            var child = await _context.OrganizationUnits.FindAsync(childId);
            if (child != null)
            {
                var childIds = await GetOUAndChildrenIdsAsync(child);
                result.AddRange(childIds);
            }
        }

        return result;
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
}
