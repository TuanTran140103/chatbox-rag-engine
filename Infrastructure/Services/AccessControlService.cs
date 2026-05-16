using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models.Constants;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Services;

public class AccessControlService : IAccessControlService
{
    private readonly ApplicationContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AccessControlService> _logger;

    public AccessControlService(
        ApplicationContext context,
        UserManager<ApplicationUser> userManager,
        ILogger<AccessControlService> logger)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<bool> IsAdminAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return false;
        return await _userManager.IsInRoleAsync(user, RoleNames.Admin);
    }

    public async Task<bool> IsManagerOfOUAsync(Guid userId, Guid ouId)
    {
        var position = await _context.UserPositions
            .FirstOrDefaultAsync(up => up.UserId == userId && up.OUId == ouId && up.Role == OrganizationRole.Manager);
        return position != null;
    }

    public async Task<bool> IsManagerOrAboveOfOUAsync(Guid userId, Guid ouId)
    {
        var ou = await _context.OrganizationUnits.FindAsync(ouId);
        if (ou == null) return false;

        var position = await _context.UserPositions
            .FirstOrDefaultAsync(up => up.UserId == userId && up.OUId == ouId && up.Role == OrganizationRole.Manager);
        if (position != null) return true;

        var parentOuIds = await GetAncestorOUIdsAsync(ou.Path);
        foreach (var parentOuId in parentOuIds)
        {
            var parentPosition = await _context.UserPositions
                .FirstOrDefaultAsync(up => up.UserId == userId && up.OUId == parentOuId && up.Role == OrganizationRole.Manager);
            if (parentPosition != null) return true;
        }

        return false;
    }

    public async Task<bool> IsInOUAsync(Guid userId, Guid ouId)
    {
        return await _context.UserPositions
            .AnyAsync(up => up.UserId == userId && up.OUId == ouId);
    }

    private async Task<List<Guid>> GetAncestorOUIdsAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return new List<Guid>();

        var ouIds = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        return ouIds;
    }

    public async Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, Dataset dataset)
    {
        if (await IsAdminAsync(userId)) return DatasetPermissions.FullControl;
        if (dataset.OwnerUserId == userId) return DatasetPermissions.FullControl;

        var effectiveMask = DatasetPermissions.None;

        if (dataset.OUId.HasValue)
        {
            if (await IsManagerOfOUAsync(userId, dataset.OUId.Value))
                effectiveMask |= DatasetPermissions.FullControl;
            else if (await IsInOUAsync(userId, dataset.OUId.Value) && dataset.IsPublicToUnit)
                effectiveMask |= DatasetPermissions.Read;
        }

        var userShares = await _context.AccessShares
            .Where(s => s.DatasetId == dataset.Id && s.DatasetItemId == null && s.ShareToUserId == userId)
            .ToListAsync();

        foreach (var share in userShares)
        {
            effectiveMask |= share.PermissionMask;
        }

        var ouShares = await _context.AccessShares
            .Where(s => s.DatasetId == dataset.Id && s.DatasetItemId == null && s.ShareToOUId != null)
            .ToListAsync();

        foreach (var share in ouShares)
        {
            if (share.ShareToOUId.HasValue && await IsInOUAsync(userId, share.ShareToOUId.Value))
            {
                effectiveMask |= share.PermissionMask;
            }
        }

        return effectiveMask;
    }

    public async Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, DatasetItem datasetItem)
    {
        var parentDataset = await _context.Datasets.FindAsync(datasetItem.DatasetId);
        if (parentDataset == null) return DatasetPermissions.None;

        var parentPermissions = await GetEffectivePermissionsAsync(userId, parentDataset);

        if (parentPermissions.HasFlag(DatasetPermissions.FullControl))
            return DatasetPermissions.FullControl;

        var itemShares = await _context.AccessShares
            .Where(s => s.DatasetId == datasetItem.DatasetId && s.DatasetItemId == datasetItem.Id)
            .ToListAsync();

        var effectiveMask = parentPermissions;

        foreach (var share in itemShares)
        {
            if (share.ShareToUserId == userId)
                effectiveMask |= share.PermissionMask;

            if (share.ShareToOUId.HasValue && await IsInOUAsync(userId, share.ShareToOUId.Value))
                effectiveMask |= share.PermissionMask;
        }

        return effectiveMask;
    }

    public async Task<bool> CanViewDatasetAsync(Guid userId, Dataset dataset)
    {
        var perms = await GetEffectivePermissionsAsync(userId, dataset);
        return perms.HasFlag(DatasetPermissions.Read);
    }

    public async Task<bool> CanWriteDatasetAsync(Guid userId, Dataset dataset)
    {
        var perms = await GetEffectivePermissionsAsync(userId, dataset);
        return perms.HasFlag(DatasetPermissions.Update);
    }

    public async Task<bool> CanDeleteDatasetAsync(Guid userId, Dataset dataset)
    {
        var perms = await GetEffectivePermissionsAsync(userId, dataset);
        return perms.HasFlag(DatasetPermissions.Delete);
    }

    public async Task<bool> CanShareDatasetAsync(Guid userId, Dataset dataset)
    {
        var perms = await GetEffectivePermissionsAsync(userId, dataset);
        return perms.HasFlag(DatasetPermissions.Share);
    }

    public async Task<bool> CanViewDatasetItemAsync(Guid userId, DatasetItem datasetItem)
    {
        var perms = await GetEffectivePermissionsAsync(userId, datasetItem);
        return perms.HasFlag(DatasetPermissions.Read);
    }

    public async Task<bool> CanWriteDatasetItemAsync(Guid userId, DatasetItem datasetItem)
    {
        var perms = await GetEffectivePermissionsAsync(userId, datasetItem);
        return perms.HasFlag(DatasetPermissions.Update);
    }

    public async Task<List<Guid>> GetAccessibleDatasetIdsAsync(Guid userId, bool includeDeleted = false)
    {
        var isAdmin = await IsAdminAsync(userId);
        if (isAdmin)
        {
            var query = includeDeleted
                ? _context.Datasets.IgnoreQueryFilters()
                : _context.Datasets.AsQueryable();
            return await query.Select(d => d.Id).ToListAsync();
        }

        var myOUIds = await _context.UserPositions
            .Where(up => up.UserId == userId)
            .Select(up => up.OUId)
            .ToListAsync();

        IQueryable<Dataset> datasetsQuery = includeDeleted
            ? _context.Datasets.IgnoreQueryFilters()
            : _context.Datasets.AsQueryable();

        var ownedByMe = await datasetsQuery
            .Where(d => d.OwnerUserId == userId)
            .Select(d => d.Id)
            .ToListAsync();

        var publicInMyOUs = await datasetsQuery
            .Where(d => d.OUId.HasValue && myOUIds.Contains(d.OUId.Value) && d.IsPublicToUnit)
            .Select(d => d.Id)
            .ToListAsync();

        var managerOfOUs = await _context.UserPositions
            .Where(up => up.UserId == userId && up.Role == OrganizationRole.Manager)
            .Select(up => up.OUId)
            .ToListAsync();

        var managedDatasets = await datasetsQuery
            .Where(d => d.OUId.HasValue && managerOfOUs.Contains(d.OUId.Value))
            .Select(d => d.Id)
            .ToListAsync();

        var sharedToMe = await _context.AccessShares
            .Where(s => s.ShareToUserId == userId && s.DatasetItemId == null)
            .Select(s => s.DatasetId)
            .ToListAsync();

        var sharedToMyOUs = await _context.AccessShares
            .Where(s => s.ShareToOUId.HasValue && myOUIds.Contains(s.ShareToOUId.Value) && s.DatasetItemId == null)
            .Select(s => s.DatasetId)
            .ToListAsync();

        return ownedByMe.Union(publicInMyOUs).Union(managedDatasets)
            .Union(sharedToMe).Union(sharedToMyOUs)
            .Distinct()
            .ToList();
    }

    public async Task<List<Guid>> GetAccessibleDocumentIdsAsync(Guid userId)
    {
        var accessibleDatasetIds = await GetAccessibleDatasetIdsAsync(userId);

        var accessibleDocumentIds = await _context.DatasetItems
            .Where(di => accessibleDatasetIds.Contains(di.DatasetId) && di.DocumentId.HasValue)
            .Select(di => di.DocumentId!.Value)
            .ToListAsync();

        return accessibleDocumentIds;
    }
}
