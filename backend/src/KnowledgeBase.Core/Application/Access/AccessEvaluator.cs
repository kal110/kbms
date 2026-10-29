using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>
/// Folder access comes from the derived FolderEffectiveAccess table; document access is the folder's
/// (unless the document breaks inheritance) plus document grants, and owners can always edit their
/// own documents. Admins get nothing implicit here: they repair access, they don't read content.
/// </summary>
public sealed class AccessEvaluator(KbDbContext db, TimeProvider clock) : IAccessEvaluator
{
    public async Task<AccessLevel> GetFolderAccessAsync(UserContext user, Guid folderId, CancellationToken ct = default)
    {
        var keys = user.PrincipalKeys.ToArray();
        var levels = await db.FolderEffectiveAccess.AsNoTracking()
            .Where(a => a.FolderId == folderId && keys.Contains(a.PrincipalKey))
            .Select(a => a.Level)
            .ToListAsync(ct);
        return AccessLevels.Max(levels);
    }

    public async Task<AccessLevel> GetDocumentAccessAsync(UserContext user, Guid documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.LifecycleStatus != LifecycleStatus.Deleted)
            .Select(d => new { d.FolderId, d.InheritPermissions, d.OwnerUserId })
            .SingleOrDefaultAsync(ct);
        if (doc is null) return AccessLevel.None;

        var level = doc.InheritPermissions ? await GetFolderAccessAsync(user, doc.FolderId, ct) : AccessLevel.None;

        var keys = user.PrincipalKeys.ToArray();
        var now = clock.GetUtcNow();
        var direct = await db.DocumentPermissions.AsNoTracking()
            .Where(p => p.ResourceType == ResourceType.Document && p.ResourceId == documentId
                && keys.Contains(p.PrincipalKey)
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Select(p => p.Level)
            .ToListAsync(ct);
        level = AccessLevels.Max(level, AccessLevels.Max(direct));

        if (doc.OwnerUserId == user.UserId) level = AccessLevels.Max(level, AccessLevel.Edit);
        return level;
    }

    public IQueryable<Document> VisibleDocuments(UserContext user, AccessLevel min = AccessLevel.View)
    {
        var keys = user.PrincipalKeys.ToArray();
        var levels = AccessLevels.AtLeast(min);
        var now = clock.GetUtcNow();
        var ownerQualifies = AccessLevel.Edit >= min;
        var userId = user.UserId;

        return db.Documents.Where(d => d.LifecycleStatus != LifecycleStatus.Deleted && (
            (d.InheritPermissions && db.FolderEffectiveAccess.Any(a =>
                a.FolderId == d.FolderId && keys.Contains(a.PrincipalKey) && levels.Contains(a.Level)))
            || db.DocumentPermissions.Any(p =>
                p.ResourceType == ResourceType.Document && p.ResourceId == d.Id
                && keys.Contains(p.PrincipalKey) && levels.Contains(p.Level)
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            || (ownerQualifies && d.OwnerUserId == userId)));
    }

    public IQueryable<Folder> VisibleFolders(UserContext user, AccessLevel min = AccessLevel.View)
    {
        var keys = user.PrincipalKeys.ToArray();
        var levels = AccessLevels.AtLeast(min);
        return db.Folders.Where(f => db.FolderEffectiveAccess.Any(a =>
            a.FolderId == f.Id && keys.Contains(a.PrincipalKey) && levels.Contains(a.Level)));
    }

    /// <summary>
    /// Rebuilds the effective rows of a folder and its descendants from folder grants. Runs in a
    /// transaction (the caller's, if open) and saves, so call it after the grant change is saved.
    /// </summary>
    public Task RecomputeSubtreeAsync(Guid folderId, CancellationToken ct = default) =>
        DbTransactions.RunAsync(db, async () =>
        {
            var root = await db.Folders.AsNoTracking()
                .Where(f => f.Id == folderId)
                .Select(f => new { f.Id, f.ParentId, f.Path, f.InheritPermissions })
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Folder", folderId);

            var nodes = await db.Folders.AsNoTracking()
                .Where(f => f.Path.StartsWith(root.Path))
                .Select(f => new EffectiveAccessCalculator.Node(f.Id, f.ParentId, f.Path, f.InheritPermissions))
                .ToListAsync(ct);
            var ids = nodes.Select(n => n.Id).ToArray();

            var now = clock.GetUtcNow();
            var grants = await db.DocumentPermissions.AsNoTracking()
                .Where(p => p.ResourceType == ResourceType.Folder && ids.Contains(p.ResourceId)
                    && (p.ExpiresAt == null || p.ExpiresAt > now))
                .Select(p => new EffectiveAccessCalculator.Grant(p.ResourceId, p.PrincipalKey, p.Level))
                .ToListAsync(ct);

            var inherited = new Dictionary<string, EffectiveAccessCalculator.Effective>();
            if (root.InheritPermissions && root.ParentId is { } parentId)
            {
                inherited = await db.FolderEffectiveAccess.AsNoTracking()
                    .Where(a => a.FolderId == parentId)
                    .ToDictionaryAsync(a => a.PrincipalKey, a => new EffectiveAccessCalculator.Effective(a.Level, a.SourceFolderId), ct);
            }

            var computed = EffectiveAccessCalculator.Compute(root.Id, nodes, grants, inherited);

            await db.FolderEffectiveAccess.Where(a => ids.Contains(a.FolderId)).ExecuteDeleteAsync(ct);
            var idSet = ids.ToHashSet();
            foreach (var entry in db.ChangeTracker.Entries<FolderEffectiveAccess>().Where(e => idSet.Contains(e.Entity.FolderId)).ToList())
                entry.State = EntityState.Detached;

            db.FolderEffectiveAccess.AddRange(computed.SelectMany(folder => folder.Value.Select(a => new FolderEffectiveAccess
            {
                FolderId = folder.Key,
                PrincipalKey = a.Key,
                Level = a.Value.Level,
                SourceFolderId = a.Value.SourceFolderId,
            })));
            await db.SaveChangesAsync(ct);
        }, ct);
}
