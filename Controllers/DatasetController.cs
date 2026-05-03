using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.User.Dataset;
using MarkdownGenQAs.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/user/me/datasets")]
[Authorize]
public class DatasetController : ControllerBase
{
    private readonly DatasetService _datasetService;

    public DatasetController(DatasetService datasetService)
    {
        _datasetService = datasetService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<DatasetListDto>>> GetMyDatasets(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.GetMyDatasetsAsync(userId, page, pageSize);

        if (!result.IsSuccess)
            return StatusCode(500, new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DatasetDetailDto>> GetMyDatasetById(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.GetDatasetByIdAsync(userId, id);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<ActionResult<DatasetDetailDto>> CreateMyDataset([FromBody] CreateDatasetRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.CreateDatasetAsync(userId, dto);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetMyDatasetById), new { id = result.Data!.Id }, result.Data);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DatasetDetailDto>> UpdateMyDataset(Guid id, [FromBody] UpdateDatasetRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.UpdateDatasetAsync(userId, id, dto);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteMyDataset(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.DeleteDatasetAsync(userId, id);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return NoContent();
    }

    [HttpGet("{id:guid}/items")]
    public async Task<ActionResult<DatasetItemsResponseDto>> GetMyDatasetItems(
        Guid id,
        [FromQuery] Guid? parentId = null)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.GetDatasetItemsAsync(userId, id, parentId);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpPost("{id:guid}/create-item")]
    [RequestSizeLimit(1024 * 1024 * 100)]
    public async Task<ActionResult<CreateItemResponseDto>> CreateItem(
        Guid id,
        [FromForm] int type,
        [FromForm] string? name,
        [FromForm] Guid? parentId,
        [FromForm] IFormFile? file)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        Stream? fileStream = null;
        string? fileName = null;
        string? contentType = null;

        if (file != null)
        {
            fileStream = file.OpenReadStream();
            fileName = file.FileName;
            contentType = file.ContentType;
        }

        var result = await _datasetService.CreateItemAsync(userId, id, type, name, parentId, fileStream, fileName, contentType);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetMyDatasetItems), new { id }, result.Data);
    }

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> DeleteItem(Guid id, Guid itemId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.DeleteItemAsync(userId, id, itemId);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return NoContent();
    }
}
