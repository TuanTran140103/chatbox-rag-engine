using MarkdownGenQAs.Application.Dto.Admin;

namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IOrphanFileCleanupService
{
    Task<List<OrphanFileDto>> GetOrphanFilesAsync(CancellationToken ct = default);
    Task<OrphanCleanupResultDto> CleanupOrphanFilesAsync(CancellationToken ct = default);
    Task<int> CleanupStuckUploadingDocumentsAsync(CancellationToken ct = default);
}
