namespace MarkdownGenQAs.Infrastructure.Exceptions;

public class OcrApiException : Exception
{
    public int StatusCode { get; }
    public string? ErrorBody { get; }

    public OcrApiException(int statusCode, string? errorBody = null)
        : base($"OCR API error: {(int)statusCode} {GetStatusReason(statusCode)}. Details: {errorBody ?? "No details"}")
    {
        StatusCode = statusCode;
        ErrorBody = errorBody;
    }

    public OcrApiException(int statusCode, string? errorBody, Exception innerException)
        : base($"OCR API error: {(int)statusCode} {GetStatusReason(statusCode)}. Details: {errorBody ?? "No details"}", innerException)
    {
        StatusCode = statusCode;
        ErrorBody = errorBody;
    }

    private static string GetStatusReason(int statusCode)
    {
        return statusCode switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            _ => "Unknown"
        };
    }
}
