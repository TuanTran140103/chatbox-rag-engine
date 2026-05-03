using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Application.Dto.DocumentJobs;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/ocr-jobs")]
[Authorize]
public class OCRFileJobController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<OCRFileJobController> _logger;

    public OCRFileJobController(
        IUnitOfWork uow,
        ILogger<OCRFileJobController> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    /// <summary>
    /// Get all OCR file jobs
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<DocumentJobDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<DocumentJobDto>>> GetAll()
    {
        try
        {
            var jobs = await _uow.DocumentJobs.GetAllAsync();
            var jobDtos = jobs.Select(MapToDto);
            return Ok(jobDtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all OCR file jobs");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get OCR file job by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DocumentJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentJobDto>> GetById(Guid id)
    {
        try
        {
            var job = await _uow.DocumentJobs.GetByIdAsync(id);
            if (job == null) return NotFound();
            return Ok(MapToDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting OCR file job {Id}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get OCR file job by OCR File ID
    /// </summary>
    [HttpGet("by-file/{documentId}")]
    [ProducesResponseType(typeof(DocumentJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentJobDto>> GetByFileId(Guid documentId)
    {
        try
        {
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

    /// <summary>
    /// Get OCR file job by external File Job ID (file_id from OCR server)
    /// </summary>
    [HttpGet("by-external-file/{ocrJobId}")]
    [ProducesResponseType(typeof(DocumentJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentJobDto>> GetByFileJobId(string ocrJobId)
    {
        try
        {
            var job = await _uow.DocumentJobs.GetByOcrJobIdAsync(ocrJobId);
            if (job == null) return NotFound("No OCR job found for this external file job ID");
            return Ok(MapToDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting OCR file job for external file job ID {OcrJobId}", ocrJobId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Delete OCR file job
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var job = await _uow.DocumentJobs.GetByIdAsync(id);
            if (job == null) return NotFound();

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

    /// <summary>
    /// Maps OCRFileJob entity to OCRFileJobDto
    /// </summary>
    private static DocumentJobDto MapToDto(DocumentJob job)
    {
        return new DocumentJobDto
        {
            DocumentId = job.DocumentId,
            OcrJobId = job.OcrJobId,
            GenQaJobId = job.GenQaJobId,
        };
    }
}
