using System.Text;
using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>
/// Direct-to-storage uploads (frame 8): the API hands out a short-lived signed PUT URL, the browser
/// sends the bytes there, and "complete" turns the stored object into a document or a new version.
/// With the local store the signed URL points back at this API (<see cref="ReceiveAsync"/>).
/// </summary>
public sealed partial class UploadService(
    KbDbContext db,
    ICurrentUser currentUser,
    IAccessEvaluator access,
    IObjectStorage storage,
    StorageUrlSigner signer,
    IDocumentFactory factory,
    DocumentService documents,
    TimeProvider clock)
{
    public const long MaxBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    /// <summary>Allowed content types and the file extensions that go with them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedTypes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = [".pdf"],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = [".docx"],
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = [".xlsx"],
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = [".pptx"],
        ["image/png"] = [".png"],
        ["image/jpeg"] = [".jpg", ".jpeg"],
        ["text/plain"] = [".txt"],
    };

    public async Task<UploadTicket> CreateAsync(CreateUploadRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var contentType = request.ContentType?.Split(';')[0].Trim() ?? "";
        var fileName = SafeFileName(request.FileName ?? "");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 300)
            errors["fileName"] = ["Give a file name of at most 300 characters."];
        if (!AllowedTypes.TryGetValue(contentType, out var extensions))
            errors["contentType"] = ["Upload a PDF, Word, Excel, PowerPoint, PNG, JPEG or text file."];
        else if (!extensions.Contains(extension))
            errors["fileName"] = [$"A {contentType} file should end in {string.Join(" or ", extensions)}."];
        if (request.Size <= 0 || request.Size > MaxBytes)
            errors["size"] = [$"Files must be between 1 byte and {MaxBytes / (1024 * 1024)} MB."];
        var sha = request.Sha256?.Trim().ToLowerInvariant();
        if (sha is not null && !Sha256Hex().IsMatch(sha))
            errors["sha256"] = ["Send the SHA-256 as 64 hexadecimal characters."];
        if (errors.Count > 0) throw new ValidationAppException(errors);

        if (request.DocumentId is { } documentId)
        {
            // Fail early for a new version the caller couldn't add anyway.
            var level = await access.GetDocumentAccessAsync(me, documentId, ct);
            level.Require(AccessLevel.Contribute, "Document", documentId);
            var owner = await db.Documents.Where(d => d.Id == documentId).Select(d => d.OwnerUserId).SingleAsync(ct);
            if (level < AccessLevel.Edit && owner != me.UserId)
                throw new ForbiddenException("Only the owner or someone who can edit this document can add a version.");
        }

        var now = clock.GetUtcNow();
        var session = new UploadSession
        {
            UserId = me.UserId,
            DocumentId = request.DocumentId,
            FileName = fileName,
            ContentType = contentType,
            ExpectedSize = request.Size,
            ExpectedSha256 = sha,
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
            ObjectKey = "",
        };
        session.ObjectKey = $"uploads/{now:yyyy}/{now:MM}/{session.Id:N}/{fileName}";
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync(ct);

        var url = storage.CreateUploadUrl(session.ObjectKey, contentType, UrlLifetime);
        var headers = new Dictionary<string, string>(url.Headers ?? new Dictionary<string, string>()) { ["Content-Type"] = contentType };
        return new UploadTicket(session.Id, url.Method, url.Url, headers, url.ExpiresAt, request.Size);
    }

    /// <summary>
    /// The local store's stand-in for an S3 presigned PUT: the token names the key, and the bytes
    /// must match the announced size and type. No sign-in: the signature is the authorization.
    /// </summary>
    public async Task ReceiveAsync(string token, Stream body, long? contentLength, string? contentType, CancellationToken ct = default)
    {
        var grant = signer.Verify(token, LocalObjectStorage.UploadOperation)
            ?? throw new ForbiddenException("The upload link is invalid or has expired.");
        var session = await db.UploadSessions.SingleOrDefaultAsync(s => s.ObjectKey == grant.Key, ct)
            ?? throw new NotFoundException("Upload");
        if (session.Status != UploadStatus.Pending)
            throw new ConflictException("upload-received", "This upload link was already used.");
        if (await storage.ExistsAsync(session.ObjectKey, ct))
            throw new ConflictException("upload-received", "This upload link was already used.");

        var sentType = contentType?.Split(';')[0].Trim();
        if (!string.IsNullOrEmpty(sentType) && grant.ContentType is { } signedType
            && !string.Equals(sentType, signedType, StringComparison.OrdinalIgnoreCase))
            throw new ValidationAppException("contentType", $"Send the file as {signedType}, as announced.");
        if (contentLength is { } length && length != session.ExpectedSize)
            throw new ValidationAppException("size", $"The upload was announced as {session.ExpectedSize:N0} bytes.");

        try
        {
            await using var limited = new LengthLimitedStream(body, session.ExpectedSize);
            await storage.PutAsync(session.ObjectKey, limited, session.ContentType, ct);
        }
        catch (InvalidDataException)
        {
            session.Status = UploadStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            throw new ValidationAppException("size", $"The file is larger than the announced {session.ExpectedSize:N0} bytes.");
        }

        var stored = await storage.GetSizeAsync(session.ObjectKey, ct);
        session.Status = stored == session.ExpectedSize ? UploadStatus.Uploaded : UploadStatus.Failed;
        await db.SaveChangesAsync(ct);
        if (session.Status == UploadStatus.Failed)
            throw new ValidationAppException("size", "The upload ended before the whole file arrived. Start a new upload.");
    }

    /// <summary>The local store's stand-in for an S3 presigned GET.</summary>
    public async Task<StoredObject> OpenAsync(string token, CancellationToken ct = default)
    {
        var grant = signer.Verify(token, LocalObjectStorage.DownloadOperation)
            ?? throw new ForbiddenException("The download link is invalid or has expired.");
        if (!await storage.ExistsAsync(grant.Key, ct)) throw new NotFoundException("File");
        var length = await storage.GetSizeAsync(grant.Key, ct);
        var stream = await storage.OpenReadAsync(grant.Key, ct);
        return new StoredObject(stream, grant.ContentType ?? "application/octet-stream", grant.FileName, grant.Inline, length);
    }

    /// <summary>
    /// Turns the upload into a new document in a library folder, or a new version of a document.
    /// Academic (system) folders are filled through submissions, not by direct upload.
    /// </summary>
    public async Task<DocumentDetail> CompleteAsync(Guid uploadId, CompleteUploadRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var session = await db.UploadSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == uploadId && s.UserId == me.UserId, ct)
            ?? throw new NotFoundException("Upload", uploadId);

        var documentId = await DbTransactions.RunAsync(db, async () =>
        {
            if ((session.DocumentId ?? request.DocumentId) is { } existingId)
            {
                await factory.AddVersionAsync(existingId, uploadId, request.ChangeDescription, ct);
                await db.SaveChangesAsync(ct);
                return existingId;
            }

            var errors = new Dictionary<string, string[]>();
            if (request.FolderId is null) errors["folderId"] = ["Pick a folder."];
            if (request.DocumentTypeId is null) errors["documentTypeId"] = ["Every document needs a type."];
            if (string.IsNullOrWhiteSpace(request.Title)) errors["title"] = ["Give the document a title."];
            if (errors.Count > 0) throw new ValidationAppException(errors);

            var folderId = request.FolderId!.Value;
            var folder = await db.Folders.AsNoTracking().Where(f => f.Id == folderId)
                .Select(f => new { f.IsSystem }).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Folder", folderId);
            (await access.GetFolderAccessAsync(me, folderId, ct)).Require(AccessLevel.Contribute, "Folder", folderId);
            if (folder.IsSystem)
                throw new ConflictException("system-folder", "Academic folders are filled through submissions.",
                    "Submit the document for review; it's filed here when approved.");

            var version = await factory.CreateDocumentAsync(uploadId, new NewDocumentSpec(
                folderId, request.DocumentTypeId!.Value, request.Title!, me.UserId,
                request.AcademicYearId, request.AcademicTermId, request.Description), ct);
            if (request.Tags is { Count: > 0 })
            {
                var doc = await db.Documents.SingleAsync(d => d.Id == version.DocumentId, ct);
                await DocumentRules.SetTagsAsync(db, doc, request.Tags, ct);
                await db.SaveChangesAsync(ct);
            }
            return version.DocumentId;
        }, ct);

        return await documents.GetAsync(documentId, ct);
    }

    /// <summary>A storage-safe file name: no directories, no "..", only letters, digits, dot, dash and underscore.</summary>
    public static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/').Split('/').Last()).Normalize(NormalizationForm.FormKD);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name);
        stem = UnsafeChars().Replace(stem, "-").Trim('-', '.');
        stem = Dashes().Replace(stem, "-");
        if (stem.Length == 0) stem = "file";
        if (stem.Length > 120) stem = stem[..120];
        ext = UnsafeChars().Replace(ext, "");
        return stem + (ext.Length > 1 ? (ext.StartsWith('.') ? ext : "." + ext) : "");
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex(@"-{2,}|\.{2,}")]
    private static partial Regex Dashes();

    /// <summary>Passes bytes through and throws <see cref="InvalidDataException"/> past the limit.</summary>
    private sealed class LengthLimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int n)
        {
            _read += n;
            if (_read > limit) throw new InvalidDataException("The stream is longer than allowed.");
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
