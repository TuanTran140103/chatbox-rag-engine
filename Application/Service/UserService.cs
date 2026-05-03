using System.Linq.Expressions;
using MarkdownGenQAs.Application.Dto.Admin.User;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Models.Constants;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class UserService
{
    private readonly ApplicationContext _context;

    public UserService(ApplicationContext context)
    {
        _context = context;
    }

    private Expression<Func<ApplicationUser, bool>> NotAdminFilter()
    {
        var adminRoleId = _context.Roles.Where(r => r.Name == RoleNames.Admin).Select(r => r.Id).FirstOrDefault();
        return u => !_context.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == adminRoleId);
    }

    public async Task<SearchUserPagedResponse> SearchUsersAsync(SearchUserRequest request)
    {
        var query = _context.Users.AsNoTracking().Where(NotAdminFilter());

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var keyword = request.Email.Trim().ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail != null && u.NormalizedEmail.Contains(keyword));
        }

        var rawItems = await query
            .OrderBy(u => u.Email)
            .Take(10)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.UserName,
                u.EmailConfirmed
            })
            .ToListAsync();

        var userIds = rawItems.Select(u => u.Id).ToList();
        var ouMap = await BuildOuMapAsync(userIds);

        var items = rawItems.Select(u => new UserListItemDto(
            u.Id,
            u.Email ?? string.Empty,
            u.UserName ?? string.Empty,
            u.EmailConfirmed,
            ouMap.GetValueOrDefault(u.Id, [])
        )).ToList();

        return new SearchUserPagedResponse
        {
            Items = items
        };
    }

    public async Task<UserListResponse> ListUsersAsync(int pageSize, DateTime? cursorCreatedAt, Guid? cursorId)
    {
        var query = _context.Users.AsNoTracking().Where(NotAdminFilter());

        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            query = query.Where(u => u.CreatedAt < cursorCreatedAt.Value ||
                                    (u.CreatedAt == cursorCreatedAt.Value && u.Id.CompareTo(cursorId.Value) < 0));
        }

        var rawItems = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Take(pageSize + 1)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.UserName,
                u.EmailConfirmed,
                u.CreatedAt
            })
            .ToListAsync();

        var hasMore = rawItems.Count > pageSize;
        if (hasMore) rawItems.RemoveAt(rawItems.Count - 1);

        DateTime? nextCursorCreatedAt = null;
        Guid? nextCursorId = null;
        if (rawItems.Count > 0)
        {
            var last = rawItems[^1];
            nextCursorCreatedAt = last.CreatedAt;
            nextCursorId = last.Id;
        }

        var userIds = rawItems.Select(u => u.Id).ToList();
        var ouMap = await BuildOuMapAsync(userIds);

        var items = rawItems.Select(u => new UserListItemDto(
            u.Id,
            u.Email ?? string.Empty,
            u.UserName ?? string.Empty,
            u.EmailConfirmed,
            ouMap.GetValueOrDefault(u.Id, [])
        )).ToList();

        return new UserListResponse
        {
            Items = items,
            NextCursorCreatedAt = nextCursorCreatedAt,
            NextCursorId = nextCursorId,
            HasMore = hasMore
        };
    }

    public async Task<UserListItemDto?> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null) return null;

        var ouNames = await _context.UserPositions
            .AsNoTracking()
            .Where(up => up.UserId == userId && !up.IsDeleted)
            .Include(up => up.OrganizationUnit)
            .Select(up => up.OrganizationUnit.Name)
            .ToListAsync();

        return new UserListItemDto(
            user.Id,
            user.Email ?? string.Empty,
            user.UserName ?? string.Empty,
            user.EmailConfirmed,
            ouNames
        );
    }

    private async Task<Dictionary<Guid, List<string>>> BuildOuMapAsync(List<Guid> userIds)
    {
        if (userIds.Count == 0) return [];

        return await _context.UserPositions
            .AsNoTracking()
            .Where(up => userIds.Contains(up.UserId) && !up.IsDeleted)
            .Include(up => up.OrganizationUnit)
            .GroupBy(up => up.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Names = g.Select(up => up.OrganizationUnit.Name).ToList()
            })
            .ToDictionaryAsync(g => g.UserId, g => g.Names);
    }

    public async Task InvalidateCacheAsync()
    {
        await Task.CompletedTask;
    }
}
