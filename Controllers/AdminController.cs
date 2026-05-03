using MarkdownGenQAs.Application.Dto.Admin.Dataset;
using MarkdownGenQAs.Application.Dto.Admin.Org;
using MarkdownGenQAs.Application.Dto.Admin.Stats;
using MarkdownGenQAs.Application.Dto.Admin.User;
using MarkdownGenQAs.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly AdminOrgService _adminOrgService;
    private readonly AdminStatsService _adminStatsService;
    private readonly AdminDatasetService _adminDatasetService;
    private readonly UserService _userService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        AdminOrgService adminOrgService,
        AdminStatsService adminStatsService,
        AdminDatasetService adminDatasetService,
        UserService userService,
        ILogger<AdminController> logger)
    {
        _adminOrgService = adminOrgService;
        _adminStatsService = adminStatsService;
        _adminDatasetService = adminDatasetService;
        _userService = userService;
        _logger = logger;
    }

    #region Organization & Personnel

    [HttpPost("org")]
    public async Task<ActionResult<OrgTreeDto>> CreateOrg([FromBody] CreateOrgRequestDto request)
    {
        var result = await _adminOrgService.CreateOrgAsync(request);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetOrgById), new { ouId = result.Data!.Id }, result.Data);
    }

    [HttpPut("org/{ouId:guid}")]
    public async Task<ActionResult<OrgTreeDto>> UpdateOrg(Guid ouId, [FromBody] UpdateOrgRequestDto request)
    {
        var result = await _adminOrgService.UpdateOrgAsync(ouId, request);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("org/{ouId:guid}/move")]
    public async Task<ActionResult<OrgTreeDto>> MoveOrg(Guid ouId, [FromBody] MoveOrgRequestDto request)
    {
        var result = await _adminOrgService.MoveOrgAsync(ouId, request);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });
        if (result.Data == null) return NotFound();
        return Ok(result.Data);
    }

    [HttpDelete("org/{ouId:guid}")]
    public async Task<IActionResult> DeleteOrg(Guid ouId)
    {
        var result = await _adminOrgService.DeleteOrgAsync(ouId);
        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });
        return Ok();
    }

    [HttpGet("org/tree")]
    public async Task<ActionResult<List<OrgTreeDto>>> GetOrgTree()
    {
        var result = await _adminOrgService.GetOrgTreeAsync();
        return Ok(result);
    }

    [HttpGet("org/{ouId:guid}")]
    public async Task<ActionResult<OrgTreeDto>> GetOrgById(Guid ouId)
    {
        var result = await _adminOrgService.GetOrgByIdAsync(ouId);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpGet("org/{ouId:guid}/users")]
    public async Task<ActionResult<List<OrgUserDto>>> GetUsersInOU(Guid ouId)
    {
        var result = await _adminOrgService.GetUsersInOUAsync(ouId);
        return Ok(result);
    }

    [HttpGet("org/{ouId:guid}/users/tree")]
    public async Task<ActionResult<List<OrgUserDto>>> GetUsersInOUAndChildren(Guid ouId)
    {
        var result = await _adminOrgService.GetUsersInOUAndChildrenAsync(ouId);
        return Ok(result);
    }

    [HttpPost("users/{userId:guid}/assign")]
    public async Task<IActionResult> AssignUserToOU(
        Guid userId,
        [FromBody] AssignUserToOURequest request)
    {
        var result = await _adminOrgService.AssignUserToOUAsync(
            userId, request.OUId, request.Role, request.IsPrimary, request.ManagerId);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });
        return Ok();
    }

    [HttpDelete("users/{userId:guid}/ou/{ouId:guid}")]
    public async Task<IActionResult> RemoveUserFromOU(Guid userId, Guid ouId)
    {
        var result = await _adminOrgService.RemoveUserFromOUAsync(userId, ouId);
        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });
        return Ok();
    }

    #endregion

    #region Dashboard Statistics

    [HttpGet("stats/summary")]
    public async Task<ActionResult<SystemStatsSummaryDto>> GetSummary()
    {
        var result = await _adminStatsService.GetSummaryAsync();
        return Ok(result);
    }

    [HttpGet("stats/storage-chart")]
    public async Task<ActionResult<List<StorageChartDto>>> GetStorageChart()
    {
        var result = await _adminStatsService.GetStorageChartAsync();
        return Ok(result);
    }

    [HttpGet("stats/storage-tree")]
    public async Task<ActionResult<List<StorageTreeDto>>> GetStorageTree()
    {
        var result = await _adminStatsService.GetStorageTreeAsync();
        return Ok(result);
    }

    [HttpGet("stats/ou/{ouId:guid}")]
    public async Task<ActionResult> GetStatsByOU(Guid ouId)
    {
        var result = await _adminStatsService.GetStatsByOUAsync(ouId);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("stats/recalculate")]
    public async Task<IActionResult> RecalculateStats()
    {
        await _adminStatsService.RecalculateStatsAsync();
        return Ok(new { message = "Statistics recalculated successfully" });
    }

    #endregion

    #region Dataset Management

    [HttpGet("datasets")]
    public async Task<ActionResult<DatasetPagedResponse>> GetAllDatasets(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var datasets = await _adminDatasetService.GetAllDatasetsAsync(page, pageSize);
        var total = await _adminDatasetService.GetTotalDatasetsCountAsync();

        return Ok(new DatasetPagedResponse
        {
            Items = datasets,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        });
    }

    [HttpGet("datasets/{datasetId:guid}/items")]
    public async Task<ActionResult<List<DatasetItemDto>>> GetDatasetItems(
        Guid datasetId,
        [FromQuery] Guid? parentId = null)
    {
        var result = await _adminDatasetService.GetDatasetItemsAsync(datasetId, parentId);
        return Ok(result);
    }

    [HttpPost("datasets/{datasetId:guid}/transfer-owner")]
    public async Task<IActionResult> TransferOwnership(
        Guid datasetId,
        [FromBody] TransferOwnerRequest request)
    {
        var result = await _adminDatasetService.TransferOwnershipAsync(datasetId, request.NewOwnerUserId);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });
        return Ok();
    }

    [HttpGet("datasets/{datasetId:guid}/shares")]
    public async Task<ActionResult<List<AccessShareDto>>> GetDatasetShares(Guid datasetId)
    {
        var result = await _adminDatasetService.GetDatasetSharesAsync(datasetId);
        return Ok(result);
    }

    #endregion

    #region User Search

    [HttpGet("users")]
    public async Task<ActionResult<UserListResponse>> ListUsers(
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTime? cursorCreatedAt = null,
        [FromQuery] Guid? cursorId = null)
    {
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 50) pageSize = 50;

        var result = await _userService.ListUsersAsync(pageSize, cursorCreatedAt, cursorId);
        return Ok(result);
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<UserListItemDto>> GetUserById(Guid userId)
    {
        var result = await _userService.GetUserByIdAsync(userId);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpGet("users/search")]
    public async Task<ActionResult<SearchUserPagedResponse>> SearchUsers(
        [FromQuery] SearchUserRequest request)
    {
        var result = await _userService.SearchUsersAsync(request);
        return Ok(result);
    }

    #endregion
}

#region Request DTOs

public class AssignUserToOURequest
{
    public Guid OUId { get; set; }
    public OrganizationRole Role { get; set; } = OrganizationRole.Staff;
    public bool IsPrimary { get; set; } = true;
    public Guid? ManagerId { get; set; }
}

public class TransferOwnerRequest
{
    public Guid NewOwnerUserId { get; set; }
}

public class DatasetPagedResponse
{
    public List<DatasetOverviewDto> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

#endregion