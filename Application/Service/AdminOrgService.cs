using MarkdownGenQAs.Application.Dto.Admin.Org;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Service;

public class AdminOrgService
{
    private readonly IUnitOfWork _uow;
    private readonly IAccessControlService _accessControl;
    private readonly ILogger<AdminOrgService> _logger;

    public AdminOrgService(IUnitOfWork uow, IAccessControlService accessControl, ILogger<AdminOrgService> logger)
    {
        _uow = uow;
        _accessControl = accessControl;
        _logger = logger;
    }

    public async Task<List<OrgTreeDto>> GetOrgTreeAsync()
    {
        var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();

        var userCounts = await _uow.UserPositions.GetCountsByOUAsync();
        var datasetCounts = await _uow.Datasets.GetCountsByOUAsync();

        var childrenLookup = allOUs.ToLookup(o => o.ParentId);
        var rootOUs = childrenLookup[null].ToList();

        return rootOUs.Select(ou => BuildOrgTree(ou, childrenLookup, userCounts, datasetCounts)).ToList();
    }

    private OrgTreeDto BuildOrgTree(
        Models.Entities.OrganizationUnit ou,
        ILookup<Guid?, Models.Entities.OrganizationUnit> childrenLookup,
        Dictionary<Guid, int> userCounts,
        Dictionary<Guid, int> datasetCounts)
    {
        var children = childrenLookup[ou.Id];
        userCounts.TryGetValue(ou.Id, out var ouUserCount);
        datasetCounts.TryGetValue(ou.Id, out var ouDatasetCount);

        return new OrgTreeDto(
            ou.Id,
            ou.Name,
            ou.Code,
            ou.Level,
            ouUserCount,
            ouDatasetCount,
            children.Select(child => BuildOrgTree(child, childrenLookup, userCounts, datasetCounts)).ToList()
        );
    }

    public async Task<OrgTreeDto?> GetOrgByIdAsync(Guid ouId)
    {
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);
        if (ou == null) return null;

        var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();
        var userCounts = await _uow.UserPositions.GetCountsByOUAsync();
        var datasetCounts = await _uow.Datasets.GetCountsByOUAsync();

        var childrenLookup = allOUs.ToLookup(o => o.ParentId);

