using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Runtime.CompilerServices;
using MarkdownGenQAs.Application.Dto;
using MarkdownGenQAs.Application.Dto.Documents;
using MarkdownGenQAs.Application.Service;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;


namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/documents")]
[Authorize]
public class DocumentController : ControllerBase
{
    private readonly DocumentService _documentService;
    private readonly NotificationService _notificationService;
    private readonly ILogger<DocumentController> _logger;
    private const long MaxFileSize = 1024 * 1024 * 100; // 100MB

    public DocumentController(
        DocumentService documentService,
        NotificationService notificationService,
        ILogger<DocumentController> logger)
    {
        _documentService = documentService;
        _notificationService = notificationService;
        _logger = logger;
    }

    #region Content Retrieval

    /// <summary>
    /// Get full document detail (including OCR/QA/Summary content)
    /// </summary>
    [HttpGet("{id}/detail")]
    [ProducesResponseType(typeof(DocumentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDetailDto>> GetDetail(Guid id)
    {
        var result = await _documentService.GetDetailAsync(id);
        if (!result.IsSuccess)
        {
            if (result.ErrorMessage == "File not found") return NotFound();
            return StatusCode(500, result.ErrorMessage);
        }
        return Ok(result.Data);
    }

    #endregion

    #region Content Retrieval

    /// <summary>
    /// Get only OCR content
    /// </summary>
    [HttpGet("{id}/content/ocr")]
    public async Task<ActionResult<string>> GetOcrContent(Guid id)
    {
        var result = await _documentService.GetOcrContentAsync(id);
        if (!result.IsSuccess) return result.ErrorMessage == "OCR content not found" ? NotFound(result.ErrorMessage) : StatusCode(500, result.ErrorMessage);
        return Ok(result.Data);
    }

    /// <summary>
    /// Get only chunk content (JSON)
    /// </summary>
    [HttpGet("{id}/content/chunks")]
    public async Task<ActionResult<string>> GetChunkContent(Guid id)
    {
        var result = await _documentService.GetChunkContentAsync(id);
        if (!result.IsSuccess) return result.ErrorMessage == "Chunk content not found" ? NotFound(result.ErrorMessage) : StatusCode(500, result.ErrorMessage);
        return Ok(result.Data);
    }

    /// <summary>
    /// Get only Summary content
    /// </summary>
    [HttpGet("{id}/content/summary")]
    public async Task<ActionResult<string>> GetSummaryContent(Guid id)
    {
        var result = await _documentService.GetSummaryContentAsync(id);
        if (!result.IsSuccess) return result.ErrorMessage == "Summary content not found" ? NotFound(result.ErrorMessage) : StatusCode(500, result.ErrorMessage);
        return Ok(result.Data);
    }

    /// <summary>
    /// Get historical processing logs
    /// </summary>
    /// <param name="id">Document Id</param>
    /// <param name="type">Process type: ocr (default) or indexing</param>
    [HttpGet("{id}/logs")]
    [ProducesResponseType(typeof(IEnumerable<NotificationMessage>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<NotificationMessage>>> GetLogs(Guid id, [FromQuery] string type = "ocr")
    {
        var result = await _notificationService.GetHistoryAsync(id, type);
        if (!result.IsSuccess) return BadRequest(result.ErrorMessage);
        return Ok(result.Data);
    }

    /// <summary>
    /// Subscribe to real-time processing notifications (Server-Sent Events)
    /// </summary>
    /// <param name="id">Document Id</param>
    /// <param name="type">Process type: ocr (default) or indexing</param>
    [HttpGet("{id}/notifications")]
    [Produces("text/event-stream")]
    public async Task GetNotifications(
        Guid id,
        [FromQuery] string type = "ocr",
        [FromHeader(Name = "Last-Event-ID")] string? lastEventId = null,
        CancellationToken ct = default)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        _logger.LogInformation("SSE notification stream started. DocumentId: {DocumentId}, Type: {Type}, LastEventId: {LastEventId}", id, type, lastEventId ?? "null");

        try
        {
            await foreach (var message in _notificationService.SubscribeWithResumeAsync(type, id, lastEventId, ct))
            {
                if (!string.IsNullOrEmpty(message.EntryId))
                {
                    await Response.WriteAsync($"id: {message.EntryId}\r\n", ct);
                }
                _logger.LogDebug("SSE notification sent. DocumentId: {DocumentId}, EntryId: {EntryId}", id, message.EntryId);
                await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(message)}\r\n\r\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE notification stream cancelled. DocumentId: {DocumentId}, Type: {Type}", id, type);
        }
        finally
        {
            _logger.LogInformation("SSE notification stream disconnected. DocumentId: {DocumentId}, Type: {Type}", id, type);
        }
    }

    #endregion

    #region Upload Operation

    /// <summary>
    /// Standalone PDF file upload
    /// </summary>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(DocumentUploadResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<ActionResult<DocumentUploadResponseDto>> UploadFile([FromForm] IFormFile file, [FromForm] Guid? categoryId)
    {
        if (file == null || file.Length == 0)
            return BadRequest("File is required");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var contentType = extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };

        using var stream = file.OpenReadStream();
        var dto = new DocumentUploadRequestDto
        {
            FileStream = stream,
            FileName = file.FileName,
            ContentType = contentType,
            CategoryId = categoryId
        };

        var result = await _documentService.UploadAsync(dto);
        if (!result.IsSuccess) return BadRequest(result.ErrorMessage);
        return Ok(result.Data);
    }

    #endregion

    #region OCR Operations

    /// <summary>
    /// Process PDF file with OCR using file stored in S3
    /// </summary>
    /// <param name="id">Document ID</param>
    /// <param name="modelId">OCR model ID (default: chandraocr)</param>
    [HttpPost("ocr/process/{id}")]
    [ProducesResponseType(typeof(OcrProcessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OcrProcessResponse>> ProcessOCR(Guid id, [FromQuery] string? modelId = null)
    {
        try
        {
            var result = await _documentService.ProcessOCR(id, modelId);
            if (!result.IsSuccess)
            {
                return BadRequest(result.ErrorMessage);
            }
            return Accepted(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR processing error for file {Id}", id);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancel OCR job
    /// </summary>
    [HttpPost("ocr/cancel/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelOCR(Guid id)
    {
        try
        {
            var result = await _documentService.CancelOCR(id);
            if (!result.IsSuccess)
            {
                if (result.ErrorMessage!.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    return NotFound(result.ErrorMessage);
                return BadRequest(result.ErrorMessage);
            }
            return Ok(new { message = result.Data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling OCR for document {Id}", id);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    #endregion

    #region Indexing Operations

    /// <summary>
    /// Trigger background document indexing (metadata + chunking + summary + Qdrant)
    /// </summary>
    [HttpPost("indexing/process/{id}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ProcessIndexing(Guid id)
    {
        try
        {
            var result = await _documentService.ProcessIndexing(id);
            if (!result.IsSuccess)
            {
                return BadRequest(result.ErrorMessage);
            }
            return Accepted(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing processing error for document {DocumentId}", id);
            return StatusCode(500, ex.Message);
        }
    }


    /// <summary>
    /// Cancel indexing background job
    /// </summary>
    [HttpPost("indexing/cancel/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelIndexing(Guid id)
    {
        try
        {
            var result = await _documentService.CancelIndexing(id);
            if (!result.IsSuccess)
            {
                if (result.ErrorMessage != null && result.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    return NotFound(result.ErrorMessage);
                return BadRequest(result.ErrorMessage ?? "Unknown error occurred");
            }
            return Ok(new { message = result.Data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling indexing for document {Id}", id);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    #endregion

    #region Metadata Operations

    /// <summary>
    /// Get document metadata (extraction result)
    /// </summary>
    [HttpGet("{id}/metadata")]
    [ProducesResponseType(typeof(DocumentMetadata), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentMetadata>> GetMetadata(Guid id)
    {
        var result = await _documentService.GetDetailAsync(id);
        if (!result.IsSuccess)
        {
            if (result.ErrorMessage == "File not found") return NotFound();
            return StatusCode(500, result.ErrorMessage);
        }
        return Ok(result.Data!.Metadata);
    }

    /// <summary>
    /// Update document metadata (human review/override)
    /// </summary>
    [HttpPut("{id}/metadata")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMetadata(Guid id, [FromBody] UpdateMetadataRequestDto dto)
    {
        var result = await _documentService.UpdateMetadataAsync(id, dto.MetadataContent, dto.IsExtracted);
        if (!result.IsSuccess)
        {
            if (result.ErrorMessage == "Document not found")
                return NotFound(new { error = result.ErrorMessage });
            return BadRequest(new { error = result.ErrorMessage });
        }
        return Ok(new { message = "Metadata updated successfully" });
    }

    #endregion

    #region Download Operations

    /// <summary>
    /// Consolidated download endpoint
    /// </summary>
    /// <param name="id">File ID</param>
    /// <param name="scope">Download scope: original, ocr-markdown, chunks-markdown, all</param>
    [HttpGet("{id}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string scope = "original")
    {
        var result = await _documentService.GetDownloadDataAsync(id, scope);
        if (!result.IsSuccess)
        {
            if (result.ErrorMessage == "File not found") return NotFound("File not found");
            return BadRequest(result.ErrorMessage);
        }

        var (stream, contentType, fileName) = result.Data;
        return File(stream, contentType, fileName);
    }

    #endregion
}
