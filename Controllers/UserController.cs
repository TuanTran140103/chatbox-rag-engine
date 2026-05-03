using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.Admin.Org;
using MarkdownGenQAs.Application.Dto.User;
using MarkdownGenQAs.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/user")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly UserInformationService _userInfoService;

    public UserController(UserInformationService userInfoService)
    {
        _userInfoService = userInfoService;
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserProfileDto>> GetMyProfile()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _userInfoService.GetMyProfileAsync(userId);
        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });
        return Ok(result.Data);
    }

    [HttpGet("me/positions")]
    public async Task<ActionResult<List<UserPositionDto>>> GetMyPositions()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _userInfoService.GetMyPositionsAsync(userId);
        return Ok(result);
    }

    [HttpGet("me/managers")]
    public async Task<ActionResult<List<UserManagerDto>>> GetMyManagers()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _userInfoService.GetMyManagersAsync(userId);
        return Ok(result);
    }

    [HttpGet("me/ous")]
    public async Task<ActionResult<List<UserOUSummaryDto>>> GetMyOUs()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _userInfoService.GetMyOUsAsync(userId);
        return Ok(result);
    }

    [HttpGet("users/{userId:guid}/managers")]
    public async Task<ActionResult<List<UserManagerDto>>> GetUserManagers(Guid userId)
    {
        var result = await _userInfoService.GetUserManagersAsync(userId);
        return Ok(result);
    }

    [HttpGet("users/{userId:guid}/positions")]
    public async Task<ActionResult<List<UserPositionDto>>> GetUserPositions(Guid userId)
    {
        var result = await _userInfoService.GetUserPositionsAsync(userId);
        return Ok(result);
    }

    [HttpGet("org/tree")]
    public async Task<ActionResult<List<UserOrgTreeDto>>> GetOrgTree()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _userInfoService.GetOrgTreeAsync(userId);
        return Ok(result);
    }

    [HttpGet("org/{ouId:guid}/managers")]
    public async Task<ActionResult<List<OUManagerDto>>> GetManagersInOU(Guid ouId)
    {
        var result = await _userInfoService.GetManagersInOUAsync(ouId);
        return Ok(result);
    }
}
