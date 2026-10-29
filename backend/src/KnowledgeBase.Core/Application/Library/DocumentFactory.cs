using System.Security.Cryptography;
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
/// Turns a stored upload into an immutable DocumentVersion (frame 8): the bytes are checked against
/// the session (size, SHA-256), the type's rules are applied, and the version is queued for processing.
/// Callers authorize the target folder; adding a version also checks the caller's document access.
/// </summary>
public sealed class DocumentFactory(
    KbDbContext db,
    ICurrentUser currentUser,
    IAccessEvaluator access,
    IObjectStorage storage,
    IProcessingPipeline pipeline,
    IAuditWriter audit,
    DocumentNumbering numbering,
    TimeProvider clock) : IDocumentFactory
{
    /// <remarks>
    /// Saves: EF can't insert a document and its first version in one SaveChanges (each points at the
    /// other), so v1 is saved first and the document's current-version pointer second. Open a
    /// transaction around the call when the rest of the unit of work must commit with it.
    /// </remarks>
    public async Task<DocumentVersion> CreateDocumentAsync(Guid uploadSessionId, NewDocumentSpec spec, CancellationToken ct = default)
    {
        var session = await LoadSessionAsync(uploadSessionId, documentId: null, ct);

        // Find, not a query: submission filing creates a person's year and term folders in this unit of
        // work (AcademicFolderService.EnsureChildAsync), and they are saved together with the document.
        var folder = await db.Folders.FindAsync([spec.FolderId], ct)
            ?? throw new NotFoundException("Folder", spec.FolderId);
        var type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Id == spec.DocumentTypeId, ct)
            ?? throw new ValidationAppException("documentTypeId", "That document type doesn't exist.");
        if (!type.IsActive)
            throw new BusinessRuleException("type-retired", "This document type is retired.", "Pick the type that replaced it.");
        await DocumentRules.CheckScopeAsync(db, type, folder, ct);
        var (yearId, termId) = await DocumentRules.ResolvePeriodAsync(db, type.AcademicTag, spec.AcademicYearId, spec.AcademicTermId, ct);
        var title = DocumentRules.ValidTitle(spec.Title);
        var description = DocumentRules.ValidDescription(spec.Description);

        var (size, sha256) = await VerifyObjectAsync(session, ct);
        var number = type.NumberPattern is null ? null : await numbering.NextAsync(type, ct);

        var doc = new Document
        {
            Title = title,
            Description = description,
            DocumentNumber = number,
            DocumentTypeId = type.Id,
            FolderId = folder.Id,
            OwnerUserId = spec.OwnerUserId,
            DepartmentId = folder.DepartmentId,
            AcademicYearId = yearId,
            AcademicTermId = termId,
        };
        var version = NewVersion(doc.Id, 1, session, size, sha256, changeDescription: null);
        db.Documents.Add(doc);
        db.DocumentVersions.Add(version);
        Complete(session, doc.Id, version.Id);
        pipeline.Enqueue(version);
        audit.Record("document.created", "document", doc.Id, version.Id, new
        {
            title,
            documentNumber = number,
            folderId = folder.Id,
            documentType = type.Code,
            fileName = version.OriginalFilename,
            sizeBytes = size,
        });

        await db.SaveChangesAsync(ct);
        doc.CurrentVersionId = version.Id;
        await db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>Adds the next version and makes it current. Does not save.</summary>
    public async Task<DocumentVersion> AddVersionAsync(Guid documentId, Guid uploadSessionId, string? changeDescription, CancellationToken ct = default)
    {
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId && d.LifecycleStatus != LifecycleStatus.Deleted, ct)
            ?? throw new NotFoundException("Document", documentId);

        if (currentUser.IsAuthenticated)
        {
            var me = await currentUser.GetAsync(ct);
            var level = await access.GetDocumentAccessAsync(me, documentId, ct);
            level.Require(AccessLevel.Contribute, "Document", documentId);
            if (level < AccessLevel.Edit && doc.OwnerUserId != me.UserId)
                throw new ForbiddenException("Only the owner or someone who can edit this document can add a version.");
        }
        if (doc.LifecycleStatus == LifecycleStatus.Archived)
            throw new ConflictException("document-archived", "This document is archived.", "Restore it before adding a version.");

        var session = await LoadSessionAsync(uploadSessionId, documentId, ct);
        var (size, sha256) = await VerifyObjectAsync(session, ct);
        var note = changeDescription?.Trim();
        if (note is { Length: > 1000 }) throw new ValidationAppException("changeDescription", "Keep the change note under 1,000 characters.");

        var version = NewVersion(doc.Id, await NextVersionNumberAsync(doc.Id, ct), session, size, sha256, string.IsNullOrEmpty(note) ? null : note);
        db.DocumentVersions.Add(version);
        doc.CurrentVersionId = version.Id;
        Complete(session, doc.Id, version.Id);
        pipeline.Enqueue(version);
        audit.Record("document.version-added", "document", doc.Id, version.Id, new
        {
            versionNumber = version.VersionNumber,
            fileName = version.OriginalFilename,
            sizeBytes = size,
        });
        return version;
    }

    /// <summary>The next version number, counting versions added earlier in this unit of work.</summary>
    public async Task<int> NextVersionNumberAsync(Guid documentId, CancellationToken ct)
    {
        var saved = await db.DocumentVersions.Where(v => v.DocumentId == documentId).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;
        var pending = db.DocumentVersions.Local.Where(v => v.DocumentId == documentId).Select(v => v.VersionNumber).DefaultIfEmpty(0).Max();
        return Math.Max(saved, pending) + 1;
    }

    private DocumentVersion NewVersion(Guid documentId, int number, UploadSession session, long size, string sha256, string? changeDescription) => new()
    {
        DocumentId = documentId,
        VersionNumber = number,
        // The upload's key becomes the version's key: objects are written once and never moved.
        ObjectKey = session.ObjectKey,
        OriginalFilename = session.FileName,
        ContentType = session.ContentType,
        FileSizeBytes = size,
        Sha256 = sha256,
        UploadedByUserId = session.UserId,
        UploadedAt = clock.GetUtcNow(),
        ChangeDescription = changeDescription,
        ProcessingStatus = VersionProcessingStatus.Pending,
    };

    private static void Complete(UploadSession session, Guid documentId, Guid versionId)
    {
        session.Status = UploadStatus.Completed;
        session.DocumentId = documentId;
        session.VersionId = versionId;
    }

    private async Task<UploadSession> LoadSessionAsync(Guid uploadSessionId, Guid? documentId, CancellationToken ct)
    {
        var session = await db.UploadSessions.SingleOrDefaultAsync(s => s.Id == uploadSessionId, ct)
            ?? throw new NotFoundException("Upload", uploadSessionId);
        if (currentUser.LoadedUserId is { } me && session.UserId != me)
            throw new NotFoundException("Upload", uploadSessionId);

        switch (session.Status)
        {
            case UploadStatus.Completed:
                throw new ConflictException("upload-completed", "This upload was already turned into a document version.");
            case UploadStatus.Expired or UploadStatus.Failed:
                throw new ConflictException("upload-expired", "This upload expired or failed. Start a new upload.");
        }
        if (session.ExpiresAt < clock.GetUtcNow())
        {
            session.Status = UploadStatus.Expired;
            throw new ConflictException("upload-expired", "This upload expired. Start a new upload.");
        }
        if (documentId is not null && session.DocumentId is { } target && target != documentId)
            throw new ConflictException("upload-mismatch", "This upload was started for a different document.");
        return session;
    }

    /// <summary>Checks that the bytes arrived complete and computes their SHA-256 from storage.</summary>
    private async Task<(long Size, string Sha256)> VerifyObjectAsync(UploadSession session, CancellationToken ct)
    {
        var size = await storage.GetSizeAsync(session.ObjectKey, ct)
            ?? throw new ConflictException("upload-incomplete", "The file hasn't been uploaded yet.", "PUT the file to the upload URL first.");
        if (size != session.ExpectedSize || size <= 0)
            throw new BusinessRuleException("upload-size-mismatch", "The uploaded file isn't the size that was announced.",
                $"Expected {session.ExpectedSize:N0} bytes, received {size:N0}.");

        string sha256;
        await using (var stream = await storage.OpenReadAsync(session.ObjectKey, ct))
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (session.ExpectedSha256 is { } expected && !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("checksum-mismatch", "The uploaded file doesn't match its checksum.");
        return (size, sha256);
    }
}
