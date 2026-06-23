using System.Text;
using Amazon;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Infrastructure.ExternalServices;
using MarkdownGenQAs.Options;
using Microsoft.Extensions.Options;

namespace MarkdownGenQAs.Infrastructure.Services;

public class MinioAdminService : IMinioAdminService
{
    private readonly MinioOptions _minioOptions;
    private readonly ILogger<MinioAdminService> _logger;
    private readonly IAmazonS3 _s3Client;
    private readonly MinioAdminClient _adminClient;

    public MinioAdminService(
        IOptions<MinioOptions> minioOptions,
        IAmazonS3 s3Client,
        IConfiguration configuration,
        ILogger<MinioAdminService> logger)
    {
        _minioOptions = minioOptions.Value;
        _s3Client = s3Client;
        _logger = logger;

        var awsSection = configuration.GetSection("AWS");
        var serviceUrl = awsSection.GetValue<string>("ServiceURL") ?? "http://localhost:9000";

        var profileName = awsSection.GetValue<string>("Profile") ?? "minio";
        var profilesLocation = awsSection.GetValue<string>("ProfilesLocation") ?? "";

        var rootAccessKey = "";
        var rootSecretKey = "";

        if (!string.IsNullOrEmpty(profilesLocation) && File.Exists(profilesLocation))
        {
            var sharedFile = new SharedCredentialsFile(profilesLocation);
            if (sharedFile.TryGetProfile(profileName, out var profile)
                && AWSCredentialsFactory.TryGetAWSCredentials(profile, sharedFile, out var credentials))
            {
                var creds = credentials.GetCredentials();
                rootAccessKey = creds.AccessKey;
                rootSecretKey = creds.SecretKey;
            }
            else
            {
                _logger.LogWarning("Could not load AWS credentials from profile '{Profile}' in {Location}",
                    profileName, profilesLocation);
            }
        }
        else
        {
            _logger.LogWarning("AWS credentials file not found at {Location}", profilesLocation);
        }

        _adminClient = new MinioAdminClient(serviceUrl, rootAccessKey, rootSecretKey);
    }

