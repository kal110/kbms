using System.Text;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Infrastructure.Ai;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KnowledgeBase.Core.Infrastructure.Processing;

/// <summary>The version_pages rows behind exact search (one per extracted page).</summary>
public static class VersionPages
{
    /// <summary>Swaps in a version's pages; the caller saves. Re-processing writes them again.</summary>
    public static async Task ReplaceAsync(KbDbContext db, Guid versionId, IReadOnlyList<PageText> pages, CancellationToken ct)
    {
        await db.VersionPages.Where(p => p.VersionId == versionId).ExecuteDeleteAsync(ct);
        db.VersionPages.AddRange(pages.Select(p => new VersionPage { VersionId = versionId, PageNumber = p.Number, Text = p.Text }));
    }
}

/// <summary>
/// Fills version_pages for versions processed before exact search existed, from their Extract
/// checkpoint (TextObjectKey). Housekeeping runs it, so it catches up shortly after an upgrade;
/// versions whose checkpoint is gone or empty are skipped until the host restarts.
/// </summary>
public sealed class VersionPageBackfill(KbDbContext db, IObjectStorage storage, ILogger<VersionPageBackfill> log)
{
    public const int BatchSize = 50;
    public const int MaxBatches = 20;

    /// <summary>Returns how many versions got their pages.</summary>
    public async Task<int> RunAsync(ISet<Guid> skip, CancellationToken ct)
    {
        var filled = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var skipped = skip.ToArray();
            var versions = await db.DocumentVersions.AsNoTracking()
                .Where(v => v.TextObjectKey != null && !skipped.Contains(v.Id) && !db.VersionPages.Any(p => p.VersionId == v.Id))
                .OrderBy(v => v.Id)
                .Select(v => new { v.Id, Key = v.TextObjectKey! })
                .Take(BatchSize)
                .ToListAsync(ct);
            if (versions.Count == 0) break;

            foreach (var v in versions)
            {
                IReadOnlyList<PageText> pages = [];
                try
                {
                    if (await storage.ExistsAsync(v.Key, ct))
                    {
                        await using var stream = await storage.OpenReadAsync(v.Key, ct);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        pages = ExtractedText.FromJsonLines(await reader.ReadToEndAsync(ct)).PlainPages();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Couldn't read the extracted text of version {VersionId}", v.Id);
                }

                if (pages.Count == 0)
                {
                    skip.Add(v.Id);
                    continue;
                }
                await VersionPages.ReplaceAsync(db, v.Id, pages, ct);
                filled++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            if (versions.Count < BatchSize) break;
        }
        return filled;
    }
}
