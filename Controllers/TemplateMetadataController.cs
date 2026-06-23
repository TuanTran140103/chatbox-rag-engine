using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.TemplateMetadata;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/templates")]
public class TemplateMetadataController : ControllerBase
{
    private readonly ITemplateMetadataService _templateMetadataService;

    public TemplateMetadataController(ITemplateMetadataService templateMetadataService)
    {
        _templateMetadataService = templateMetadataService;
    }

    [HttpGet]
    [Authorize(Roles = "User")]
    public async Task<ActionResult<List<TemplateMetadataListDto>>> GetAll()
    {
        var result = await _templateMetadataService.GetAllAsync();
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "User")]
    public async Task<ActionResult<TemplateMetadataDetailDto>> GetById(Guid id)
    {
        var result = await _templateMetadataService.GetByIdAsync(id);
        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });
        return Ok(result.Data);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TemplateMetadataDetailDto>> Create([FromBody] CreateTemplateMetadataRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _templateMetadataService.CreateAsync(userId, dto);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, result.Data);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TemplateMetadataDetailDto>> Update(Guid id, [FromBody] UpdateTemplateMetadataRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _templateMetadataService.UpdateAsync(userId, id, dto);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _templateMetadataService.DeleteAsync(userId, id);

        if (!result.IsSuccess)
            return NotFound(new { error = result.ErrorMessage });

        return NoContent();
    }
}
