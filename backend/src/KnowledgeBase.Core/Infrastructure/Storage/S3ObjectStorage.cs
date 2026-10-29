using System.Net;
using System.Net.Http.Headers;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Storage;

/// <summary>
/// S3-compatible store (AWS S3, RustFS, MinIO, SeaweedFS…). Browsers upload and download with
/// presigned URLs, so file bytes never pass through the API.
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly S3StorageOptions options;
    private readonly AmazonS3Client client;
    // Presigning is offline, so a second client pointed at the public endpoint signs URLs for the
    // host browsers use (localhost:9000) while the API itself talks to the internal one (s3:9000).
    private readonly AmazonS3Client presigner;
    private readonly TimeProvider clock;

    public S3ObjectStorage(IOptions<StorageOptions> storage, TimeProvider clock)
    {
        options = storage.Value.S3;
        this.clock = clock;
        if (string.IsNullOrWhiteSpace(options.Bucket)) throw new InvalidOperationException("Storage:S3:Bucket is not configured.");

        client = CreateClient(options.ServiceUrl);
        presigner = string.IsNullOrWhiteSpace(options.PublicUrl) ? client : CreateClient(options.PublicUrl);
    }

    private AmazonS3Client CreateClient(string serviceUrl)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // S3-compatible stores don't all accept the SDK's default CRC trailers; send checksums only when required.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (string.IsNullOrWhiteSpace(serviceUrl)) config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        else
        {
            config.ServiceURL = serviceUrl;
            config.AuthenticationRegion = options.Region;
        }
        return string.IsNullOrEmpty(options.AccessKey)
            ? new AmazonS3Client(config) // AWS default chain: environment, profile, instance role
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!options.CreateBucket) return;
        try
        {
            if (await AmazonS3Util.DoesS3BucketExistV2Async(client, options.Bucket)) return;
            await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException(
                $"S3 storage at {options.ServiceUrl} isn't reachable (Storage:S3:ServiceUrl). Start it (from the repository root: " +
                "docker compose up -d s3) or set Storage:Provider to Local.", e);
        }
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        // The SDK needs the length up front; copies from another object arrive as a plain network stream.
        await using var buffered = content.CanSeek ? null : await BufferAsync(content, ct);
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = key,
                InputStream = buffered ?? content,
                AutoCloseStream = false,
                ContentType = contentType,
                // Objects are immutable, as in the local store: refuse to replace an existing key.
                IfNoneMatch = "*",
            }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException($"Object '{key}' already exists and objects are immutable.", e);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetObjectAsync(options.Bucket, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Same exception as the local store, so the processing worker reports "missing from storage".
            throw new FileNotFoundException($"Object '{key}' was not found.", key, e);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) => await GetSizeAsync(key, ct) is not null;

    public async Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        try { return (await client.GetObjectMetadataAsync(options.Bucket, key, ct)).ContentLength; }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public PresignedUrl CreateUploadUrl(string key, string contentType, TimeSpan ttl)
    {
        var request = Presign(key, HttpVerb.PUT, ttl);
        // Both are signed, so the browser must send them (the upload ticket lists them): the announced
        // type, and If-None-Match so the link can't replace the file once it's there.
        request.ContentType = contentType;
        request.Headers["If-None-Match"] = "*";
        return new PresignedUrl(presigner.GetPreSignedURL(request), "PUT", ExpiresAt(request),
            new Dictionary<string, string> { ["If-None-Match"] = "*" });
    }

    public PresignedUrl CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan ttl, bool inline = false)
    {
        var request = Presign(key, HttpVerb.GET, ttl);
        request.ResponseHeaderOverrides.ContentType = contentType;
        request.ResponseHeaderOverrides.ContentDisposition = Disposition(fileName, inline);
        return new PresignedUrl(presigner.GetPreSignedURL(request), "GET", ExpiresAt(request));
    }

    private GetPreSignedUrlRequest Presign(string key, HttpVerb verb, TimeSpan ttl) => new()
    {
        BucketName = options.Bucket,
        Key = key,
        Verb = verb,
        Expires = clock.GetUtcNow().Add(ttl).UtcDateTime,
        // GetPreSignedURL defaults to https; follow the endpoint's scheme so local http endpoints work.
        Protocol = (string.IsNullOrWhiteSpace(options.PublicUrl) ? options.ServiceUrl : options.PublicUrl)
            .StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS,
    };

    private static DateTimeOffset ExpiresAt(GetPreSignedUrlRequest request) => new(request.Expires!.Value, TimeSpan.Zero);

    /// <summary>`inline; filename="Syllabus_2026.pdf"; filename*=utf-8''Syllabus%202026.pdf`, like the local store sends.</summary>
    private static string Disposition(string fileName, bool inline)
    {
        var ascii = new string(fileName.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
        return new ContentDispositionHeaderValue(inline ? "inline" : "attachment") { FileName = $"\"{ascii}\"", FileNameStar = fileName }.ToString();
    }

    private static async Task<FileStream> BufferAsync(Stream content, CancellationToken ct)
    {
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await content.CopyToAsync(file, ct);
        file.Position = 0;
        return file;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(presigner, client)) presigner.Dispose();
        client.Dispose();
    }
}
