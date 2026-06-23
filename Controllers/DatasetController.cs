using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.Documents;
using MarkdownGenQAs.Application.Dto.User.Dataset;
using MarkdownGenQAs.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/datasets")]
[Authorize(Roles = "User")]
public class DatasetController : ControllerBase
{
    private readonly DatasetService _datasetService;

    public DatasetController(DatasetService datasetService)
    {
        _datasetService = datasetService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<DatasetDto>>> GetMyDatasets(
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
    public async Task<ActionResult<DatasetDto>> GetMyDatasetById(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.GetDatasetByIdAsync(userId, id);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<ActionResult<DatasetDto>> CreateMyDataset([FromBody] CreateDatasetRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.CreateDatasetAsync(userId, dto);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetMyDatasetById), new { id = result.Data!.Id }, result.Data);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DatasetDto>> UpdateMyDataset(Guid id, [FromBody] UpdateDatasetRequestDto dto)
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

    [HttpPost("{id:guid}/create-folder")]
    public async Task<ActionResult<CreateItemResponseDto>> CreateFolder(
        Guid id,
        [FromBody] CreateFolderRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.CreateFolderAsync(userId, id, dto.Name, dto.ParentId);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetMyDatasetItems), new { id }, result.Data);
    }

    [HttpPost("{id:guid}/init-upload")]
    public async Task<ActionResult<InitUploadResponseDto>> InitUpload(
        Guid id,
        [FromBody] InitUploadRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.InitUploadAsync(userId, id, dto.FileName, dto.FileSize, dto.ParentId, dto.ContentType);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpPost("{id:guid}/init-upload-bulk")]
    public async Task<ActionResult<InitUploadBulkResponseDto>> InitUploadBulk(
        Guid id,
        [FromBody] InitUploadBulkRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.InitUploadBulkAsync(userId, id, dto.Files);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpPost("{id:guid}/complete-upload/{documentId:guid}")]
    public async Task<ActionResult<CreateItemResponseDto>> CompleteUpload(
        Guid id, Guid documentId,
        [FromBody] CompleteUploadRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.CompleteUploadAsync(userId, id, documentId, dto.ParentId);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetMyDatasetItems), new { id }, result.Data);
    }

    [HttpPost("{id:guid}/renew-upload-url/{documentId:guid}")]
    public async Task<ActionResult<InitUploadResponseDto>> RenewUploadUrl(Guid id, Guid documentId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _datasetService.RenewUploadUrlAsync(userId, id, documentId);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(result.Data);
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
