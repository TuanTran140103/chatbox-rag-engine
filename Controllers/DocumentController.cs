using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MarkdownGenQAs.Application.Dto.Documents;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Application.Service;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/documents")]
[Authorize(Roles = "User")]
public class DocumentController : ControllerBase
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DocumentService _documentService;
    private readonly NotificationService _notificationService;
    private readonly IAccessControlService _accessControl;
    private readonly ILogger<DocumentController> _logger;

    public DocumentController(
        DocumentService documentService,
        NotificationService notificationService,
        IAccessControlService accessControl,
        ILogger<DocumentController> logger)
    {
        _documentService = documentService;
        _notificationService = notificationService;
        _accessControl = accessControl;
        _logger = logger;
    }

    #region Content Retrieval

    [HttpGet("{id}/detail")]
    [ProducesResponseType(typeof(DocumentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDetailDto>> GetDetail(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound();

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

    [HttpGet("{id}/content/ocr")]
    public async Task<ActionResult<string>> GetOcrContent(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound();

        var result = await _documentService.GetOcrContentAsync(id);
        if (!result.IsSuccess) return result.ErrorMessage == "OCR content not found" ? NotFound(result.ErrorMessage) : StatusCode(500, result.ErrorMessage);
        return Ok(result.Data);
    }

    [HttpGet("{id}/content/chunks")]
    public async Task<ActionResult<string>> GetChunkContent(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound();

        var result = await _documentService.GetChunkContentAsync(id);
        if (!result.IsSuccess) return result.ErrorMessage == "Chunk content not found" ? NotFound(result.ErrorMessage) : StatusCode(500, result.ErrorMessage);
        return Ok(result.Data);
    }

    [HttpGet("{id}/content/summary")]
    public async Task<ActionResult<string>> GetSummaryContent(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound();

        var result = await _documentService.GetSummaryContentAsync(id);
        if (!result.IsSuccess) return result.ErrorMessage == "Summary content not found" ? NotFound(result.ErrorMessage) : StatusCode(500, result.ErrorMessage);
        return Ok(result.Data);
    }

    [HttpGet("{id}/logs")]
    [ProducesResponseType(typeof(IEnumerable<NotificationMessage>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<NotificationMessage>>> GetLogs(Guid id, [FromQuery] string type = "ocr")
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound();

        var result = await _notificationService.GetHistoryAsync(id, type);
        if (!result.IsSuccess) return BadRequest(result.ErrorMessage);
        return Ok(result.Data);
    }

    [HttpGet("{id}/notifications")]
    [Produces("text/event-stream")]
    public async Task GetNotifications(
        Guid id,
        [FromQuery] string type = "ocr",
        CancellationToken ct = default)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
        {
            Response.StatusCode = 404;
            return;
        }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        _logger.LogInformation("SSE notification stream started. DocumentId: {DocumentId}, Type: {Type}", id, type);

        try
        {
            await foreach (var message in _notificationService.SubscribeAsync(type, id, ct))
            {
                _logger.LogInformation("SSE sending message. DocumentId: {DocumentId}, Status: {Status}, Message: {Msg}, Stage: {Stage}, ProcessType: {ProcessType}",
                    id, message.Status, message.Message, message.Stage, message.ProcessType);
                var json = JsonSerializer.Serialize(message, _jsonOptions);
                await Response.WriteAsync($"data: {json}\r\n\r\n", ct);
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

    #region OCR Operations

    [HttpGet("ocr/supported-models")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<string>>> GetSupportedModels()
    {
        try
        {
            var models = await _documentService.GetSupportedModelsAsync();
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching supported OCR models");
            return StatusCode(500, "Failed to fetch supported OCR models");
        }
    }

    [HttpPost("ocr/process/{id}")]
    [ProducesResponseType(typeof(OcrProcessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OcrProcessResponse>> ProcessOCR(Guid id, [FromQuery] string? modelId = null)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanWriteDocumentAsync(userId, id))
            return NotFound();

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

    [HttpPost("ocr/cancel/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelOCR(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanWriteDocumentAsync(userId, id))
            return NotFound();

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

    [HttpPost("indexing/process/{id}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ProcessIndexing(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanWriteDocumentAsync(userId, id))
            return NotFound();

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


    [HttpPost("indexing/cancel/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelIndexing(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanWriteDocumentAsync(userId, id))
            return NotFound();

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

    [HttpGet("{id}/metadata")]
    [ProducesResponseType(typeof(DocumentMetadata), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentMetadata>> GetMetadata(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound();

        var result = await _documentService.GetDetailAsync(id);
        if (!result.IsSuccess)
        {
            if (result.ErrorMessage == "File not found") return NotFound();
            return StatusCode(500, result.ErrorMessage);
        }
        return Ok(result.Data!.Metadata);
    }

    [HttpPut("{id}/metadata")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMetadata(Guid id, [FromBody] UpdateMetadataRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanWriteDocumentAsync(userId, id))
            return NotFound();

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

    [HttpGet("{id}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string scope = "original")
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _accessControl.CanViewDocumentAsync(userId, id))
            return NotFound("File not found");

        if (string.Equals(scope, "original", StringComparison.OrdinalIgnoreCase))
        {
            var url = await _documentService.GetPresignedDownloadUrlAsync(id);
            if (url == null) return NotFound("File not found");
            return Redirect(url);
        }

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
