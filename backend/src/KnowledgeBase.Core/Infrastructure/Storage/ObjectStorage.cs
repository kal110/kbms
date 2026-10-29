using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Storage;

/// <summary>
/// Object storage for original PDFs and derived artifacts (frame 7): S3-compatible storage with
/// presigned URLs (<see cref="S3ObjectStorage"/>), or the local disk store below, which behaves the
/// same way by issuing short-lived signed URLs that the API serves.
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<long?> GetSizeAsync(string key, CancellationToken ct = default);

    /// <summary>A URL the browser can PUT the bytes to until it expires.</summary>
    PresignedUrl CreateUploadUrl(string key, string contentType, TimeSpan ttl);

    /// <summary>A URL the browser can GET the bytes from until it expires (5 minutes in the design).</summary>
    PresignedUrl CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan ttl, bool inline = false);

    /// <summary>Prepares the store before first use (S3: creates the bucket when configured to).</summary>
    Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <param name="Headers">Headers the caller must send with the request, because they are part of the signature.</param>
public sealed record PresignedUrl(string Url, string Method, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string>? Headers = null);

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"Local" (files on disk, served by the API) or "S3" (any S3-compatible store).</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>Root folder of the local store. Relative paths resolve against the content root.</summary>
    public string LocalRoot { get; set; } = "storage";

    /// <summary>Public base URL of the API, used to build signed local URLs. Empty = relative URLs.</summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>HMAC key for signed local URLs. Set per environment; never commit a real one.</summary>
    public string SigningKey { get; set; } = "";

    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class S3StorageOptions
{
    /// <summary>Endpoint the API uses, e.g. http://s3:9000 inside Docker. Empty = AWS S3 in <see cref="Region"/>.</summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>
    /// Endpoint the browser uses for presigned URLs, e.g. http://localhost:9000. The host is part of
    /// the signature, so it must be the address browsers reach. Empty = <see cref="ServiceUrl"/>.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "kbms";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    /// <summary>Path-style URLs (http://host/bucket/key), which MinIO-style stores need.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Create the bucket on startup if it doesn't exist.</summary>
    public bool CreateBucket { get; set; }
}

/// <summary>What a signed local URL grants: one operation on one key until it expires.</summary>
public sealed record StorageGrant(string Operation, string Key, string? FileName, string? ContentType, bool Inline, DateTimeOffset ExpiresAt);

/// <summary>Signs and verifies the tokens in local storage URLs.</summary>
public sealed class StorageUrlSigner(IOptions<StorageOptions> options, TimeProvider clock)
{
    private byte[] Key => Encoding.UTF8.GetBytes(string.IsNullOrEmpty(options.Value.SigningKey)
        ? throw new InvalidOperationException("Storage:SigningKey is not configured.")
        : options.Value.SigningKey);

    public string Sign(StorageGrant grant)
    {
        var payload = string.Join('\n', grant.Operation, grant.Key, grant.FileName ?? "", grant.ContentType ?? "",
            grant.Inline ? "1" : "0", grant.ExpiresAt.ToUnixTimeSeconds().ToString());
        var body = Base64Url(Encoding.UTF8.GetBytes(payload));
        var mac = Base64Url(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(body)));
        return $"{body}.{mac}";
    }

    /// <summary>Returns the grant when the token is authentic and unexpired; null otherwise.</summary>
    public StorageGrant? Verify(string token, string operation)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        var expected = Base64Url(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(parts[0])));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1]))) return null;

        string[] fields;
        try { fields = Encoding.UTF8.GetString(FromBase64Url(parts[0])).Split('\n'); }
        catch (FormatException) { return null; }
        if (fields.Length != 6 || fields[0] != operation || !long.TryParse(fields[5], out var exp)) return null;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (expiresAt < clock.GetUtcNow()) return null;
        return new StorageGrant(fields[0], fields[1], NullIfEmpty(fields[2]), NullIfEmpty(fields[3]), fields[4] == "1", expiresAt);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

/// <summary>Development store on local disk. Keys map to paths under <see cref="StorageOptions.LocalRoot"/>.</summary>
public sealed class LocalObjectStorage(IOptions<StorageOptions> options, StorageUrlSigner signer, TimeProvider clock, string contentRoot)
    : IObjectStorage
{
    public const string UploadOperation = "put";
    public const string DownloadOperation = "get";

    private string Root => Path.GetFullPath(Path.Combine(contentRoot, options.Value.LocalRoot));

    private string PathFor(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(key)) throw new ArgumentException("Invalid object key.", nameof(key));
        var full = Path.GetFullPath(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? full : throw new ArgumentException("Invalid object key.", nameof(key));
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Objects are immutable: write to a temp file, then move into place only if the key is new.
        var temp = path + ".part-" + Guid.NewGuid().ToString("N");
        await using (var file = File.Create(temp)) await content.CopyToAsync(file, ct);
        try { File.Move(temp, path, overwrite: false); }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            throw new InvalidOperationException($"Object '{key}' already exists and objects are immutable.");
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(PathFor(key)));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(key)));

    public Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        var info = new FileInfo(PathFor(key));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public PresignedUrl CreateUploadUrl(string key, string contentType, TimeSpan ttl)
    {
        var expires = clock.GetUtcNow().Add(ttl);
        var token = signer.Sign(new StorageGrant(UploadOperation, key, null, contentType, false, expires));
        return new PresignedUrl($"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/storage/objects?token={Uri.EscapeDataString(token)}", "PUT", expires);
    }

    public PresignedUrl CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan ttl, bool inline = false)
    {
        var expires = clock.GetUtcNow().Add(ttl);
        var token = signer.Sign(new StorageGrant(DownloadOperation, key, fileName, contentType, inline, expires));
        return new PresignedUrl($"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/storage/objects?token={Uri.EscapeDataString(token)}", "GET", expires);
    }
}
