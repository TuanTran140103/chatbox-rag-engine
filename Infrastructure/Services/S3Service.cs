using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Options;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Serilog;
using System.Net;
using System.Text;

namespace MarkdownGenQAs.Infrastructure.Services;

public class S3Service : IS3Service
{
    private readonly IAmazonS3 _s3Client;
    private readonly MinioOptions _minioOptions;
    private readonly AsyncRetryPolicy _retryPolicy;

    public S3Service(IAmazonS3 s3Client, IOptions<MinioOptions> minioOptions)
    {
        _s3Client = s3Client;
        _minioOptions = minioOptions.Value;

        _retryPolicy = Policy
            .Handle<AmazonS3Exception>(ex => ex.StatusCode == HttpStatusCode.InternalServerError || ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            .Or<TimeoutException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    Log.Warning("Retry {RetryCount} for S3 operation due to {ExceptionMessage}", retryCount, exception.Message);
                });
    }

    public async Task InitializeBucketsAsync()
    {
        var privateBuckets = new[]
        {
            S3BucketName.OCRUploadPdf,
            S3BucketName.OCRResultsMarkdown,
            S3BucketName.ChunkQa
        };

        foreach (var bucket in privateBuckets)
        {
            var isNew = await EnsureBucketExistsAsync(bucket, false);
            if (isNew)
                await SetPrivateBucketPolicyAsync(bucket);
        }

        // Public bucket for images
        var isNewPublic = await EnsureBucketExistsAsync(S3BucketName.PublicImages, true);
        if (isNewPublic)
            await SetPublicReadBucketPolicyAsync(S3BucketName.PublicImages);
    }

    private async Task<bool> EnsureBucketExistsAsync(string bucketName, bool isPublic = false)
    {
        try
        {
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, bucketName))
            {
                Log.Information("Creating bucket {BucketName}...", bucketName);
                var putBucketRequest = new PutBucketRequest
                {
                    BucketName = bucketName,
                    CannedACL = isPublic ? S3CannedACL.PublicRead : S3CannedACL.Private
                };
                await _s3Client.PutBucketAsync(putBucketRequest);
                Log.Information("Bucket {BucketName} created successfully.", bucketName);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking or creating bucket {BucketName}", bucketName);
            throw;
        }
    }

    private async Task SetPrivateBucketPolicyAsync(string bucketName)
    {
        try
        {
            Log.Information("Setting private policy for bucket {BucketName}...", bucketName);
            // S3 buckets are private by default, but we can explicitly set a policy if needed.
            // For MinIO/S3, setting CannedACL.Private during creation is usually enough.
            // If we want a strict JSON policy:
            var emptyPolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": []
            }";

            await _s3Client.PutBucketPolicyAsync(new PutBucketPolicyRequest
            {
                BucketName = bucketName,
                Policy = emptyPolicy
            });

            Log.Information("Bucket {BucketName} set to private via empty policy", bucketName);

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not set strict public access block for {BucketName}. This might be a MinIO version limitation.", bucketName);
        }
    }

    private async Task SetPublicReadBucketPolicyAsync(string bucketName)
    {
        try
        {
            Log.Information("Setting public-read policy for bucket {BucketName}...", bucketName);
            var publicReadPolicy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [
                    {{
                        ""Sid"": ""PublicReadGetObject"",
                        ""Effect"": ""Allow"",
                        ""Principal"": ""*"",
                        ""Action"": ""s3:GetObject"",
                        ""Resource"": ""arn:aws:s3:::{bucketName}/*""
                    }}
                ]
            }}";

            await _s3Client.PutBucketPolicyAsync(new PutBucketPolicyRequest
            {
                BucketName = bucketName,
                Policy = publicReadPolicy
            });

            Log.Information("Bucket {BucketName} set to public-read", bucketName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not set public-read policy for {BucketName}", bucketName);
        }
    }

    /// <summary>
    /// Upload file to S3
    /// </summary>
    /// <param name="fileStream"></param>
    /// <param name="fileName"></param>
    /// <param name="bucketName"></param>
    /// <param name="contentType"></param>
    /// <returns>return object key do not include bucket name, objectKey is normalized</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string bucketName, string contentType = "application/octet-stream")
    {
        if (fileStream == null) throw new ArgumentNullException(nameof(fileStream));

        fileName = S3Helper.NormalizeObjectKey(fileName);

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = fileName,
                InputStream = fileStream,
                ContentType = contentType,
                CannedACL = S3CannedACL.Private
            };

            await _s3Client.PutObjectAsync(request);
            Log.Information("Uploaded {FileName} to {BucketName}", fileName, bucketName);
            return fileName;
        });
    }

    public async Task<Stream?> DownloadFileAsync(string objectKey, string bucketName)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            try
            {
                objectKey = S3Helper.NormalizeObjectKey(objectKey);
                var request = new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = objectKey
                };

                var response = await _s3Client.GetObjectAsync(request);
                return response.ResponseStream;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Warning("File {FileName} not found in {BucketName}", objectKey, bucketName);
                return null;
            }
        });
    }

    public async Task<bool> FileExistsAsync(string objectKey, string bucketName)
    {
        try
        {
            objectKey = S3Helper.NormalizeObjectKey(objectKey);
            var request = new GetObjectMetadataRequest
            {
                BucketName = bucketName,
                Key = objectKey
            };

            await _s3Client.GetObjectMetadataAsync(request);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking file existence {FileName} in {BucketName}", objectKey, bucketName);
            throw;
        }
    }

    public async Task<bool> DeleteFileAsync(string objectKey, string bucketName)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            try
            {
                objectKey = S3Helper.NormalizeObjectKey(objectKey);
                var request = new DeleteObjectRequest
                {
                    BucketName = bucketName,
                    Key = objectKey
                };

                await _s3Client.DeleteObjectAsync(request);
                Log.Information("Deleted {FileName} from {BucketName}", objectKey, bucketName);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Warning("File {FileName} not found in {BucketName} for deletion", objectKey, bucketName);
                return false;
            }
        });
    }

    public async Task<string?> GetFileContentAsync(string objectKey, string bucketName)
    {
        using var stream = await DownloadFileAsync(objectKey, bucketName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public async Task<bool> DeleteMultipleFilesAsync(IEnumerable<(string ObjectKey, string BucketName)> files)
    {
        if (files == null || !files.Any()) return true;

        var fileList = files.Where(f => !string.IsNullOrEmpty(f.ObjectKey)).ToList();
        if (!fileList.Any()) return true;

        try
        {
            Log.Information("Starting parallel deletion of {Count} files", fileList.Count);
            var deleteTasks = fileList.Select(f => DeleteFileAsync(f.ObjectKey, f.BucketName));
            await Task.WhenAll(deleteTasks);

            Log.Information("Parallel deletion of {Count} files completed successfully", fileList.Count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred during parallel file deletion");
            return false;
        }
    }

    public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string bucketName, string contentType, TimeSpan expiration)
    {
        objectKey = S3Helper.NormalizeObjectKey(objectKey);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expiration),
            ContentType = contentType,
            Protocol = Protocol.HTTP
        };

        var url = _s3Client.GetPreSignedURL(request);
        url = ReplaceHostWithPublicEndpoint(url);
        return Task.FromResult(url);
    }

    public async Task<(long ContentLength, string? ContentType)> GetFileMetadataAsync(string objectKey, string bucketName)
    {
        objectKey = S3Helper.NormalizeObjectKey(objectKey);
        var request = new GetObjectMetadataRequest
        {
            BucketName = bucketName,
            Key = objectKey
        };

        var response = await _s3Client.GetObjectMetadataAsync(request);
        return (response.ContentLength, response.Headers.ContentType);
    }

    public async Task<byte[]> ReadFileHeadAsync(string objectKey, string bucketName, int byteCount)
    {
        objectKey = S3Helper.NormalizeObjectKey(objectKey);
        var request = new GetObjectRequest
        {
            BucketName = bucketName,
            Key = objectKey,
            ByteRange = new ByteRange(0, byteCount - 1)
        };

        using var response = await _s3Client.GetObjectAsync(request);
        using var memoryStream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }

    public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, string bucketName, TimeSpan expiration)
    {
        objectKey = S3Helper.NormalizeObjectKey(objectKey);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiration),
            Protocol = Protocol.HTTP
        };

        var url = _s3Client.GetPreSignedURL(request);
        url = ReplaceHostWithPublicEndpoint(url);
        return Task.FromResult(url);
    }

    private string ReplaceHostWithPublicEndpoint(string url)
    {
        if (string.IsNullOrEmpty(_minioOptions.PublicEndpoint))
            return url;

        try
        {
            var originalUri = new Uri(url);
            var publicUri = new UriBuilder(_minioOptions.PublicEndpoint);
            publicUri.Path = publicUri.Path.TrimEnd('/') + originalUri.AbsolutePath;
            publicUri.Query = originalUri.Query;
            return publicUri.ToString();
        }
        catch
        {
            return url;
        }
    }
}
