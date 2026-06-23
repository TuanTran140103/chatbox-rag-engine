using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Application.Dto.DocumentJobs;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/ocr-jobs")]
[Authorize(Roles = "User")]
public class OCRFileJobController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IAccessControlService _accessControl;
    private readonly ILogger<OCRFileJobController> _logger;

    public OCRFileJobController(
        IUnitOfWork uow,
        IAccessControlService accessControl,
        ILogger<OCRFileJobController> logger)
    {
        _uow = uow;
        _accessControl = accessControl;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<DocumentJobDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<DocumentJobDto>>> GetAll()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var accessibleDocIds = await _accessControl.GetAccessibleDocumentIdsAsync(userId);

            var jobs = await _uow.DocumentJobs.GetAllAsync();
            var filteredJobs = jobs.Where(j => accessibleDocIds.Contains(j.DocumentId));
            var jobDtos = filteredJobs.Select(MapToDto);
            return Ok(jobDtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all OCR file jobs");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DocumentJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentJobDto>> GetById(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var job = await _uow.DocumentJobs.GetByIdAsync(id);
            if (job == null) return NotFound();

            if (!await _accessControl.CanViewDocumentAsync(userId, job.DocumentId))
                return NotFound();

            return Ok(MapToDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting OCR file job {Id}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("by-file/{documentId}")]
    [ProducesResponseType(typeof(DocumentJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentJobDto>> GetByFileId(Guid documentId)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            if (!await _accessControl.CanViewDocumentAsync(userId, documentId))
                return NotFound("No OCR job found for this file");

            var job = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);
            if (job == null) return NotFound("No OCR job found for this file");
            return Ok(MapToDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting OCR file job for file {FileId}", documentId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("by-external-file/{ocrJobId}")]
    [ProducesResponseType(typeof(DocumentJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentJobDto>> GetByFileJobId(string ocrJobId)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var job = await _uow.DocumentJobs.GetByOcrJobIdAsync(ocrJobId);
            if (job == null) return NotFound("No OCR job found for this external file job ID");

            if (!await _accessControl.CanViewDocumentAsync(userId, job.DocumentId))
                return NotFound("No OCR job found for this external file job ID");

            return Ok(MapToDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting OCR file job for external file job ID {OcrJobId}", ocrJobId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var job = await _uow.DocumentJobs.GetByIdAsync(id);
            if (job == null) return NotFound();

            if (!await _accessControl.CanWriteDocumentAsync(userId, job.DocumentId))
                return NotFound();

            _uow.DocumentJobs.Delete(job);
            await _uow.SaveChangesAsync();

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting OCR file job {Id}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    private static DocumentJobDto MapToDto(DocumentJob job)
    {
        return new DocumentJobDto
        {
            DocumentId = job.DocumentId,
            OcrJobId = job.OcrJobId,
            IndexingJobId = job.IndexingJobId,
        };
    }
}
