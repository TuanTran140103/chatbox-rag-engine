using MarkdownGenQAs.Application.Dto.Admin.Stats;
using MarkdownGenQAs.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class AdminStatsService
{
    private readonly ApplicationContext _context;
    private readonly ILogger<AdminStatsService> _logger;

    public AdminStatsService(
        ApplicationContext context,
        ILogger<AdminStatsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<SystemStatsSummaryDto> GetSummaryAsync()
    {
        var allStats = await _context.SystemStatistics.ToListAsync();

        return new SystemStatsSummaryDto(
            allStats.Sum(s => s.TotalDatasets),
            allStats.Sum(s => s.TotalDocuments),
            FormatBytes(allStats.Sum(s => s.TotalStorageUsage)),
            await _context.OrganizationUnits.CountAsync(),
            await _context.Users.CountAsync()
        );
    }

    public async Task<List<StorageChartDto>> GetStorageChartAsync()
    {
        var ouStats = await _context.SystemStatistics
            .Include(s => s.OU)
            .ToListAsync();

        var totalStorage = ouStats.Sum(s => s.TotalStorageUsage);

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
                totalStorage > 0 ? Math.Round((double)stat.TotalStorageUsage / totalStorage * 100, 2) : 0
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
