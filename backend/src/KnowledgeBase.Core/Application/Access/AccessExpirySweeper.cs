using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>
/// Expired folder grants stop counting only once the effective table is rebuilt. Housekeeping calls
/// this periodically: it recomputes the subtrees whose effective rows still come from an expired
/// grant. (Document grants are checked against ExpiresAt on every read and need no sweep.)
/// </summary>
public sealed class AccessExpirySweeper(KbDbContext db, IAccessEvaluator access, TimeProvider clock)
{
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var stale = await db.DocumentPermissions.AsNoTracking()
            .Where(p => p.ResourceType == ResourceType.Folder && p.ExpiresAt != null && p.ExpiresAt <= now
                && db.FolderEffectiveAccess.Any(a => a.SourceFolderId == p.ResourceId && a.PrincipalKey == p.PrincipalKey))
            .Select(p => p.ResourceId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var folderId in stale) await access.RecomputeSubtreeAsync(folderId, ct);
        return stale.Count;
    }
}
