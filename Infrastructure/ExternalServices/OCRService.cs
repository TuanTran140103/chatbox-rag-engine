using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MarkdownGenQAs.Options;
using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Infrastructure.Exceptions;

namespace MarkdownGenQAs.Infrastructure.ExternalServices;

public class OCRService : IOCRService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OCRService> _logger;

    public OCRService(HttpClient httpClient, IOptions<ExternalServiceOptions> options, ILogger<OCRService> logger)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(options.Value.OCRService.BaseUrl);
        _logger = logger;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient.GetAsync("/health", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "healthy";
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OCR ping timeout after 3 seconds");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR ping failed");
            return false;
        }
    }

    public async Task<List<string>> GetSupportedModelsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/ocr/supported-models");
            response.EnsureSuccessStatusCode();
            var models = await response.Content.ReadFromJsonAsync<List<string>>();
            return models ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching supported OCR models");
            throw new OcrApiException(500, "Failed to fetch supported OCR models", ex);
        }
    }

    public async Task<OcrProcessResponse> ProcessFromS3Async(string bucketName, string objectKey, string modelId = "deepseekocr")
    {
        try
        {
            var payload = new
            {
                bucket = bucketName,
                objectKey,
                modelId
            };

            var json = JsonSerializer.Serialize(payload);
            var stringContent = new StringContent(json, Encoding.UTF8, "application/json");

            var url = "/api/ocr/process";
            _logger.LogInformation("Calling OCR API ProcessFromS3: {Url}, Bucket: {Bucket}, Key: {ObjectKey}, ModelId: {ModelId}",
                url, bucketName, objectKey, modelId);

            var response = await _httpClient.PostAsync(url, stringContent);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("OCR API error: {StatusCode}, Body: {Error}", response.StatusCode, error);
                throw new OcrApiException((int)response.StatusCode, error);
            }

            var result = await response.Content.ReadFromJsonAsync<OcrProcessResponse>();

            if (result == null)
            {
                _logger.LogError("OCR API returned null response");
                throw new OcrApiException((int)response.StatusCode, "API returned null response");
            }

            return result;
        }
        catch (OcrApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OCR API ProcessFromS3Async");
            throw new OcrApiException(500, "Internal error calling OCR service", ex);
        }
    }

    public async Task<OcrMarkdownResponse> GetMarkdownAsync(string taskId)
    {
        try
        {
            // API mới: /api/ocr/get-markdown/{taskId} (lowercase)
            var response = await _httpClient.GetAsync($"/api/ocr/get-markdown/{taskId}");
            if (response.IsSuccessStatusCode)
            {
                // API trả về JSON array trực tiếp: [{ "pageIndex": 0, "markdown": "...", "images": {} }, ...]
                var pages = await response.Content.ReadFromJsonAsync<List<PageOcrResult>>();
                if (pages == null)
                {
                    _logger.LogError("OCR API GetMarkdown returned null response for task {TaskId}", taskId);
                    throw new OcrApiException((int)response.StatusCode, "API returned null response");
                }
                // Implicit conversion từ List<PageOcrResult> sang OcrMarkdownResponse
                return pages;
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to get markdown for task {TaskId}. Status: {Status}, Error: {Error}",
                taskId, response.StatusCode, error);
            throw new OcrApiException((int)response.StatusCode, error);
        }
        catch (OcrApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OCR API GetMarkdownAsync for taskId: {TaskId}", taskId);
            throw new OcrApiException(500, "Internal error calling OCR service", ex);
        }
    }

    public async Task<string?> CancelJobAsync(string taskId)
    {
        try
        {
            // API mới: /api/ocr/cancel (lowercase)
            var response = await _httpClient.PostAsync($"/api/ocr/cancel?taskId={taskId}", null);
            if (response.IsSuccessStatusCode)
            {
                // Đọc message từ response JSON
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (result.TryGetProperty("message", out var messageProp))
                {
                    return messageProp.GetString();
                }
                return "Cancel request successful";
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to cancel OCR task {TaskId}. Status: {Status}, Error: {Error}",
                taskId, response.StatusCode, error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OCR API CancelJobAsync for taskId: {TaskId}", taskId);
            return null;
        }
    }
}
