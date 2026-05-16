using System.Security.Claims;
using MarkdownGenQAs.Application.Dto.Thread;
using MarkdownGenQAs.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/threads")]
[Authorize]
public class ThreadController : ControllerBase
{
    private readonly ApplicationContext _context;

    public ThreadController(ApplicationContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<List<ThreadListDto>>> GetList(
        [FromQuery] Guid? id,
        [FromQuery] string? title)
    {
        var query = _context.Threads.AsQueryable();

        if (id.HasValue)
            query = query.Where(t => t.Id == id.Value);

        if (!string.IsNullOrWhiteSpace(title))
            query = query.Where(t => EF.Functions.ILike(t.Title, $"%{title}%"));

        var threads = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .Select(t => new ThreadListDto
            {
                Id = t.Id,
                UserId = t.UserId,
                Title = t.Title,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(threads);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ThreadDetailDto>> GetById(Guid id)
    {
        var thread = await _context.Threads
            .Where(t => t.Id == id)
            .Select(t => new ThreadDetailDto
            {
                Id = t.Id,
                UserId = t.UserId,
                Title = t.Title,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            })
            .FirstOrDefaultAsync();

        if (thread == null)
            return NotFound(new { error = "Thread not found" });

        return Ok(thread);
    }

    [HttpPost]
    public async Task<ActionResult<ThreadDetailDto>> Create([FromBody] CreateThreadRequestDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var thread = new Models.Entities.ConversationThread
        {
            UserId = userId,
            Title = dto.Title
        };

        _context.Threads.Add(thread);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = thread.Id }, new ThreadDetailDto
        {
            Id = thread.Id,
            UserId = thread.UserId,
            Title = thread.Title,
            CreatedAt = thread.CreatedAt,
            UpdatedAt = thread.UpdatedAt
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateThreadRequestDto dto)
    {
        var thread = await _context.Threads.FirstOrDefaultAsync(t => t.Id == id);

        if (thread == null)
            return NotFound(new { error = "Thread not found" });

        thread.Title = dto.Title;
        await _context.SaveChangesAsync();

        return Ok(new ThreadDetailDto
        {
            Id = thread.Id,
            UserId = thread.UserId,
            Title = thread.Title,
            CreatedAt = thread.CreatedAt,
            UpdatedAt = thread.UpdatedAt
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var thread = await _context.Threads.FirstOrDefaultAsync(t => t.Id == id);

        if (thread == null)
            return NotFound(new { error = "Thread not found" });

        thread.IsDeleted = true;
        thread.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
