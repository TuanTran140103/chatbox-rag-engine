using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.Search;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/search")]
[Authorize(Roles = "User")]
public class SearchController : ControllerBase
{
    private readonly ISearchService _searchService;

    public SearchController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    [HttpGet("documents/{documentId:guid}")]
    public async Task<ActionResult<ReadDocumentResult>> ReadDocument(
        Guid documentId,
        [FromQuery] string? contentType)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _searchService.ReadDocumentAsync(userId, documentId, contentType);

        if (!result.IsSuccess)
            return MapError(result.ErrorMessage);

        return Ok(result.Data);
    }

    [HttpPost("documents/by-name")]
    public async Task<ActionResult<List<DocumentSearchItem>>> SearchDocumentsByName(
        [FromBody] SearchByNameRequest request)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _searchService.SearchDocumentsByNameAsync(
            userId, request.QueryText, request.DatasetIds);

        if (!result.IsSuccess)
            return MapError(result.ErrorMessage);

        return Ok(result.Data);
    }

    [HttpPost("vector")]
    public async Task<ActionResult<List<VectorSearchItem>>> VectorSearch(
        [FromBody] VectorSearchRequest request)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _searchService.VectorSearchAsync(userId, request);

        if (!result.IsSuccess)
            return MapError(result.ErrorMessage);

        return Ok(result.Data);
    }

    [HttpGet("datasets/{datasetId:guid}/documents")]
    public async Task<ActionResult<List<DocumentSearchItem>>> ListDatasetDocuments(Guid datasetId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _searchService.ListDatasetDocumentsAsync(userId, datasetId);

        if (!result.IsSuccess)
            return MapError(result.ErrorMessage);

        return Ok(result.Data);
    }

    private ActionResult MapError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return StatusCode(500, new { error = "Internal server error" });

        var lower = errorMessage.ToLowerInvariant();

        if (lower.Contains("not found"))
            return NotFound(new { error = errorMessage });

        if (lower.Contains("access denied") || lower.Contains("forbidden"))
            return StatusCode(403, new { error = errorMessage });

        if (lower.StartsWith("internal server error"))
            return StatusCode(500, new { error = errorMessage });

        return StatusCode(500, new { error = errorMessage });
    }
}
