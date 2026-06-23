using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Utils;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Services;

public class AccessControlService : IAccessControlService
{
    private readonly ApplicationContext _context;
    private readonly ILogger<AccessControlService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AccessControlService(
        ApplicationContext context,
        ILogger<AccessControlService> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public Task<bool> IsAdminAsync(Guid userId)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user == null) return Task.FromResult(false);
        return Task.FromResult(user.IsInRole("Admin"));
    }

    public async Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, Dataset dataset)
    {
        if (dataset.OwnerUserId == userId) return DatasetPermissions.FullControl;

        var effectiveMask = DatasetPermissions.None;

        // Manager của Department mà dataset thuộc về → Read
        if (dataset.DepartmentId.HasValue)
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user != null)
            {
                var role = user.GetDepartmentRole(dataset.DepartmentId.Value);
                if (role == DepartmentRole.Manager)
                    effectiveMask |= DatasetPermissions.Read;
            }
        }

        // Direct shares to user
        var userShares = await _context.AccessShares
            .Where(s => s.DatasetId == dataset.Id && s.DatasetItemId == null && s.ShareToUserId == userId)
            .ToListAsync();

        foreach (var share in userShares)
        {
            effectiveMask |= share.PermissionMask;
        }

        // Shares to user's departments
        var currentUserDepts = _httpContextAccessor.HttpContext?.User.GetDepartmentIds() ?? [];
        if (currentUserDepts.Count != 0)
        {
            var departmentShares = await _context.AccessShares
                .Where(s => s.DatasetId == dataset.Id && s.DatasetItemId == null
                    && s.ShareToDepartmentId != null && currentUserDepts.Contains(s.ShareToDepartmentId.Value))
                .ToListAsync();

            foreach (var share in departmentShares)
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

    public async Task<bool> CanViewDocumentAsync(Guid userId, Guid documentId)
    {
        var datasetId = await _context.DatasetItems
            .Where(di => di.DocumentId == documentId)
            .Select(di => di.DatasetId)
            .FirstOrDefaultAsync();

        if (datasetId == Guid.Empty) return false;

        var dataset = await _context.Datasets.FindAsync(datasetId);
        if (dataset == null) return false;

        return await CanViewDatasetAsync(userId, dataset);
    }

    public async Task<bool> CanWriteDocumentAsync(Guid userId, Guid documentId)
    {
        var datasetId = await _context.DatasetItems
            .Where(di => di.DocumentId == documentId)
            .Select(di => di.DatasetId)
            .FirstOrDefaultAsync();

        if (datasetId == Guid.Empty) return false;

        var dataset = await _context.Datasets.FindAsync(datasetId);
        if (dataset == null) return false;

        return await CanWriteDatasetAsync(userId, dataset);
    }

    public async Task<List<Guid>> GetAccessibleDatasetIdsAsync(Guid userId, bool includeDeleted = false)
    {
        IQueryable<Dataset> datasetsQuery = includeDeleted
            ? _context.Datasets.IgnoreQueryFilters()
            : _context.Datasets.AsQueryable();

        var userDeptIds = _httpContextAccessor.HttpContext?.User.GetDepartmentIds() ?? [];

        // Owned datasets
        var ownedByMe = await datasetsQuery
            .Where(d => d.OwnerUserId == userId)
            .Select(d => d.Id)
            .ToListAsync();

        // Shared to user directly
        var sharedToMe = await _context.AccessShares
            .Where(s => s.ShareToUserId == userId && s.DatasetItemId == null)
            .Select(s => s.DatasetId)
            .ToListAsync();

        // Datasets where user is Manager of the dataset's department
        var managedDeptIds = new List<Guid>();
        if (userDeptIds.Count != 0)
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user != null)
            {
                managedDeptIds = userDeptIds
                    .Where(did => user.GetDepartmentRole(did) == DepartmentRole.Manager)
                    .ToList();
            }
        }

        var managedDatasets = managedDeptIds.Count != 0
            ? await datasetsQuery
                .Where(d => d.DepartmentId != null && managedDeptIds.Contains(d.DepartmentId.Value))
                .Select(d => d.Id)
                .ToListAsync()
            : [];

        // Shared to user's departments
        var sharedToDepts = userDeptIds.Count != 0
            ? await _context.AccessShares
                .Where(s => s.ShareToDepartmentId != null && userDeptIds.Contains(s.ShareToDepartmentId.Value) && s.DatasetItemId == null)
                .Select(s => s.DatasetId)
                .ToListAsync()
            : [];

        return ownedByMe
            .Union(sharedToMe)
            .Union(managedDatasets)
            .Union(sharedToDepts)
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