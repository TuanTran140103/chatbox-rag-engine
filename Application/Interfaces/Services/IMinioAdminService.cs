namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IMinioAdminService
{
    Task EnsureOcrUserAsync(CancellationToken cancellationToken = default);
}
