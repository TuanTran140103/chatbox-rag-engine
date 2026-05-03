using MarkdownGenQAs.Application.Dto.Admin.Org;
using MarkdownGenQAs.Application.Dto.User;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class UserInformationService
{
    private readonly IUnitOfWork _uow;
    private readonly UserManager<ApplicationUser> _userManager;

    public UserInformationService(IUnitOfWork uow, UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _userManager = userManager;
    }

    public async Task<ServiceResult<UserProfileDto>> GetMyProfileAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
            return new ServiceResult<UserProfileDto> { IsSuccess = false, ErrorMessage = "User not found" };

        var positions = await GetMyPositionsAsync(userId);
        var managers = await GetMyManagersAsync(userId);

        return new ServiceResult<UserProfileDto>
        {
            IsSuccess = true,
            Data = new UserProfileDto(
                user.Id,
                user.Email ?? string.Empty,
                user.UserName ?? string.Empty,
                user.EmailConfirmed,
                positions,
                managers
            )
        };
    }

    public async Task<List<UserPositionDto>> GetMyPositionsAsync(Guid userId)
    {
        var positions = await _uow.UserPositions.GetByUserAsync(userId);

        return positions.Select(pos => new UserPositionDto(
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
        )).ToList();
    }

    public async Task<List<UserManagerDto>> GetMyManagersAsync(Guid userId)
    {
        var positions = await _uow.UserPositions.GetByUserAsync(userId);

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

    public async Task<List<UserOUSummaryDto>> GetMyOUsAsync(Guid userId)
    {
        var positions = await _uow.UserPositions.GetByUserAsync(userId);

        var ous = positions
            .Where(p => p.OrganizationUnit != null)
            .Select(p => new UserOUSummaryDto(
                p.OUId,
                p.OrganizationUnit?.Name ?? string.Empty
            ))
            .DistinctBy(o => o.OUId)
            .ToList();

        return ous;
    }

    public async Task<List<UserManagerDto>> GetUserManagersAsync(Guid userId)
    {
        return await GetMyManagersAsync(userId);
    }

    public async Task<List<UserPositionDto>> GetUserPositionsAsync(Guid userId)
    {
        return await GetMyPositionsAsync(userId);
    }

    public async Task<List<UserOrgTreeDto>> GetOrgTreeAsync(Guid userId)
    {
        var allOUs = (await _uow.OrganizationUnits.GetAllAsync()).ToList();
        var userCounts = await _uow.UserPositions.GetCountsByOUAsync();

        var positions = await _uow.UserPositions.GetByUserAsync(userId);
        var memberOUIds = positions.Select(p => p.OUId).ToHashSet();
        var managerOUIds = positions
            .Where(p => p.Role == OrganizationRole.Manager)
            .Select(p => p.OUId)
            .ToHashSet();

        var childrenLookup = allOUs.ToLookup(o => o.ParentId);
        var rootOUs = childrenLookup[null].ToList();

        return rootOUs.Select(ou => BuildUserOrgTree(ou, childrenLookup, userCounts, memberOUIds, managerOUIds)).ToList();
    }

    private UserOrgTreeDto BuildUserOrgTree(
        OrganizationUnit ou,
        ILookup<Guid?, OrganizationUnit> childrenLookup,
        Dictionary<Guid, int> userCounts,
        HashSet<Guid> memberOUIds,
        HashSet<Guid> managerOUIds)
    {
        userCounts.TryGetValue(ou.Id, out var count);

        return new UserOrgTreeDto(
            ou.Id,
            ou.Name,
            ou.Code,
            ou.Level,
            count,
            0,
            memberOUIds.Contains(ou.Id),
            managerOUIds.Contains(ou.Id),
            childrenLookup[ou.Id]
                .Select(child => BuildUserOrgTree(child, childrenLookup, userCounts, memberOUIds, managerOUIds))
                .ToList()
        );
    }

    public async Task<List<OUManagerDto>> GetManagersInOUAsync(Guid ouId)
    {
        var positions = await _uow.UserPositions.GetByOUAsync(ouId);

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
}
