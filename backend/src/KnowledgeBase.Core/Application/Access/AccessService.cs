using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>
/// Grant management on folders and documents (frames 13–14). Needs Manage on the resource, or the
/// System Administrator role: admins repair access anywhere without getting content access.
/// System grants come from the org tree and can't be changed here.
/// </summary>
public sealed class AccessService(
    KbDbContext db,
    ICurrentUser currentUser,
    IAccessEvaluator access,
    PrincipalDirectory principals,
    IAuditWriter audit,
    TimeProvider clock)
{
    // ---- Folders --------------------------------------------------------------------------

    public async Task<ResourceAccessDto> GetFolderAccessAsync(Guid folderId, CancellationToken ct = default)
    {
        var (folder, myLevel) = await RequireFolderManagerAsync(folderId, ct);
        var grants = await GrantsAsync(ResourceType.Folder, folderId, ct);

        var effectiveRows = await db.FolderEffectiveAccess.AsNoTracking()
            .Where(a => a.FolderId == folderId)
            .ToListAsync(ct);
        var sourceIds = effectiveRows.Select(r => r.SourceFolderId).Distinct().ToArray();
        var sourceNames = await db.Folders.AsNoTracking().Where(f => sourceIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var described = await principals.DescribeAsync(effectiveRows.Select(r => r.PrincipalKey), ct);

        var effective = effectiveRows
            .OrderByDescending(r => r.Level).ThenBy(r => described[r.PrincipalKey].Name)
            .Select(r => new EffectiveAccessDto(
                described[r.PrincipalKey],
                r.Level,
                new AccessSourceDto("folder", r.SourceFolderId, sourceNames.GetValueOrDefault(r.SourceFolderId, "")),
                r.SourceFolderId != folderId))
            .ToList();

        return new ResourceAccessDto(ResourceType.Folder, folder.Id, folder.Name, folder.IsSystem, folder.InheritPermissions,
            myLevel, true, grants, effective);
    }

    public async Task<GrantDto> GrantFolderAsync(Guid folderId, GrantRequest request, CancellationToken ct = default)
    {
        await RequireFolderManagerAsync(folderId, ct);
        var grant = await UpsertGrantAsync(ResourceType.Folder, folderId, request, ct);
        return await DbTransactions.RunAsync(db, async () =>
        {
            await db.SaveChangesAsync(ct);
            await access.RecomputeSubtreeAsync(folderId, ct);
            return (await GrantsAsync(ResourceType.Folder, folderId, ct)).Single(g => g.Id == grant.Id);
        }, ct);
    }

    public async Task RevokeFolderAsync(Guid folderId, Guid grantId, CancellationToken ct = default)
    {
        await RequireFolderManagerAsync(folderId, ct);
        await RemoveGrantAsync(await LoadGrantAsync(ResourceType.Folder, folderId, grantId, ct), ct);
        await DbTransactions.RunAsync(db, async () =>
        {
            await db.SaveChangesAsync(ct);
            await access.RecomputeSubtreeAsync(folderId, ct);
        }, ct);
    }

    public async Task<ResourceAccessDto> SetFolderInheritanceAsync(Guid folderId, bool inherit, CancellationToken ct = default)
    {
        var (folder, _) = await RequireFolderManagerAsync(folderId, ct);
        if (folder.IsSystem)
            throw new ConflictException("system-folder", "Inheritance on academic folders follows the org tree.",
                "Faculty and staff folders never inherit; unit folders always do.");
        if (folder.ParentId is null && inherit)
            throw new BusinessRuleException("no-parent", "A top-level folder has nothing to inherit from.");

        var tracked = await db.Folders.SingleAsync(f => f.Id == folderId, ct);
        if (tracked.InheritPermissions != inherit)
        {
            tracked.InheritPermissions = inherit;
            if (!inherit) await KeepManagerAsync(ResourceType.Folder, folderId, ct);
            audit.Record(inherit ? "folder.inheritance-restored" : "folder.inheritance-stopped", "folder", folderId);
            await DbTransactions.RunAsync(db, async () =>
            {
                await db.SaveChangesAsync(ct);
                await access.RecomputeSubtreeAsync(folderId, ct);
            }, ct);
        }
        return await GetFolderAccessAsync(folderId, ct);
    }

    // ---- Documents ------------------------------------------------------------------------

    public async Task<ResourceAccessDto> GetDocumentAccessAsync(Guid documentId, CancellationToken ct = default)
    {
        var (doc, myLevel) = await RequireDocumentManagerAsync(documentId, ct);
        var grants = await GrantsAsync(ResourceType.Document, documentId, ct);

        // Effective = the folder's rows (when inheriting) + unexpired document grants + the owner.
        var rows = new Dictionary<string, (AccessLevel Level, AccessSourceDto Source, bool Inherited)>();
        void Offer(string key, AccessLevel level, AccessSourceDto source, bool inherited)
        {
            if (!rows.TryGetValue(key, out var current) || level > current.Level) rows[key] = (level, source, inherited);
        }

        if (doc.InheritPermissions)
        {
            var folderRows = await db.FolderEffectiveAccess.AsNoTracking().Where(a => a.FolderId == doc.FolderId).ToListAsync(ct);
            var sourceIds = folderRows.Select(r => r.SourceFolderId).Distinct().ToArray();
            var names = await db.Folders.AsNoTracking().Where(f => sourceIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
            foreach (var r in folderRows)
                Offer(r.PrincipalKey, r.Level, new AccessSourceDto("folder", r.SourceFolderId, names.GetValueOrDefault(r.SourceFolderId, "")), true);
        }
        foreach (var g in grants.Where(g => !g.Expired))
            Offer(g.Principal.Key, g.Level, new AccessSourceDto("document", documentId, doc.Title), false);
        Offer(PrincipalKeys.User(doc.OwnerUserId), AccessLevel.Edit, new AccessSourceDto("owner", doc.OwnerUserId, "Document owner"), false);

        var described = await principals.DescribeAsync(rows.Keys, ct);
        var effective = rows
            .OrderByDescending(r => r.Value.Level).ThenBy(r => described[r.Key].Name)
            .Select(r => new EffectiveAccessDto(described[r.Key], r.Value.Level, r.Value.Source, r.Value.Inherited))
            .ToList();

        return new ResourceAccessDto(ResourceType.Document, doc.Id, doc.Title, false, doc.InheritPermissions,
            myLevel, true, grants, effective);
    }

    public async Task<GrantDto> GrantDocumentAsync(Guid documentId, GrantRequest request, CancellationToken ct = default)
    {
        await RequireDocumentManagerAsync(documentId, ct);
        var grant = await UpsertGrantAsync(ResourceType.Document, documentId, request, ct);
        await db.SaveChangesAsync(ct);
        return (await GrantsAsync(ResourceType.Document, documentId, ct)).Single(g => g.Id == grant.Id);
    }

    public async Task RevokeDocumentAsync(Guid documentId, Guid grantId, CancellationToken ct = default)
    {
        await RequireDocumentManagerAsync(documentId, ct);
        await RemoveGrantAsync(await LoadGrantAsync(ResourceType.Document, documentId, grantId, ct), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ResourceAccessDto> SetDocumentInheritanceAsync(Guid documentId, bool inherit, CancellationToken ct = default)
    {
        await RequireDocumentManagerAsync(documentId, ct);
        var doc = await db.Documents.SingleAsync(d => d.Id == documentId, ct);
        if (doc.InheritPermissions != inherit)
        {
            doc.InheritPermissions = inherit;
            if (!inherit) await KeepManagerAsync(ResourceType.Document, documentId, ct);
            audit.Record(inherit ? "document.inheritance-restored" : "document.inheritance-stopped", "document", documentId);
            await db.SaveChangesAsync(ct);
        }
        return await GetDocumentAccessAsync(documentId, ct);
    }

    public Task<IReadOnlyList<PrincipalDto>> SearchPrincipalsAsync(string? q, CancellationToken ct = default) =>
        principals.SearchAsync(q, ct: ct);

    // ---- Shared -------------------------------------------------------------------------------

    private async Task<(Folder Folder, AccessLevel Level)> RequireFolderManagerAsync(Guid folderId, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(ct);
        var folder = await db.Folders.AsNoTracking().SingleOrDefaultAsync(f => f.Id == folderId, ct)
            ?? throw new NotFoundException("Folder", folderId);
        var level = await access.GetFolderAccessAsync(me, folderId, ct);
        if (!me.IsAdmin) level.Require(AccessLevel.Manage, "Folder", folderId);
        return (folder, level);
    }

    private async Task<(Document Document, AccessLevel Level)> RequireDocumentManagerAsync(Guid documentId, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(ct);
        var doc = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId && d.LifecycleStatus != LifecycleStatus.Deleted, ct)
            ?? throw new NotFoundException("Document", documentId);
        var level = await access.GetDocumentAccessAsync(me, documentId, ct);
        if (!me.IsAdmin) level.Require(AccessLevel.Manage, "Document", documentId);
        return (doc, level);
    }

    /// <summary>
    /// Stopping inheritance must not lock out the person doing it: unless their Manage already comes
    /// from a grant on the resource itself, they get an explicit Manage grant.
    /// </summary>
    private async Task KeepManagerAsync(ResourceType type, Guid resourceId, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(ct);
        if (me.IsAdmin) return;

        var keys = me.PrincipalKeys.ToArray();
        var manage = AccessLevels.AtLeast(AccessLevel.Manage);
        var now = clock.GetUtcNow();
        var hasOwn = await db.DocumentPermissions.AnyAsync(p => p.ResourceType == type && p.ResourceId == resourceId
            && keys.Contains(p.PrincipalKey) && manage.Contains(p.Level)
            && (p.ExpiresAt == null || p.ExpiresAt > now), ct);
        if (!hasOwn) await UpsertGrantAsync(type, resourceId, new GrantRequest(PrincipalType.User, me.UserId, AccessLevel.Manage, null), ct);
    }

    private async Task<DocumentPermission> UpsertGrantAsync(ResourceType type, Guid resourceId, GrantRequest request, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(ct);
        if (request.Level == AccessLevel.None)
            throw new ValidationAppException("level", "Pick a level; remove the grant to take access away.");
        if (request.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
            throw new ValidationAppException("expiresAt", "The expiry date must be in the future.");

        var principal = await principals.RequireAsync(request.PrincipalType, request.PrincipalId, ct);
        var grant = await db.DocumentPermissions
            .SingleOrDefaultAsync(p => p.ResourceType == type && p.ResourceId == resourceId && p.PrincipalKey == principal.Key, ct);
        if (grant is { IsSystem: true })
            throw new ConflictException("system-grant", "This grant comes from the org tree and can't be changed by hand.");

        if (grant is null)
        {
            grant = new DocumentPermission
            {
                ResourceType = type,
                ResourceId = resourceId,
                PrincipalType = request.PrincipalType,
                PrincipalId = request.PrincipalId,
                PrincipalKey = principal.Key,
            };
            db.DocumentPermissions.Add(grant);
        }
        grant.Level = request.Level;
        grant.ExpiresAt = request.ExpiresAt;
        grant.GrantedByUserId = me.UserId;
        grant.GrantedAt = clock.GetUtcNow();

        var resource = type == ResourceType.Folder ? "folder" : "document";
        audit.Record($"{resource}.access-granted", resource, resourceId, metadata: new
        {
            principal = principal.Key,
            principalName = principal.Name,
            level = request.Level.ToString(),
            expiresAt = request.ExpiresAt,
        });
        return grant;
    }

    private async Task<DocumentPermission> LoadGrantAsync(ResourceType type, Guid resourceId, Guid grantId, CancellationToken ct)
    {
        var grant = await db.DocumentPermissions
            .SingleOrDefaultAsync(p => p.Id == grantId && p.ResourceType == type && p.ResourceId == resourceId, ct)
            ?? throw new NotFoundException("Grant", grantId);
        if (grant.IsSystem)
            throw new ConflictException("system-grant", "This grant comes from the org tree and can't be removed by hand.");
        return grant;
    }

    private async Task RemoveGrantAsync(DocumentPermission grant, CancellationToken ct)
    {
        // Named like the grant record, so the audit log reads without looking the key up.
        var principal = await principals.DescribeAsync(grant.PrincipalKey, ct);
        db.DocumentPermissions.Remove(grant);
        var resource = grant.ResourceType == ResourceType.Folder ? "folder" : "document";
        audit.Record($"{resource}.access-revoked", resource, grant.ResourceId, metadata: new
        {
            principal = grant.PrincipalKey,
            principalName = principal.Name,
            level = grant.Level.ToString(),
        });
    }

    private async Task<IReadOnlyList<GrantDto>> GrantsAsync(ResourceType type, Guid resourceId, CancellationToken ct)
    {
        var rows = await db.DocumentPermissions.AsNoTracking()
            .Where(p => p.ResourceType == type && p.ResourceId == resourceId)
            .Select(p => new
            {
                p.Id,
                p.PrincipalKey,
                p.Level,
                p.IsSystem,
                p.ExpiresAt,
                p.GrantedAt,
                GrantedBy = db.Users.Where(u => u.Id == p.GrantedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var described = await principals.DescribeAsync(rows.Select(r => r.PrincipalKey), ct);
        var now = clock.GetUtcNow();
        return rows
            .OrderByDescending(r => r.IsSystem).ThenByDescending(r => r.Level).ThenBy(r => described[r.PrincipalKey].Name)
            .Select(r => new GrantDto(r.Id, described[r.PrincipalKey], r.Level, r.IsSystem, r.ExpiresAt,
                r.ExpiresAt is { } e && e <= now, r.GrantedBy, r.GrantedAt))
            .ToList();
    }
}
