using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using MarkdownGenQAs.Application.Dto.Admin;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure;
using MarkdownGenQAs.Models.Entities;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Application.Service;

public class OrphanFileCleanupService : IOrphanFileCleanupService
{
    private readonly IS3Service _s3Service;
    private readonly ApplicationContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<OrphanFileCleanupService> _logger;
    private readonly IAmazonS3 _s3Client;

    public OrphanFileCleanupService(
        IS3Service s3Service,
        ApplicationContext context,
        IUnitOfWork uow,
        IAmazonS3 s3Client,
        ILogger<OrphanFileCleanupService> logger)
    {
        _s3Service = s3Service;
        _context = context;
        _uow = uow;
        _s3Client = s3Client;
        _logger = logger;
    }

    public async Task<List<OrphanFileDto>> GetOrphanFilesAsync(CancellationToken ct = default)
    {
        var orphanFiles = new List<OrphanFileDto>();

        var buckets = new[] { S3BucketName.OCRUploadPdf, S3BucketName.OCRResultsMarkdown, S3BucketName.ChunkQa };

        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var documents = await _context.Documents
            .Where(d => !string.IsNullOrEmpty(d.ObjectKeyFilePdf))
            .Select(d => d.ObjectKeyFilePdf!)
            .ToListAsync(ct);

        foreach (var key in documents)
        {
            validKeys.Add(key);
            validKeys.Add(Path.GetFileName(key));
        }

        foreach (var bucket in buckets)
        {
            try
            {
                var request = new ListObjectsV2Request
                {
                    BucketName = bucket,
                    MaxKeys = 1000
                };

                do
                {
                    var response = await _s3Client.ListObjectsV2Async(request, ct);

                    foreach (var obj in response.S3Objects)
                    {
                        var key = obj.Key;
                        var keyName = Path.GetFileName(key);

                        if (!validKeys.Contains(key) && !validKeys.Contains(keyName))
                        {
                            orphanFiles.Add(new OrphanFileDto
                            {
                                ObjectKey = key,
                                BucketName = bucket,
                                SizeBytes = obj.Size ?? 0,
                                LastModified = (obj.LastModified ?? DateTime.UtcNow).ToUniversalTime()
                            });
                        }
                    }

                    request.ContinuationToken = response.NextContinuationToken;
                } while (request.ContinuationToken != null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing objects in bucket {Bucket}", bucket);
            }
        }

        return orphanFiles;
    }

    public async Task<OrphanCleanupResultDto> CleanupOrphanFilesAsync(CancellationToken ct = default)
    {
        var result = new OrphanCleanupResultDto();
        var orphanFiles = await GetOrphanFilesAsync(ct);

        foreach (var file in orphanFiles)
        {
            try
            {
                var deleted = await _s3Service.DeleteFileAsync(file.ObjectKey, file.BucketName);
                if (deleted)
                {
                    result.DeletedCount++;
                    result.DeletedFiles.Add($"{file.BucketName}/{file.ObjectKey}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting orphan file {Bucket}/{Key}", file.BucketName, file.ObjectKey);
                result.Errors.Add($"{file.BucketName}/{file.ObjectKey}: {ex.Message}");
            }
        }

        _logger.LogInformation("Orphan file cleanup completed: {Deleted} deleted, {Errors} errors",
            result.DeletedCount, result.Errors.Count);

        return result;
    }

    public async Task<int> CleanupStuckUploadingDocumentsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);

        var stuckDocuments = await _context.Documents
            .Where(d => d.Status == StatusDocument.Uploading && d.CreatedAt < cutoff)
            .ToListAsync(ct);

        var cleaned = 0;

        foreach (var doc in stuckDocuments)
        {
            try
            {
                if (!string.IsNullOrEmpty(doc.ObjectKeyFilePdf))
                {
                    await _s3Service.DeleteFileAsync(doc.ObjectKeyFilePdf, S3BucketName.OCRUploadPdf);
                }

                _context.Documents.Remove(doc);
                cleaned++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up stuck document {Id}", doc.Id);
            }
        }

        if (cleaned > 0)
        {
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Cleaned up {Count} stuck upload documents", cleaned);
        }

        return cleaned;
    }
}
