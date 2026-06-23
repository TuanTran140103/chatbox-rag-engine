using MarkdownGenQAs.Application.Dto.Admin;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers.Admin;

[ApiController]
[Route("api/v1/admin/orphan-files")]
[Authorize(Roles = "Admin")]
public class OrphanFilesController : ControllerBase
{
    private readonly IOrphanFileCleanupService _orphanService;

    public OrphanFilesController(IOrphanFileCleanupService orphanService)
    {
        _orphanService = orphanService;
    }

    [HttpGet]
    public async Task<ActionResult<List<OrphanFileDto>>> GetOrphanFiles()
    {
        var result = await _orphanService.GetOrphanFilesAsync();
        return Ok(result);
    }

    [HttpPost("cleanup")]
    public async Task<ActionResult<OrphanCleanupResultDto>> CleanupOrphanFiles()
    {
        var result = await _orphanService.CleanupOrphanFilesAsync();
        return Ok(result);
    }

    [HttpPost("cleanup-stuck-uploads")]
    public async Task<ActionResult<int>> CleanupStuckUploads()
    {
        var result = await _orphanService.CleanupStuckUploadingDocumentsAsync();
        return Ok(new { cleaned = result });
    }
}
