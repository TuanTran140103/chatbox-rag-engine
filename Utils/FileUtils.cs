using System.IO;

namespace MarkdownGenQAs.Utils;

/// <summary>
/// Utility class for file operations. BaseDir is always at project root level,
/// not inside bin/ directory which gets regenerated on each build.
/// </summary>
public static class FileUtils
{
    /// <summary>
    /// Base directory at project root (same level as .csproj file).
    /// Falls back to AppContext.BaseDirectory parent if GetCurrentDirectory doesn't work.
    /// </summary>
    public static readonly string BaseDir = ResolveProjectRoot();

    public static readonly string DataDir = Path.Combine(BaseDir, "data");
    public static readonly string CacheBaseDir = Path.Combine(DataDir, "cache");

    public const string BucketUploads = "uploads";
    public const string BucketOcr = "ocr";
    public const string BucketQas = "qas";

    // Expiration settings (in minutes)
    public const int OcrCacheExpirationMinutes = 30;
    public const int DefaultCacheExpirationMinutes = 1440; // 24 hours

    static FileUtils()
    {
        EnsureDirectories();
    }

    private static string ResolveProjectRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();

        // If .csproj exists, we're at project root
        if (File.Exists(Path.Combine(currentDir, "MarkdownGenQAs.csproj")))
        {
            return currentDir;
        }

        // Otherwise, walk up from bin/Debug/net9.0/ to find project root
        var searchDir = currentDir;
        for (int i = 0; i < 5; i++)
        {
            if (File.Exists(Path.Combine(searchDir, "MarkdownGenQAs.csproj")))
            {
                return searchDir;
            }
            searchDir = Path.GetDirectoryName(searchDir);
            if (string.IsNullOrEmpty(searchDir)) break;
        }

        // Last resort: use current directory
        return currentDir;
    }

    private static void EnsureDirectories()
    {
        if (!Directory.Exists(CacheBaseDir)) Directory.CreateDirectory(CacheBaseDir);
        foreach (var bucket in new[] { BucketUploads, BucketOcr, BucketQas })
        {
            var path = Path.Combine(CacheBaseDir, bucket);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }
    }

    public static string GetCachePath(Guid documentId, string bucket, string extension)
    {
        return Path.Combine(CacheBaseDir, bucket, $"{documentId}{extension}");
    }

    public static bool CacheExists(Guid documentId, string bucket, string extension)
    {
        return File.Exists(GetCachePath(documentId, bucket, extension));
    }

    public static Stream? GetCacheContent(Guid documentId, string bucket, string extension)
    {
        var path = GetCachePath(documentId, bucket, extension);
        if (!File.Exists(path)) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Save stream to cache bucket with expiration tracking
    /// </summary>
    public static async Task SaveToCacheAsync(Guid documentId, string bucket, string extension, Stream inputStream)
    {
        await SaveToCacheAsync(documentId, bucket, extension, inputStream, DefaultCacheExpirationMinutes);
    }

    /// <summary>
    /// Save stream to cache bucket with custom expiration time
    /// </summary>
    public static async Task SaveToCacheAsync(Guid documentId, string bucket, string extension, Stream inputStream, int expirationMinutes)
    {
        var filePath = GetCachePath(documentId, bucket, extension);

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

        if (inputStream.CanSeek)
        {
            var originalPosition = inputStream.Position;
            inputStream.Position = 0;
            await inputStream.CopyToAsync(fileStream);
            inputStream.Position = originalPosition;
        }
        else
        {
            await inputStream.CopyToAsync(fileStream);
        }

        // Set file creation time to now (used for expiration check)
        File.SetCreationTimeUtc(filePath, DateTime.UtcNow);
    }

    /// <summary>
    /// Cleanup files older than their expiration time
    /// </summary>
    public static void CleanupOldCache()
    {
        var dirInfo = new DirectoryInfo(CacheBaseDir);
        if (!dirInfo.Exists) return;

        var now = DateTime.UtcNow;

        foreach (var bucketDir in dirInfo.GetDirectories())
        {
            var expirationMinutes = bucketDir.Name switch
            {
                BucketOcr => OcrCacheExpirationMinutes,
                _ => DefaultCacheExpirationMinutes
            };

            foreach (var file in bucketDir.GetFiles())
            {
                var age = now - File.GetCreationTimeUtc(file.FullName);
                if (age > TimeSpan.FromMinutes(expirationMinutes))
                {
                    file.Delete();
                }
            }
        }
    }
}
