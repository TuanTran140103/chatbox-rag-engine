using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Application.Interfaces.ExternalServices;

public interface ITokenCountService
{
    Task<CountResponse> CountAsync(CountRequest request, CancellationToken cancellationToken = default);
    Task<BatchCountResponse> BatchCountAsync(BatchCountRequest request, CancellationToken cancellationToken = default);
}