        return BuildOrgTree(ou, childrenLookup, userCounts, datasetCounts);
    }

    public async Task<List<OrgUserDto>> GetUsersInOUAsync(Guid ouId)
    {
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);
        if (ou == null) return new List<OrgUserDto>();

        var positions = await _uow.UserPositions
            .GetByOUAsync(ouId);

        var result = new List<OrgUserDto>();
        foreach (var pos in positions)
        {
            result.Add(new OrgUserDto(
                pos.UserId,
                pos.User?.Email ?? string.Empty,
                pos.User?.UserName ?? string.Empty,
                ouId,
                ou.Name,
                pos.Role.ToString(),
                pos.IsPrimary,
                pos.CreatedAt,
                pos.Manager?.UserName
            ));
        }

        return result;
    }

    public async Task<List<OrgUserDto>> GetUsersInOUAndChildrenAsync(Guid ouId)
    {
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);
        if (ou == null) return new List<OrgUserDto>();

        var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();
        var childrenLookup = allOUs.ToLookup(o => o.ParentId);

        var ouIds = new HashSet<Guid>();
        TraverseOUIds(ou.Id, childrenLookup, ouIds);

        var positions = await _uow.UserPositions
            .GetByOUIdsAsync(ouIds);

        var result = new List<OrgUserDto>();
        foreach (var pos in positions)
        {
            var posOU = allOUs.FirstOrDefault(o => o.Id == pos.OUId);
            if (posOU == null) continue;

            result.Add(new OrgUserDto(
                pos.UserId,
                pos.User?.Email ?? string.Empty,
                pos.User?.UserName ?? string.Empty,
                pos.OUId,
                posOU.Name,
                pos.Role.ToString(),
                pos.IsPrimary,
                pos.CreatedAt,
                pos.Manager?.UserName
            ));
        }

        return result;
    }

    private void TraverseOUIds(Guid parentId, ILookup<Guid?, Models.Entities.OrganizationUnit> childrenLookup, HashSet<Guid> result)
    {
        result.Add(parentId);
        foreach (var child in childrenLookup[parentId])
        {
            TraverseOUIds(child.Id, childrenLookup, result);
        }
    }

    public async Task<List<UserPositionDto>> GetUserPositionsAsync(Guid userId)
    {
        var positions = await _uow.UserPositions
            .GetByUserAsync(userId);

        var result = new List<UserPositionDto>();
        foreach (var pos in positions)
        {
            result.Add(new UserPositionDto(
                pos.Id,
                pos.UserId,
                pos.User?.UserName ?? string.Empty,
                pos.User?.Email ?? string.Empty,
                pos.OUId,
                pos.OrganizationUnit?.Name ?? string.Empty,
                pos.Role,
                pos.IsPrimary,
                pos.CreatedAt,
                pos.ManagerId,
                pos.Manager?.UserName,
                pos.Manager?.Email
            ));
        }

        return result;
    }

    public async Task<List<UserManagerDto>> GetUserManagersAsync(Guid userId)
    {
        var positions = await _uow.UserPositions
            .GetByUserAsync(userId);

        var managers = positions
            .Where(p => p.ManagerId.HasValue && p.Manager != null)
            .Select(p => new UserManagerDto(
                p.ManagerId!.Value,
                p.Manager!.UserName ?? string.Empty,
                p.Manager.Email ?? string.Empty,
                p.OUId,
                p.OrganizationUnit?.Name ?? string.Empty
            ))
            .DistinctBy(m => m.ManagerId)
            .ToList();

        return managers;
    }

    public async Task<List<OUManagerDto>> GetManagersInOUAsync(Guid ouId)
    {
        var positions = await _uow.UserPositions
            .GetByOUAsync(ouId);

        var managers = positions
            .Where(p => p.Role == OrganizationRole.Manager && p.User != null)
            .Select(p => new OUManagerDto(
                p.UserId,
                p.User?.UserName ?? string.Empty,
                p.User?.Email ?? string.Empty
            ))
            .DistinctBy(m => m.UserId)
            .ToList();

        return managers;
    }

    public async Task<ServiceResult> AssignUserToOUAsync(Guid userId, Guid ouId, OrganizationRole role, bool isPrimary, Guid? managerId = null)
    {
        if (await _accessControl.IsAdminAsync(userId))
        {
            var msg = "Cannot assign admin user to an OU";
            _logger.LogWarning("{Msg}: {UserId}", msg, userId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = msg };
        }

        var existing = await _uow.UserPositions
            .FindAsync(up => up.UserId == userId && up.OUId == ouId);

        if (existing.Any())
        {
            var msg = "User already assigned to this OU";
            _logger.LogWarning("{Msg}: {UserId} - {OUId}", msg, userId, ouId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = msg };
        }

        if (role == OrganizationRole.Staff && !managerId.HasValue)
        {
            var msg = "Staff must have a manager assigned";
            _logger.LogWarning("{Msg}: {UserId} - {OUId}", msg, userId, ouId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = msg };
        }

        if (managerId.HasValue)
        {
            var isManager = await _uow.UserPositions
                .FindAsync(up => up.UserId == managerId.Value && up.OUId == ouId && up.Role == OrganizationRole.Manager);
            if (!isManager.Any())
            {
                var msg = "Specified manager is not a Manager of this OU";
                _logger.LogWarning("{Msg}: {ManagerId} - {OUId}", msg, managerId, ouId);
                return new ServiceResult { IsSuccess = false, ErrorMessage = msg };
            }
        }

        if (isPrimary)
        {
            var otherPrimaries = await _uow.UserPositions
                .FindAsync(up => up.UserId == userId && up.IsPrimary);

            foreach (var p in otherPrimaries)
            {
                p.IsPrimary = false;
                _uow.UserPositions.Update(p);
            }

            var datasets = await _uow.Datasets
                .FindAsync(d => d.OwnerUserId == userId);

            foreach (var ds in datasets)
            {
                ds.OUId = ouId;
                _uow.Datasets.Update(ds);
            }
        }

        var position = new UserPosition
        {
            UserId = userId,
            OUId = ouId,
            Role = role,
            IsPrimary = isPrimary,
            ManagerId = managerId
        };

        await _uow.UserPositions.AddAsync(position);
        await _uow.SaveChangesAsync();
        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult> RemoveUserFromOUAsync(Guid userId, Guid ouId)
    {
        var positions = await _uow.UserPositions
            .FindAsync(up => up.UserId == userId && up.OUId == ouId);

        var position = positions.FirstOrDefault();
        if (position == null)
        {
            var msg = "User position not found";
            _logger.LogWarning("{Msg}: {UserId} - {OUId}", msg, userId, ouId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = msg };
        }

        _uow.UserPositions.Delete(position);
        await _uow.SaveChangesAsync();
        return new ServiceResult { IsSuccess = true };
    }

    public async Task<ServiceResult<OrgTreeDto>> CreateOrgAsync(CreateOrgRequestDto request)
    {
        var ou = new OrganizationUnit
        {
            Name = request.Name,
            Code = request.Code,
            ParentId = request.ParentId,
        };

        if (request.ParentId.HasValue)
        {
            var parent = await _uow.OrganizationUnits.GetByIdAsync(request.ParentId.Value);
            if (parent == null)
            {
                var msg = $"Parent OU {request.ParentId} not found";
                _logger.LogWarning("{Msg}", msg);
                return new ServiceResult<OrgTreeDto> { IsSuccess = false, ErrorMessage = msg };
            }

            ou.Level = parent.Level + 1;
            ou.Path = $"{parent.Path}/{ou.Id}";
        }
        else
        {
            ou.Level = 0;
            ou.Path = ou.Id.ToString();
        }

        await _uow.OrganizationUnits.AddAsync(ou);
        await _uow.SaveChangesAsync();

        return new ServiceResult<OrgTreeDto> { IsSuccess = true, Data = new OrgTreeDto(ou.Id, ou.Name, ou.Code, ou.Level, 0, 0, []) };
    }

    public async Task<OrgTreeDto?> UpdateOrgAsync(Guid ouId, UpdateOrgRequestDto request)
    {
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);
        if (ou == null) return null;

        ou.Name = request.Name;
        ou.Code = request.Code;

        await _uow.SaveChangesAsync();

        var userCount = (await _uow.UserPositions.FindAsync(up => up.OUId == ouId)).Count();
        var datasetCounts = await _uow.Datasets.GetCountsByOUAsync();
        datasetCounts.TryGetValue(ouId, out var ouDatasetCount);

        return new OrgTreeDto(ou.Id, ou.Name, ou.Code, ou.Level, userCount, ouDatasetCount, []);
    }

    public async Task<ServiceResult<OrgTreeDto>> MoveOrgAsync(Guid ouId, MoveOrgRequestDto request)
    {
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);
        if (ou == null)
        {
            var msg = "OU not found";
            _logger.LogWarning("{Msg}: {OUId}", msg, ouId);
            return new ServiceResult<OrgTreeDto> { IsSuccess = false, ErrorMessage = msg };
        }

        if (request.ParentId == ou.ParentId)
        {
            var tree = await BuildOrgTreeResponseAsync(ouId);
            return new ServiceResult<OrgTreeDto> { IsSuccess = true, Data = tree };
        }

        if (request.ParentId.HasValue)
        {
            var newParent = await _uow.OrganizationUnits.GetByIdAsync(request.ParentId.Value);
            if (newParent == null)
            {
                var msg = $"Parent OU {request.ParentId} not found";
                _logger.LogWarning("{Msg}", msg);
                return new ServiceResult<OrgTreeDto> { IsSuccess = false, ErrorMessage = msg };
            }

            if (request.ParentId.Value == ouId)
            {
                return new ServiceResult<OrgTreeDto> { IsSuccess = false, ErrorMessage = "Cannot set self as parent" };
            }

            var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();
            var childrenLookup = allOUs.ToLookup(o => o.ParentId);
            var descendantIds = new HashSet<Guid>();
            TraverseOUIds(ouId, childrenLookup, descendantIds);

            if (descendantIds.Contains(request.ParentId.Value))
            {
                return new ServiceResult<OrgTreeDto> { IsSuccess = false, ErrorMessage = "Cannot set descendant as parent (circular dependency)" };
            }

            ou.ParentId = request.ParentId.Value;
            ou.Level = newParent.Level + 1;
            ou.Path = $"{newParent.Path}/{ou.Id}";
            _uow.OrganizationUnits.Update(ou);

            RecalculatePathForDescendants(ou, allOUs, childrenLookup);
        }
        else
        {
            var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();
            var childrenLookup = allOUs.ToLookup(o => o.ParentId);

            ou.ParentId = null;
            _uow.OrganizationUnits.Update(ou);
            RecalculatePathForSubtree(ou, allOUs, childrenLookup);
        }

        await _uow.SaveChangesAsync();
        var result = await BuildOrgTreeResponseAsync(ouId);
        return new ServiceResult<OrgTreeDto> { IsSuccess = true, Data = result };
    }

    private void RecalculatePathForDescendants(
        OrganizationUnit node,
        List<OrganizationUnit> allOUs,
        ILookup<Guid?, OrganizationUnit> childrenLookup)
    {
        foreach (var child in childrenLookup[node.Id])
        {
            RecalculatePathRecursive(child, node.Path, node.Level + 1, childrenLookup);
        }
    }

    private async Task<OrgTreeDto> BuildOrgTreeResponseAsync(Guid ouId)
    {
        var userCount = (await _uow.UserPositions.FindAsync(up => up.OUId == ouId)).Count();
        var datasetCounts = await _uow.Datasets.GetCountsByOUAsync();
        datasetCounts.TryGetValue(ouId, out var ouDatasetCount);
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);

        return new OrgTreeDto(ou!.Id, ou.Name, ou.Code, ou.Level, userCount, ouDatasetCount, []);
    }

    public async Task<ServiceResult> DeleteOrgAsync(Guid ouId)
    {
        var ou = await _uow.OrganizationUnits.GetByIdAsync(ouId);
        if (ou == null)
        {
            var msg = "OU not found";
            _logger.LogWarning("{Msg}: {OUId}", msg, ouId);
            return new ServiceResult { IsSuccess = false, ErrorMessage = msg };
        }

        var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();
        var childrenLookup = allOUs.ToLookup(o => o.ParentId);
        var directChildren = childrenLookup[ouId].ToList();

        foreach (var child in directChildren)
        {
            child.ParentId = null;
            _uow.OrganizationUnits.Update(child);
            RecalculatePathForSubtree(child, allOUs, childrenLookup);
        }

        var datasets = await _uow.Datasets
            .FindAsync(d => d.OUId.HasValue && d.OUId.Value == ouId);
        foreach (var ds in datasets)
        {
            ds.OUId = null;
            ds.IsPublicToUnit = false;
            _uow.Datasets.Update(ds);
        }

        var shares = await _uow.AccessShares
            .FindAsync(s => s.ShareToOUId.HasValue && s.ShareToOUId.Value == ouId);
        foreach (var share in shares)
        {
            share.ShareToOUId = null;
            _uow.AccessShares.Update(share);
        }

        _uow.OrganizationUnits.Delete(ou);

        await _uow.SaveChangesAsync();
        return new ServiceResult { IsSuccess = true };
    }

    private void RecalculatePathForSubtree(
        OrganizationUnit node,
        List<OrganizationUnit> allOUs,
        ILookup<Guid?, OrganizationUnit> childrenLookup)
    {
        node.Path = node.Id.ToString();
        node.Level = 0;
        _uow.OrganizationUnits.Update(node);

        var subtreeChildren = childrenLookup[node.Id];
        foreach (var child in subtreeChildren)
        {
            RecalculatePathRecursive(child, node.Path, node.Level + 1, childrenLookup);
        }
    }

    private void RecalculatePathRecursive(
        OrganizationUnit node,
        string parentPath,
        int parentLevel,
        ILookup<Guid?, OrganizationUnit> childrenLookup)
    {
        node.Path = $"{parentPath}/{node.Id}";
        node.Level = parentLevel;
        _uow.OrganizationUnits.Update(node);

        foreach (var child in childrenLookup[node.Id])
        {
            RecalculatePathRecursive(child, node.Path, node.Level + 1, childrenLookup);
        }
    }
}
