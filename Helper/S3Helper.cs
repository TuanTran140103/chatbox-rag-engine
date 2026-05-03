using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownGenQAs.Helper;

public class S3Helper
{
    public static string NormalizeObjectKey(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Guid.NewGuid().ToString();

        // Preserve path prefix (e.g., "image/file.png")
        string? pathPrefix = null;
        if (fileName.Contains('/'))
        {
            var lastSlash = fileName.LastIndexOf('/');
            pathPrefix = fileName.Substring(0, lastSlash + 1);
            fileName = fileName.Substring(lastSlash + 1);
        }

        // Unicode normalization (NFC)
        fileName = fileName.Normalize(NormalizationForm.FormC);

        // trim spaces
        fileName = fileName.Trim();

        // split name + extension
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            nameWithoutExt = Guid.NewGuid().ToString();

        // remove control chars
        nameWithoutExt = new string(nameWithoutExt.Where(c => !char.IsControl(c)).ToArray());

        // convert spaces to hyphen
        nameWithoutExt = Regex.Replace(nameWithoutExt, @"\s+", "-");

        // replace unsafe characters
        nameWithoutExt = Regex.Replace(nameWithoutExt, @"[\\{}\^\%\`\]\[\""<>\#\|\?\$\&]", "-");

        // collapse multiple hyphens
        nameWithoutExt = Regex.Replace(nameWithoutExt, "-{2,}", "-");

        // lower case (optional but recommended)
        nameWithoutExt = nameWithoutExt.ToLowerInvariant();

        // trim leftover hyphen
        nameWithoutExt = nameWithoutExt.Trim('-');

        // ensure not empty
        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            nameWithoutExt = Guid.NewGuid().ToString();

        // Final key
        var normalized = nameWithoutExt + ext.ToLowerInvariant();

        // S3 max 1024 bytes — you can safely cap at ~255 chars
        if (normalized.Length > 255)
            normalized = normalized.Substring(0, 255);

        // Restore path prefix
        return pathPrefix != null ? pathPrefix + normalized : normalized;
    }
}