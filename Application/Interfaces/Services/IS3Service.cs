namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface IS3Service
{
    Task InitializeBucketsAsync();

    Task<string> UploadFileAsync(Stream fileStream, string fileName, string bucketName, string contentType = "application/octet-stream");
    Task<Stream?> DownloadFileAsync(string objectKey, string bucketName);
    Task<string?> GetFileContentAsync(string objectKey, string bucketName);
    Task<bool> FileExistsAsync(string objectKey, string bucketName);
    Task<bool> DeleteFileAsync(string objectKey, string bucketName);
    Task<bool> DeleteMultipleFilesAsync(IEnumerable<(string ObjectKey, string BucketName)> files);
}
