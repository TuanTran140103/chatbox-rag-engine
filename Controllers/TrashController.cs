using MarkdownGenQAs.Application.Dto.Admin.Trash;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/admin/trash")]
[Authorize]
public class TrashController : ControllerBase
{
    private readonly ITrashService _trashService;

    public TrashController(ITrashService trashService)
    {
        _trashService = trashService;
    }

    [HttpGet]
    public async Task<ActionResult<List<TrashItemDto>>> GetTrash()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _trashService.GetTrashItemsAsync(userId);
        return Ok(result);
    }

    [HttpPost("restore/{type}/{id:guid}")]
    public async Task<IActionResult> RestoreItem(string type, Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var trashType = ParseType(type);
        var result = await _trashService.RestoreItemAsync(trashType, id, userId);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });
        return Ok();
    }

    [HttpDelete("empty")]
    public async Task<IActionResult> EmptyTrash()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _trashService.EmptyTrashAsync(userId);
        return Ok();
    }

    [HttpDelete("{type}/{id:guid}")]
    public async Task<IActionResult> PermanentDelete(string type, Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var trashType = ParseType(type);
        var result = await _trashService.PermanentDeleteItemAsync(trashType, id, userId);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });
        return Ok();
    }

    private static TrashItemType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "organization-unit" => TrashItemType.OrganizationUnit,
        "dataset" => TrashItemType.Dataset,
        "folder" => TrashItemType.Folder,
        "document" => TrashItemType.Document,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown trash item type")
    };
}
