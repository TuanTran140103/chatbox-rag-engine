using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MarkdownGenQAs.Infrastructure.ExternalServices;

internal sealed class MinioAdminClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly HttpClient _httpClient;

    private const string Service = "s3";
    private const string Region = "us-east-1";

    public MinioAdminClient(string serviceUrl, string accessKey, string secretKey)
    {
        _baseUrl = serviceUrl.TrimEnd('/');
        _accessKey = accessKey;
        _secretKey = secretKey;
        _httpClient = new HttpClient();
    }

    public string GetEndpoint() => _baseUrl;

    public Task<HttpResponseMessage> PutAsync(string path, CancellationToken ct)
        => SendAsync(HttpMethod.Put, path, [], null, ct);

    public Task<HttpResponseMessage> PutJsonAsync(string path, string json, CancellationToken ct)
        => SendAsync(HttpMethod.Put, path, Encoding.UTF8.GetBytes(json), "application/json", ct);

    public Task<HttpResponseMessage> PutEncryptedAsync(string path, string jsonBody, CancellationToken ct)
        => SendAsync(HttpMethod.Put, path, EncryptJson(jsonBody, _secretKey), null, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, string? contentType, CancellationToken ct)
    {
        var uri = new Uri(_baseUrl + path);
        var request = new HttpRequestMessage(method, uri);

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType != null)
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        request = SignV4(request, body);
        return await _httpClient.SendAsync(request, ct);
    }

    private HttpRequestMessage SignV4(HttpRequestMessage request, byte[] body)
    {
        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);

        var payloadHash = HexSHA256(body);
        var uri = request.RequestUri!;
        var host = uri.Host + (uri.Port == 80 || uri.Port == 443 ? "" : $":{uri.Port}");

        var canonicalUri = uri.AbsolutePath;
        var canonicalQueryString = SortQueryString(uri.Query.TrimStart('?'));

        var signedHeadersList = new[] { "host", "x-amz-content-sha256", "x-amz-date" };
        var signedHeaders = string.Join(";", signedHeadersList);

        var canonicalHeaders =
            $"host:{host}\n" +
            $"x-amz-content-sha256:{payloadHash}\n" +
            $"x-amz-date:{amzDate}\n";

        var canonicalRequest =
            $"{request.Method.Method}\n" +
            $"{canonicalUri}\n" +
            $"{canonicalQueryString}\n" +
            $"{canonicalHeaders}\n" +
            $"{signedHeaders}\n" +
            $"{payloadHash}";

        var algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/{Region}/{Service}/aws4_request";
        var stringToSign =
            $"{algorithm}\n" +
            $"{amzDate}\n" +
            $"{credentialScope}\n" +
            $"{HexSHA256(canonicalRequest)}";

        var signingKey = GetSignatureKey(_secretKey, dateStamp);
        var signature = HmacHex(signingKey, stringToSign);

        var authorization =
            $"{algorithm} Credential={_accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        return request;
    }

    internal static byte[] EncryptJson(string json, string password)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);

        byte[] key;
        using (var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password)))
        {
            argon2.Salt = salt;
            argon2.DegreeOfParallelism = 4;
            argon2.MemorySize = 64 * 1024;
            argon2.Iterations = 1;
            key = argon2.GetBytes(32);
        }

        var noncePrefix = new byte[8];
        RandomNumberGenerator.Fill(noncePrefix);

        using var aes = new AesGcm(key, 16);

        var nonceSeq0 = new byte[12];
        Buffer.BlockCopy(noncePrefix, 0, nonceSeq0, 0, 8);

        var tag0 = new byte[16];
        aes.Encrypt(nonceSeq0, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag0);

        var aad = new byte[1 + 16];
        aad[0] = 0x80;
        Buffer.BlockCopy(tag0, 0, aad, 1, 16);

        var nonceSeq1 = new byte[12];
        Buffer.BlockCopy(noncePrefix, 0, nonceSeq1, 0, 8);
        nonceSeq1[8] = 0x01;

        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        aes.Encrypt(nonceSeq1, data, ciphertext, tag, aad);

        var result = new byte[32 + 1 + 8 + data.Length + 16];
        Buffer.BlockCopy(salt, 0, result, 0, 32);
        result[32] = 0x00;
        Buffer.BlockCopy(noncePrefix, 0, result, 33, 8);
        Buffer.BlockCopy(ciphertext, 0, result, 41, data.Length);
        Buffer.BlockCopy(tag, 0, result, 41 + data.Length, 16);

        return result;
    }

    private static string SortQueryString(string query)
    {
        if (string.IsNullOrEmpty(query))
            return "";

        return string.Join("&",
            query.Split('&')
                .Select(p => p.Split(['='], 2))
                .OrderBy(kv => kv[0], StringComparer.Ordinal)
                .Select(kv => kv.Length == 2 ? $"{kv[0]}={kv[1]}" : kv[0]));
    }

    private static string HexSHA256(byte[] data) => ConvertToHex(SHA256.HashData(data));

    private static string HexSHA256(string data) => HexSHA256(Encoding.UTF8.GetBytes(data));

    private static byte[] GetSignatureKey(string key, string dateStamp)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{key}"), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(Region));
        var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(Service));
        return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string HmacHex(byte[] key, string data) => ConvertToHex(HmacSha256(key, Encoding.UTF8.GetBytes(data)));

    private static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    private static string ConvertToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public void Dispose() => _httpClient.Dispose();
}
