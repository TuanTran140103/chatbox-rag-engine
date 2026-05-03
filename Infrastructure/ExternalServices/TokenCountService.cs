using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Infrastructure.ExternalServices;

public class TokenCountService : ITokenCountService
{
    private readonly HttpClient _httpClient;
    private const string CountEndpoint = "/count";
    private const string BatchCountEndpoint = "/batch-count";
    private readonly ILogger<TokenCountService> _logger;

    public TokenCountService(HttpClient httpClient, ILogger<TokenCountService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CountResponse> CountAsync(CountRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(CountEndpoint, request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<CountResponse>(cancellationToken: cancellationToken);
            if (result == null)
            {
                _logger.LogError("CountAsync - API response was empty or could not be deserialized");
                throw new InvalidOperationException("API response content was empty or could not be deserialized.");
            }
            return result;
        }
        else
        {
            string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("CountAsync failed with status code {StatusCode}. Endpoint: {Endpoint}, Error: {ErrorContent}",
                response.StatusCode, CountEndpoint, errorContent);
            throw new HttpRequestException($"API call to {CountEndpoint} failed with status code {response.StatusCode}. Content: {errorContent}");
        }
    }

    public async Task<BatchCountResponse> BatchCountAsync(BatchCountRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(BatchCountEndpoint, request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<BatchCountResponse>(cancellationToken: cancellationToken);
            if (result == null)
            {
                _logger.LogError("BatchCountAsync - API response was empty or could not be deserialized");
                throw new InvalidOperationException("API response content was empty or could not be deserialized.");
            }
            return result;
        }
        else
        {
            string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("BatchCountAsync failed with status code {StatusCode}. Endpoint: {Endpoint}, Error: {ErrorContent}",
                response.StatusCode, BatchCountEndpoint, errorContent);
            throw new HttpRequestException($"API call to {BatchCountEndpoint} failed with status code {response.StatusCode}. Content: {errorContent}");
        }
    }
}