    public async Task EnsureOcrUserAsync(CancellationToken cancellationToken = default)
    {
        var ocrAccessKey = _minioOptions.OcrUser.AccessKey;
        var ocrSecretKey = _minioOptions.OcrUser.SecretKey;

        if (string.IsNullOrEmpty(ocrSecretKey))
        {
            _logger.LogWarning(
                "MinIO OCR user secret key is not configured. Set MinIO__OcrUser__SecretKey in .env. " +
                "Skipping OCR user setup. OCR server may not be able to read documents directly from MinIO.");
            return;
        }

        _logger.LogInformation("Verifying MinIO OCR user '{AccessKey}'...", ocrAccessKey);

        if (await VerifyOcrUserAccessAsync(ocrAccessKey, ocrSecretKey, cancellationToken))
        {
            _logger.LogInformation("MinIO OCR user '{AccessKey}' already exists and has proper access.", ocrAccessKey);
            return;
        }

        _logger.LogInformation("MinIO OCR user '{AccessKey}' not found. Creating via Admin API...", ocrAccessKey);

        try
        {
            await CreateUserAsync(ocrAccessKey, ocrSecretKey, cancellationToken);
            await AttachOcrPolicyAsync(ocrAccessKey, cancellationToken);
            await GrantBucketAccessToOcrUserAsync(ocrAccessKey, cancellationToken);
            _logger.LogInformation("MinIO OCR user '{AccessKey}' created successfully.", ocrAccessKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create MinIO OCR user.");
            _logger.LogWarning(
                "Create the user & bucket policy manually via MinIO Console (port 9001).\n" +
                "User: {User}\n" +
                "Bucket policy for '{Bucket}':\n{Policy}",
                ocrAccessKey, S3BucketName.OCRUploadPdf,
                GetBucketPolicyForUser(S3BucketName.OCRUploadPdf, ocrAccessKey));
        }
    }

    private async Task<bool> VerifyOcrUserAccessAsync(string accessKey, string secretKey, CancellationToken ct)
    {
        try
        {
            var config = new AmazonS3Config
            {
                ServiceURL = _adminClient.GetEndpoint(),
                ForcePathStyle = true,
                AuthenticationRegion = RegionEndpoint.USEast1.SystemName
            };

            using var client = new AmazonS3Client(accessKey, secretKey, config);
            var response = await client.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request
            {
                BucketName = S3BucketName.OCRUploadPdf,
                MaxKeys = 1
            }, ct);

            return response.HttpStatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OCR user verification failed (expected if user doesn't exist yet)");
            return false;
        }
    }

    private async Task CreateUserAsync(string accessKey, string secretKey, CancellationToken ct)
    {
        var jsonBody = $$"""{"secretKey":"{{secretKey}}","status":"enabled"}""";
        var response = await _adminClient.PutEncryptedAsync(
            $"/minio/admin/v3/add-user?accessKey={Uri.EscapeDataString(accessKey)}", jsonBody, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("MinIO Admin API error: {StatusCode} {Reason} - {Body}",
                (int)response.StatusCode, response.ReasonPhrase, errorBody);
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task AttachOcrPolicyAsync(string userAccessKey, CancellationToken ct)
    {
        var policyName = $"{userAccessKey}-policy";
        var policyJson = $$"""
        {
            "Version": "2012-10-17",
            "Statement": [
                {
                    "Effect": "Allow",
                    "Action": ["s3:ListAllMyBuckets"],
                    "Resource": ["arn:aws:s3:::*"]
                },
                {
                    "Effect": "Allow",
                    "Action": ["s3:GetObject", "s3:GetObjectVersion", "s3:ListBucket"],
                    "Resource": [
                        "arn:aws:s3:::{{S3BucketName.OCRUploadPdf}}",
                        "arn:aws:s3:::{{S3BucketName.OCRUploadPdf}}/*"
                    ]
                }
            ]
        }
        """;

        var createResp = await _adminClient.PutJsonAsync(
            $"/minio/admin/v3/add-canned-policy?name={Uri.EscapeDataString(policyName)}", policyJson, ct);

        if (!createResp.IsSuccessStatusCode && createResp.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            var body = await createResp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Failed to create IAM policy: {Code} - {Body}",
                (int)createResp.StatusCode, body);
        }

        var attachResp = await _adminClient.PutAsync(
            $"/minio/admin/v3/set-user-or-group-policy?policyName={Uri.EscapeDataString(policyName)}&userOrGroup={Uri.EscapeDataString(userAccessKey)}&isGroup=false", ct);

        if (attachResp.IsSuccessStatusCode)
        {
            _logger.LogInformation("Attached IAM policy '{Policy}' to user '{User}'.", policyName, userAccessKey);
        }
        else
        {
            var body = await attachResp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Failed to attach IAM policy: {Code} - {Body}",
                (int)attachResp.StatusCode, body);
        }
    }

    private async Task GrantBucketAccessToOcrUserAsync(string userAccessKey, CancellationToken ct)
    {
        var policyJson = GetBucketPolicyForUser(S3BucketName.OCRUploadPdf, userAccessKey);

        var request = new Amazon.S3.Model.PutBucketPolicyRequest
        {
            BucketName = S3BucketName.OCRUploadPdf,
            Policy = policyJson
        };

        await _s3Client.PutBucketPolicyAsync(request, ct);
        _logger.LogInformation("Granted read-only access to user '{User}' on bucket '{Bucket}'", userAccessKey, S3BucketName.OCRUploadPdf);
    }

    private static string GetBucketPolicyForUser(string bucketName, string userAccessKey)
    {
        return $$"""
        {
            "Version": "2012-10-17",
            "Statement": [
                {
                    "Effect": "Allow",
                    "Principal": {
                        "AWS": ["arn:aws:iam:::user/{{userAccessKey}}"]
                    },
                    "Action": [
                        "s3:GetObject",
                        "s3:GetObjectVersion"
                    ],
                    "Resource": [
                        "arn:aws:s3:::{{bucketName}}/*"
                    ]
                },
                {
                    "Effect": "Allow",
                    "Principal": {
                        "AWS": ["arn:aws:iam:::user/{{userAccessKey}}"]
                    },
                    "Action": [
                        "s3:ListBucket"
                    ],
                    "Resource": [
                        "arn:aws:s3:::{{bucketName}}"
                    ]
                }
            ]
        }
        """;
    }
}
