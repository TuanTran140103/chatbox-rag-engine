using System.Text;

namespace MarkdownGenQAs.Utils;

public static class ValidateFileUtil
{
    private const int HeaderSize = 16;

    public static async Task<(bool IsValid, string? ErrorMessage)> ValidateAsync(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => await ValidatePdfAsync(stream, fileName),
            ".docx" => await ValidateDocxAsync(stream, fileName),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".webp" => await ValidateImageAsync(stream, fileName),
            _ => (false, $"Unsupported file format: {extension}")
        };
    }

    public static bool IsValidPdf(byte[] head)
    {
        if (head.Length < 4)
            return false;

        var signature = Encoding.ASCII.GetString(head);
        var trimmed = signature.AsSpan().TrimStart();

        if (trimmed.Length >= 3 && trimmed[0] == '\u00EF' && trimmed[1] == '\u00BB' && trimmed[2] == '\u00BF')
            trimmed = trimmed[3..];

        return trimmed.StartsWith("%PDF");
    }

    public static async Task<(bool IsValid, string? ErrorMessage)> ValidatePdfAsync(Stream stream, string fileName)
    {
        var header = await ReadHeaderAsync(stream);

        if (!IsValidPdf(header))
            return (false, "Invalid PDF file content");

        return (true, null);
    }

    public static async Task<(bool IsValid, string? ErrorMessage)> ValidateDocxAsync(Stream stream, string fileName)
    {
        var header = await ReadHeaderAsync(stream);

        if (header.Length < 4)
            return (false, "Invalid Word file content");

        if (header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x03 || header[3] != 0x04)
            return (false, "Invalid Word file content");

        return (true, null);
    }

    public static async Task<(bool IsValid, string? ErrorMessage)> ValidateImageAsync(Stream stream, string fileName)
    {
        var header = await ReadHeaderAsync(stream);

        if (header.Length < 4)
            return (false, "Invalid image file content");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        var (match, expected) = ext switch
        {
            ".jpg" or ".jpeg" => (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF, "JPEG"),
            ".png" => (header.Length >= 8
                && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A, "PNG"),
            ".gif" => (header.Length >= 4 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38, "GIF"),
            ".bmp" => (header.Length >= 2 && header[0] == 0x42 && header[1] == 0x4D, "BMP"),
            ".tiff" or ".tif" => (
                (header.Length >= 4 && header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
                || (header.Length >= 4 && header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A), "TIFF"),
            ".webp" => (header.Length >= 12
                && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50, "WebP"),
            _ => (false, "Unknown image format")
        };

        if (!match)
            return (false, $"Invalid image file content: expected {expected}");

        return (true, null);
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream stream)
    {
        if (!stream.CanSeek)
            throw new InvalidOperationException("Stream must be seekable before calling ValidateFileUtil. Buffer the stream first if needed.");

        var originalPosition = stream.Position;
        var buffer = new byte[HeaderSize];
        var totalRead = 0;

        while (totalRead < HeaderSize)
        {
            var n = await stream.ReadAsync(buffer, totalRead, HeaderSize - totalRead);
            if (n == 0) break;
            totalRead += n;
        }

        stream.Position = originalPosition;

        return totalRead < HeaderSize ? buffer[..totalRead] : buffer;
    }
}
