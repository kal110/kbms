using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>Document detail, metadata edits, versions and lifecycle (frames 1, 2, 14).</summary>
public sealed class DocumentService(
    KbDbContext db,
    ICurrentUser currentUser,
    IAccessEvaluator access,
    IObjectStorage storage,
    IProcessingPipeline pipeline,
    IAuditWriter audit,
    TimeProvider clock)
{
    /// <summary>Signed content links live 5 minutes (frame 8).</summary>
    public static readonly TimeSpan ContentLinkLifetime = TimeSpan.FromMinutes(5);

    public async Task<DocumentDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var (me, level) = await RequireAsync(id, AccessLevel.View, ct);
        return await BuildDetailAsync(id, me, level, ct);
    }

    public async Task<IReadOnlyList<VersionDto>> GetVersionsAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(id, AccessLevel.View, ct);
        return await LoadVersionsAsync(id, ct);
    }

    public async Task<DocumentDetail> UpdateAsync(Guid id, uint? ifMatch, UpdateDocumentRequest request, CancellationToken ct = default)
    {
        var (me, level) = await RequireAsync(id, AccessLevel.Edit, ct);
        var doc = await db.Documents.Include(d => d.DocumentType).Include(d => d.Folder).SingleAsync(d => d.Id == id, ct);
        ETags.Check(ifMatch, doc);
        if (doc.LifecycleStatus != LifecycleStatus.Active)
            throw new ConflictException("document-archived", "This document is archived.", "Restore it before editing.");

        var changed = new List<string>();
        if (request.Title is not null)
        {
            var title = DocumentRules.ValidTitle(request.Title);
            if (title != doc.Title) { doc.Title = title; changed.Add("title"); }
        }
        if (request.Description is not null)
        {
            var description = DocumentRules.ValidDescription(request.Description);
            if (description != doc.Description) { doc.Description = description; changed.Add("description"); }
        }

        var type = doc.DocumentType;
        if (request.DocumentTypeId is { } typeId && typeId != doc.DocumentTypeId)
        {
            type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Id == typeId, ct)
                ?? throw new ValidationAppException("documentTypeId", "That document type doesn't exist.");
            if (!type.IsActive) throw new BusinessRuleException("type-retired", "This document type is retired.");
            await DocumentRules.CheckScopeAsync(db, type, doc.Folder, ct);
            doc.DocumentTypeId = type.Id;
            changed.Add("documentType");
        }

        if (request.Period is not null || changed.Contains("documentType"))
        {
            // Without an explicit period, keep what still fits the (new) type's tag.
            var (yearId, termId) = request.Period is { } p
                ? (p.AcademicYearId, p.AcademicTermId)
                : type.AcademicTag switch
                {
                    Domain.DocumentTypes.AcademicTag.Term => (doc.AcademicYearId, doc.AcademicTermId),
                    Domain.DocumentTypes.AcademicTag.Year => (doc.AcademicYearId, (Guid?)null),
                    _ => ((Guid?)null, (Guid?)null),
                };
            var resolved = await DocumentRules.ResolvePeriodAsync(db, type.AcademicTag, yearId, termId, ct);
            if (resolved.YearId != doc.AcademicYearId || resolved.TermId != doc.AcademicTermId)
            {
                (doc.AcademicYearId, doc.AcademicTermId) = resolved;
                changed.Add("period");
            }
        }

        if (request.Tags is not null)
        {
            await DocumentRules.SetTagsAsync(db, doc, request.Tags, ct);
            changed.Add("tags");
        }

        if (changed.Count > 0)
        {
            doc.UpdatedAt = clock.GetUtcNow();
            audit.Record("document.updated", "document", id, metadata: new { fields = changed });
            await db.SaveChangesAsync(ct);
        }
        return await BuildDetailAsync(id, me, level, ct);
    }

    public async Task<VersionComparison> CompareAsync(Guid id, int from, int to, CancellationToken ct = default)
    {
        await RequireAsync(id, AccessLevel.View, ct);
        var versions = await LoadVersionsAsync(id, ct);
        var a = versions.FirstOrDefault(v => v.Number == from) ?? throw new NotFoundException("Version", from);
        var b = versions.FirstOrDefault(v => v.Number == to) ?? throw new NotFoundException("Version", to);

        var changes = new List<FieldChange>();
        void Diff(string field, object? x, object? y)
        {
            var sx = x?.ToString();
            var sy = y?.ToString();
            if (sx != sy) changes.Add(new FieldChange(field, sx, sy));
        }
        Diff("fileName", a.FileName, b.FileName);
        Diff("contentType", a.ContentType, b.ContentType);
        Diff("sizeBytes", a.SizeBytes, b.SizeBytes);
        Diff("sha256", a.Sha256, b.Sha256);
        Diff("pageCount", a.PageCount, b.PageCount);
        Diff("uploadedBy", a.UploadedBy.DisplayName, b.UploadedBy.DisplayName);
        Diff("uploadedAt", a.UploadedAt.ToString("O"), b.UploadedAt.ToString("O"));
        Diff("changeDescription", a.ChangeDescription, b.ChangeDescription);
        Diff("state", a.State, b.State);
        return new VersionComparison(a, b, a.Sha256 == b.Sha256, changes);
    }

    /// <summary>
    /// Makes an older version current again by adding a new version with the same bytes. The bytes
    /// are copied to a new key, since every version owns an immutable object.
    /// </summary>
    public async Task<DocumentDetail> RestoreVersionAsync(Guid id, Guid versionId, RestoreVersionRequest? request, CancellationToken ct = default)
    {
        var (me, level) = await RequireAsync(id, AccessLevel.Contribute, ct);
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        if (level < AccessLevel.Edit && doc.OwnerUserId != me.UserId)
            throw new ForbiddenException("Only the owner or someone who can edit this document can restore a version.");
        if (doc.LifecycleStatus != LifecycleStatus.Active)
            throw new ConflictException("document-archived", "This document is archived.", "Restore the document first.");

        var source = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.DocumentId == id, ct)
            ?? throw new NotFoundException("Version", versionId);
        if (doc.CurrentVersionId == source.Id)
            throw new ConflictException("already-current", "This version is already the current one.");

        var number = (await db.DocumentVersions.Where(v => v.DocumentId == id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
        var key = $"documents/{id:N}/v{number}/{UploadService.SafeFileName(source.OriginalFilename)}";
        if (!await storage.ExistsAsync(key, ct))
        {
            await using var bytes = await storage.OpenReadAsync(source.ObjectKey, ct);
            await storage.PutAsync(key, bytes, source.ContentType, ct);
        }

        var note = request?.ChangeDescription?.Trim();
        var version = new DocumentVersion
        {
            DocumentId = id,
            VersionNumber = number,
            ObjectKey = key,
            OriginalFilename = source.OriginalFilename,
            ContentType = source.ContentType,
            FileSizeBytes = source.FileSizeBytes,
            Sha256 = source.Sha256,
            PageCount = source.PageCount,
            UploadedByUserId = me.UserId,
            UploadedAt = clock.GetUtcNow(),
            ChangeDescription = string.IsNullOrEmpty(note) ? $"Restored from v{source.VersionNumber}" : note,
            CreatedFromVersionId = source.Id,
        };
        db.DocumentVersions.Add(version);
        doc.CurrentVersionId = version.Id;
        pipeline.Enqueue(version);
        audit.Record("document.version-restored", "document", id, version.Id, new
        {
            fromVersion = source.VersionNumber,
            newVersion = number,
        });
        await db.SaveChangesAsync(ct);
        return await BuildDetailAsync(id, me, level, ct);
    }

    /// <summary>
    /// A 5-minute signed link to a version's bytes. Inline preview needs View; the original file
    /// needs Download. Every link handed out is audited.
    /// </summary>
    public async Task<ContentLink> GetContentLinkAsync(Guid id, Guid? versionId, bool inline, CancellationToken ct = default)
    {
        await RequireAsync(id, inline ? AccessLevel.View : AccessLevel.Download, ct);
        var targetId = versionId ?? await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.CurrentVersionId).SingleAsync(ct)
            ?? throw new ConflictException("no-version", "This document has no file yet.");
        var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == targetId && v.DocumentId == id, ct)
            ?? throw new NotFoundException("Version", targetId);

        var url = storage.CreateDownloadUrl(version.ObjectKey, version.OriginalFilename, version.ContentType, ContentLinkLifetime, inline);
        audit.Record(inline ? "document.previewed" : "document.downloaded", "document", id, version.Id, new
        {
            versionNumber = version.VersionNumber,
            fileName = version.OriginalFilename,
        });
        await db.SaveChangesAsync(ct);
        return new ContentLink(url.Url, url.ExpiresAt, version.Id, version.VersionNumber, version.OriginalFilename, inline);
    }

    /// <summary>
    /// One page of a version's extracted text, for previewing a file the browser can't draw (Word,
    /// plain text). Needs View, like the inline preview, and is audited the same way.
    /// </summary>
    public async Task<VersionPageText> GetPageTextAsync(Guid id, Guid versionId, int page, CancellationToken ct = default)
    {
        await RequireAsync(id, AccessLevel.View, ct);
        var version = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId && v.DocumentId == id)
            .Select(v => new { v.Id, v.VersionNumber }).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Version", versionId);
        var pages = await db.VersionPages.CountAsync(p => p.VersionId == versionId, ct);
        if (pages == 0) throw new ConflictException("no-text", "This version's text isn't ready.", "It is read when the file is processed.");
        var text = await db.VersionPages.AsNoTracking().Where(p => p.VersionId == versionId && p.PageNumber == page)
            .Select(p => p.Text).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Page", page);

        audit.Record("document.previewed", "document", id, version.Id, new { versionNumber = version.VersionNumber, page, text = true });
        await db.SaveChangesAsync(ct);
        return new VersionPageText(version.Id, version.VersionNumber, page, pages, text);
    }

    public async Task<DocumentDetail> ArchiveAsync(Guid id, uint? ifMatch, CancellationToken ct = default)
    {
        var (me, level) = await RequireAsync(id, AccessLevel.Edit, ct);
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        ETags.Check(ifMatch, doc);
        if (doc.LifecycleStatus != LifecycleStatus.Active)
            throw new BusinessRuleException("invalid-transition", "Only an active document can be archived.");
        doc.LifecycleStatus = LifecycleStatus.Archived;
        audit.Record("document.archived", "document", id);
        await db.SaveChangesAsync(ct);
        return await BuildDetailAsync(id, me, level, ct);
    }

    public async Task<DocumentDetail> RestoreAsync(Guid id, uint? ifMatch, CancellationToken ct = default)
    {
        var (me, level) = await RequireAsync(id, AccessLevel.Edit, ct);
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        ETags.Check(ifMatch, doc);
        if (doc.LifecycleStatus != LifecycleStatus.Archived)
            throw new BusinessRuleException("invalid-transition", "Only an archived document can be restored.");
        doc.LifecycleStatus = LifecycleStatus.Active;
        audit.Record("document.restored", "document", id);
        await db.SaveChangesAsync(ct);
        return await BuildDetailAsync(id, me, level, ct);
    }

    /// <summary>Soft delete. Blocked by an active legal hold or while the document is under review.</summary>
    public async Task DeleteAsync(Guid id, uint? ifMatch, CancellationToken ct = default)
    {
        await RequireAsync(id, AccessLevel.Manage, ct);
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        ETags.Check(ifMatch, doc);
        if (await db.LegalHolds.AnyAsync(h => h.DocumentId == id && h.ReleasedAt == null, ct))
            throw new ConflictException("legal-hold", "This document is on legal hold and can't be deleted.");
        var active = DocumentRowQueries.ActiveSubmissionStatuses;
        if (await db.SubmissionDocuments.AnyAsync(sd => sd.DocumentId == id
                && db.Submissions.Any(s => s.Id == sd.SubmissionId && active.Contains(s.Status)), ct))
            throw new ConflictException("document-in-review", "This document is part of a submission under review.");

        doc.LifecycleStatus = LifecycleStatus.Deleted;
        doc.DeletedAt = clock.GetUtcNow();
        audit.Record("document.deleted", "document", id, metadata: new { doc.Title, doc.DocumentNumber });
        await db.SaveChangesAsync(ct);
    }

    // ---- Helpers --------------------------------------------------------------------------

    private async Task<(UserContext Me, AccessLevel Level)> RequireAsync(Guid id, AccessLevel needed, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(ct);
        var level = await access.GetDocumentAccessAsync(me, id, ct);
        level.Require(needed, "Document", id);
        return (me, level);
    }

    private async Task<DocumentDetail> BuildDetailAsync(Guid id, UserContext me, AccessLevel level, CancellationToken ct)
    {
        var row = await db.Documents.AsNoTracking().Where(d => d.Id == id).ToRows(db).SingleAsync(ct);
        var extra = await db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(d => new { d.InheritPermissions, d.CreatedAt, d.Version, FolderPath = d.Folder.Path })
            .SingleAsync(ct);
        var tags = await db.DocumentTags.AsNoTracking().Where(t => t.DocumentId == id)
            .Select(t => t.Tag.Name).OrderBy(n => n).ToListAsync(ct);
        var onHold = await db.LegalHolds.AnyAsync(h => h.DocumentId == id && h.ReleasedAt == null, ct);
        var submissions = await SubmissionsAsync(id, ct);
        var versions = await LoadVersionsAsync(id, ct, submissions);

        var crumbIds = extra.FolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        var crumbs = await db.Folders.AsNoTracking().Where(f => crumbIds.Contains(f.Id))
            .Select(f => new FolderRef(f.Id, f.Name, f.Kind)).ToListAsync(ct);

        var active = row.Lifecycle == LifecycleStatus.Active;
        var isOwner = row.OwnerId == me.UserId;
        var actions = new List<string>();
        if (level >= AccessLevel.View) actions.Add("preview");
        if (level >= AccessLevel.Download) actions.Add("download");
        if (active && level >= AccessLevel.Edit) actions.AddRange(["edit", "archive"]);
        // Same rule as RestoreVersionAsync: a restore is a new version, so whoever may add one may restore.
        if (active && level >= AccessLevel.Contribute && (isOwner || level >= AccessLevel.Edit)) actions.AddRange(["addVersion", "restoreVersion"]);
        if (row.Lifecycle == LifecycleStatus.Archived && level >= AccessLevel.Edit) actions.Add("restore");
        if (level >= AccessLevel.Manage && !onHold) actions.Add("delete");
        if (level >= AccessLevel.Manage || me.IsAdmin) actions.Add("manageAccess");

        var item = row.ToItem();
        return new DocumentDetail(
            row.Id, row.Title, row.Description, row.DocumentNumber, item.Type, item.Folder,
            crumbIds.Select(cid => crumbs.First(c => c.Id == cid)).ToList(),
            item.Owner, row.Lifecycle, row.State, row.Period, extra.InheritPermissions, tags,
            versions.FirstOrDefault(v => v.IsCurrent), versions.FirstOrDefault(v => v.IsPublished), versions,
            submissions, onHold, level, actions, extra.CreatedAt, row.UpdatedAt, ETags.From(extra.Version));
    }

    private Task<List<SubmissionRef>> SubmissionsAsync(Guid id, CancellationToken ct) =>
        db.SubmissionDocuments.AsNoTracking()
            .Where(sd => sd.DocumentId == id)
            .Join(db.Submissions, sd => sd.SubmissionId, s => s.Id,
                (sd, s) => new SubmissionRef(s.Id, s.SubmissionNumber, s.Status, sd.DocumentVersionId))
            .ToListAsync(ct);

    private async Task<IReadOnlyList<VersionDto>> LoadVersionsAsync(Guid id, CancellationToken ct, List<SubmissionRef>? submissions = null)
    {
        submissions ??= await SubmissionsAsync(id, ct);
        var pointers = await db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(d => new { d.CurrentVersionId, d.PublishedVersionId }).SingleAsync(ct);
        var rows = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == id)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new
            {
                v.Id,
                v.VersionNumber,
                v.OriginalFilename,
                v.ContentType,
                v.FileSizeBytes,
                v.Sha256,
                v.PageCount,
                v.UploadedByUserId,
                UploadedBy = db.Users.Where(u => u.Id == v.UploadedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                v.UploadedAt,
                v.ChangeDescription,
                v.ProcessingStatus,
                v.CreatedFromVersionId,
            })
            .ToListAsync(ct);
        var numbers = rows.ToDictionary(r => r.Id, r => r.VersionNumber);
        var activeStatuses = DocumentRowQueries.ActiveSubmissionStatuses;

        return rows.Select(v =>
        {
            var isCurrent = v.Id == pointers.CurrentVersionId;
            var isPublished = v.Id == pointers.PublishedVersionId;
            var inReview = submissions.Any(s => s.DocumentVersionId == v.Id && activeStatuses.Contains(s.Status));
            var state = isPublished ? VersionState.Published
                : inReview ? VersionState.InReview
                : isCurrent ? VersionState.Draft
                : VersionState.Superseded;
            var approvedIn = submissions.FirstOrDefault(s => s.DocumentVersionId == v.Id && s.Status == SubmissionStatus.Approved)?.Number;
            return new VersionDto(v.Id, v.VersionNumber, v.OriginalFilename, v.ContentType, v.FileSizeBytes, v.Sha256.Trim(), v.PageCount,
                new PersonRef(v.UploadedByUserId, v.UploadedBy ?? ""), v.UploadedAt, v.ChangeDescription, v.ProcessingStatus,
                state, isCurrent, isPublished,
                v.CreatedFromVersionId is { } from && numbers.TryGetValue(from, out var n) ? n : null,
                approvedIn);
        }).ToList();
    }
}
