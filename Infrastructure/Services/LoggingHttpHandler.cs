using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MarkdownGenQAs.Infrastructure.Services;

public class LoggingHttpHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public LoggingHttpHandler(ILogger logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("=== HTTP REQUEST ===");
            _logger.LogInformation("Method: {Method}", request.Method);
            _logger.LogInformation("URL: {Url}", request.RequestUri);
            _logger.LogInformation("Body: {Body}", body);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content != null)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("=== HTTP RESPONSE ===");
            _logger.LogInformation("Status: {Status}", response.StatusCode);
            _logger.LogInformation("Body: {Body}", responseBody);
        }

        return response;
    }
}
