using System.IO;
using System.Threading.Tasks;

namespace MarkdownGenQAs.Helper;

/// <summary>
/// Helper for cache operations. Uses FileUtils.BaseDir which points to project root,
/// not the bin/ directory that gets regenerated on each build.
/// </summary>
public static class DocumentHelper
{
    // Delegate all path operations to FileUtils
    public static readonly string CacheBaseDir = Utils.FileUtils.CacheBaseDir;

    public const string BucketUploads = Utils.FileUtils.BucketUploads;
    public const string BucketOcr = Utils.FileUtils.BucketOcr;
    public const string BucketQas = Utils.FileUtils.BucketQas;

    public const int OcrCacheExpirationMinutes = Utils.FileUtils.OcrCacheExpirationMinutes;
    public const int DefaultCacheExpirationMinutes = Utils.FileUtils.DefaultCacheExpirationMinutes;

    static DocumentHelper()
    {
        EnsureDirectories();
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

    /// <summary>
    /// Save stream to cache bucket synchronously (relative to S3 upload)
    /// Default expiration: 24 hours
    /// </summary>
    public static async Task SaveToCacheAsync(Guid documentId, string bucket, string extension, Stream inputStream)
    {
        await SaveToCacheAsync(documentId, bucket, extension, inputStream, DefaultCacheExpirationMinutes);
    }

    /// <summary>
    /// Save stream to cache bucket with custom expiration time
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <param name="bucket">Cache bucket name</param>
    /// <param name="extension">File extension</param>
    /// <param name="inputStream">Input stream to save</param>
    /// <param name="expirationMinutes">Expiration time in minutes. File will be deleted after this time.</param>
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

    public static bool Exists(Guid documentId, string bucket, string extension)
    {
        return File.Exists(GetCachePath(documentId, bucket, extension));
    }

    public static Stream? GetContent(Guid documentId, string bucket, string extension)
    {
        var path = GetCachePath(documentId, bucket, extension);
        if (!File.Exists(path)) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Cleanup files older than their expiration time
    /// </summary>
    public static void CleanupOldCache()
    {
        var dirInfo = new DirectoryInfo(CacheBaseDir);

        if (!dirInfo.Exists) return;

        foreach (var bucketDir in dirInfo.GetDirectories())
        {
            // Determine expiration time based on bucket
            var expiration = bucketDir.Name switch
            {
                BucketOcr => DateTime.UtcNow.AddMinutes(-OcrCacheExpirationMinutes),
                _ => DateTime.UtcNow.AddMinutes(-DefaultCacheExpirationMinutes)
            };

            foreach (var file in bucketDir.GetFiles())
            {
                // Use CreationTimeUtc to check age
                if (file.CreationTimeUtc < expiration)
                {
                    try { file.Delete(); } catch { /* ignore in-use files */ }
                }
            }
        }
    }

    #region OCR Cache Helpers

    /// <summary>
    /// Check if OCR cache file exists for a document
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <returns>True if OCR cache exists and is not expired</returns>
    public static bool HasOcrCache(Guid documentId)
    {
        var path = GetOcrCachePath(documentId);
        if (!File.Exists(path)) return false;

        // Check if file is expired
        var file = new FileInfo(path);
        var expiration = DateTime.UtcNow.AddMinutes(-OcrCacheExpirationMinutes);
        return file.CreationTimeUtc >= expiration;
    }

    /// <summary>
    /// Get OCR cache file path for a document
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <returns>Full path to OCR cache file</returns>
    public static string GetOcrCachePath(Guid documentId)
    {
        return GetCachePath(documentId, BucketOcr, ".md");
    }

    /// <summary>
    /// Get OCR cache content as stream
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <returns>FileStream if exists and not expired, null otherwise</returns>
    public static Stream? GetOcrCacheStream(Guid documentId)
    {
        if (!HasOcrCache(documentId)) return null;
        return GetContent(documentId, BucketOcr, ".md");
    }

    /// <summary>
    /// Get OCR cache content as string
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <returns>Markdown content if exists and not expired, null otherwise</returns>
    public static async Task<string?> GetOcrCacheContentAsync(Guid documentId)
    {
        if (!HasOcrCache(documentId)) return null;

        var path = GetOcrCachePath(documentId);
        return await File.ReadAllTextAsync(path);
    }

    /// <summary>
    /// Delete OCR cache file for a document
    /// </summary>
    /// <param name="documentId">Document ID</param>
    /// <returns>True if deleted, false if file doesn't exist</returns>
    public static bool DeleteOcrCache(Guid documentId)
    {
        var path = GetOcrCachePath(documentId);
        if (!File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}

