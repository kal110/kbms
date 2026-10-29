using System.Collections.Concurrent;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// Keeps the vector index in step with PostgreSQL (frames 6, 10, 25). Each pass:
///   1. starts a build when there is no collection yet or the embedding model changed;
///   2. fills the in-flight build (create collection → backfill → swap the alias → retire the old one);
///   3. catches the active collection up on versions whose index write was deferred;
///   4. re-syncs point payloads (ACL principals, state, folder) whose hash changed, and removes
///      versions that are no longer current or published.
/// PostgreSQL re-checks every hit at query time, so a stale payload can hide a result for a few
/// minutes but never show one the caller can't open.
/// </summary>
public sealed class SearchIndexMaintainer(
    KbDbContext db,
    IVectorIndex index,
    SearchIndexer indexer,
    SearchIndexState state,
    IndexPayloadBuilder payloads,
    DocumentProcessor processor,
    IOptions<AiOptions> ai,
    TimeProvider clock,
    ILogger<SearchIndexMaintainer> log)
{
    /// <summary>Versions embedded per pass and collection (one embedding batch each).</summary>
    public const int VersionsPerPass = 25;

    /// <summary>Entries re-checked per pass, oldest sync first.</summary>
    public const int SyncBatch = 500;

    /// <summary>An entry is re-checked at least this often even if its document didn't change (folder grants).</summary>
    public static readonly TimeSpan SyncAge = TimeSpan.FromMinutes(2);

    /// <summary>How long a version that failed on its own (not a service outage) waits before another try.</summary>
    public static readonly TimeSpan ParkFor = TimeSpan.FromHours(1);

    public async Task RunAsync(ConcurrentDictionary<Guid, DateTimeOffset> parked, CancellationToken ct)
    {
        await EnsureBuildAsync(ct);

        var building = await db.IndexBuilds
            .Where(b => b.Status == IndexBuildStatus.Pending || b.Status == IndexBuildStatus.Building)
            .FirstOrDefaultAsync(ct);
        if (building is not null) await AdvanceAsync(building, parked, ct);

        var (active, problem) = await state.WritableAsync(ct);
        if (active is not null && problem is null)
        {
            try
            {
                await BackfillAsync(active.Collection, markIndexed: true, parked, ct);
            }
            catch (Exception ex) when (ex is EmbeddingException or VectorIndexException)
            {
                log.LogWarning("Index catch-up paused: {Error}", ex.Message);
            }
        }

        try
        {
            await SyncPayloadsAsync(ct);
        }
        catch (VectorIndexException ex)
        {
            log.LogWarning("Index payload sync paused: {Error}", ex.Message);
        }
    }

    /// <summary>No collection to search, or one filled by another embedding model: queue a build.</summary>
    private async Task EnsureBuildAsync(CancellationToken ct)
    {
        if (await state.InFlightAsync(ct) is not null) return;
        var active = await state.ActiveAsync(ct);
        if (active is not null && state.Matches(active)) return;

        var build = await state.StartBuildAsync(requestedBy: null, ct);
        log.LogInformation("Queued index build {Collection} ({Reason})", build.Collection,
            active is null ? "no active collection" : $"embedding model changed from {active.Model}");
    }

    private async Task AdvanceAsync(IndexBuild build, ConcurrentDictionary<Guid, DateTimeOffset> parked, CancellationToken ct)
    {
        if (!state.Matches(build))
        {
            // The configuration changed under the build; the next pass queues one for the new model.
            build.Status = IndexBuildStatus.Failed;
            build.LastError = "The embedding model changed while this build was running.";
            build.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await DropAsync(build.Collection, ct);
            return;
        }

        try
        {
            if (build.Status == IndexBuildStatus.Pending)
            {
                // A collection left by a crashed attempt (or a reset database) can't be trusted: start clean.
                if (await index.GetCollectionAsync(build.Collection, ct) is not null)
                    await index.DeleteCollectionAsync(build.Collection, ct);
                await index.CreateCollectionAsync(build.Collection, build.Dimensions, ct);
                build.Status = IndexBuildStatus.Building;
                await db.SaveChangesAsync(ct);
                log.LogInformation("Created collection {Collection} ({Dimensions} dims)", build.Collection, build.Dimensions);
            }

            var more = await BackfillAsync(build.Collection, markIndexed: false, parked, ct);

            var total = await IndexableVersions().CountAsync(ct);
            var done = await db.VersionIndexEntries.CountAsync(e => e.Collection == build.Collection, ct);
            build.TotalVersions = total;
            build.IndexedVersions = Math.Min(done, total);
            build.Progress = total == 0 ? 1 : Math.Min(1, done / (double)total);
            build.LastError = null;

            if (!more) await CompleteAsync(build, ct);
            else await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is EmbeddingException or VectorIndexException)
        {
            // A service outage: keep the build and try again next pass.
            build.LastError = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            await db.SaveChangesAsync(ct);
            log.LogWarning("Index build {Collection} paused: {Error}", build.Collection, ex.Message);
        }
    }

    /// <summary>Swaps the alias to the new collection, then retires the old one.</summary>
    private async Task CompleteAsync(IndexBuild build, CancellationToken ct)
    {
        await index.PointAliasAsync(ai.Value.VectorStore.CollectionAlias, build.Collection, ct);

        var now = clock.GetUtcNow();
        var previous = await db.IndexBuilds.Where(b => b.Status == IndexBuildStatus.Ready && b.Id != build.Id).ToListAsync(ct);
        build.Status = IndexBuildStatus.Ready;
        build.Progress = 1;
        build.CompletedAt = now;
        foreach (var old in previous)
        {
            old.Status = IndexBuildStatus.Retired;
            old.LastError = null;
        }
        await db.SaveChangesAsync(ct);

        // Version statuses follow the collection searches now use.
        var collection = build.Collection;
        await db.DocumentVersions
            .Where(v => v.ProcessingStatus == VersionProcessingStatus.Indexed
                && !db.VersionIndexEntries.Any(e => e.Collection == collection && e.VersionId == v.Id && e.ChunkCount > 0))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, VersionProcessingStatus.Ready), ct);
        await db.DocumentVersions
            .Where(v => v.ProcessingStatus == VersionProcessingStatus.Ready
                && db.VersionIndexEntries.Any(e => e.Collection == collection && e.VersionId == v.Id && e.ChunkCount > 0))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, VersionProcessingStatus.Indexed), ct);

        foreach (var old in previous) await DropAsync(old.Collection, ct);
        log.LogInformation("Index build {Collection} is live ({Versions} versions)", build.Collection, build.IndexedVersions);
    }

    private async Task DropAsync(string collection, CancellationToken ct)
    {
        await db.VersionIndexEntries.Where(e => e.Collection == collection).ExecuteDeleteAsync(ct);
        try
        {
            await index.DeleteCollectionAsync(collection, ct);
        }
        catch (VectorIndexException ex)
        {
            log.LogWarning("Couldn't drop collection {Collection}: {Error}", collection, ex.Message);
        }
    }

    /// <summary>Current or published versions of live documents that finished processing.</summary>
    private IQueryable<DocumentVersion> IndexableVersions() =>
        from v in db.DocumentVersions
        join d in db.Documents on v.DocumentId equals d.Id
        where d.LifecycleStatus != LifecycleStatus.Deleted
            && (d.CurrentVersionId == v.Id || d.PublishedVersionId == v.Id)
            && (v.ProcessingStatus == VersionProcessingStatus.Ready || v.ProcessingStatus == VersionProcessingStatus.Indexed)
        select v;

    /// <summary>
    /// Indexes up to <see cref="VersionsPerPass"/> indexable versions missing from the collection,
    /// newest first. Returns whether more are waiting. Service outages propagate; a version that
    /// fails on its own is parked so it can't block a build.
    /// </summary>
    private async Task<bool> BackfillAsync(string collection, bool markIndexed, ConcurrentDictionary<Guid, DateTimeOffset> parked, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var expired in parked.Where(p => p.Value <= now).Select(p => p.Key).ToList()) parked.TryRemove(expired, out _);

        var take = VersionsPerPass + parked.Count;
        var candidates = await IndexableVersions()
            .Where(v => !db.VersionIndexEntries.Any(e => e.Collection == collection && e.VersionId == v.Id))
            .OrderByDescending(v => v.UploadedAt)
            .Select(v => v.Id)
            .Take(take)
            .ToListAsync(ct);
        var todo = candidates.Where(id => !parked.ContainsKey(id)).ToList();

        foreach (var id in todo.Take(VersionsPerPass))
        {
            var version = await db.DocumentVersions.SingleAsync(v => v.Id == id, ct);
            try
            {
                var chunks = await processor.PrepareChunksAsync(version, ct);
                var result = await indexer.IndexIntoAsync(collection, id, chunks, _ => { }, ct);
                if (markIndexed && result.Indexed && result.Chunks > 0) version.ProcessingStatus = VersionProcessingStatus.Indexed;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not (EmbeddingException or VectorIndexException or OperationCanceledException))
            {
                parked[id] = now + ParkFor;
                db.ChangeTracker.Clear();
                log.LogWarning("Couldn't index version {VersionId}; retrying in {Hours} h: {Error}",
                    id, ParkFor.TotalHours, ProcessingWorker.Sanitize(ex));
            }
        }
        return todo.Count > VersionsPerPass || candidates.Count == take;
    }

    /// <summary>
    /// Re-checks indexed versions against PostgreSQL: documents edited since their last sync first,
    /// then the longest-unsynced. Rewrites the filter fields when their hash changed and removes
    /// versions that are no longer current or published.
    /// </summary>
    private async Task SyncPayloadsAsync(CancellationToken ct)
    {
        var collections = await db.IndexBuilds.AsNoTracking()
            .Where(b => b.Status == IndexBuildStatus.Ready || b.Status == IndexBuildStatus.Building)
            .Select(b => new { b.Collection, b.Status })
            .ToListAsync(ct);
        if (collections.Count == 0) return;
        var names = collections.Select(c => c.Collection).ToArray();
        var active = collections.FirstOrDefault(c => c.Status == IndexBuildStatus.Ready)?.Collection;

        var now = clock.GetUtcNow();
        var cutoff = now - SyncAge;
        var changed = await (
                from e in db.VersionIndexEntries
                where names.Contains(e.Collection)
                join d in db.Documents on e.DocumentId equals d.Id
                where d.UpdatedAt > e.SyncedAt
                orderby e.SyncedAt
                select e)
            .Take(SyncBatch)
            .ToListAsync(ct);
        var stale = await db.VersionIndexEntries
            .Where(e => names.Contains(e.Collection) && e.SyncedAt < cutoff)
            .OrderBy(e => e.SyncedAt)
            .Take(SyncBatch)
            .ToListAsync(ct);
        var entries = changed.Concat(stale).Distinct().ToList();
        if (entries.Count == 0) return;

        var current = await payloads.BuildAsync(entries.Select(e => e.VersionId).ToArray(), ct);
        var dropped = new List<Guid>();
        var rewritten = 0;
        foreach (var entry in entries)
        {
            var payload = current.GetValueOrDefault(entry.VersionId);
            if (payload is null || !payload.Indexable)
            {
                await index.DeleteVersionAsync(entry.Collection, entry.VersionId, ct: ct);
                db.VersionIndexEntries.Remove(entry);
                if (entry.Collection == active) dropped.Add(entry.VersionId);
                continue;
            }
            if (payload.Hash != entry.PayloadHash)
            {
                await index.SetVersionPayloadAsync(entry.Collection, entry.VersionId, payload.Fields, ct);
                entry.PayloadHash = payload.Hash;
                rewritten++;
            }
            entry.SyncedAt = now;
        }

        if (dropped.Count > 0)
        {
            await db.DocumentVersions
                .Where(v => dropped.Contains(v.Id) && v.ProcessingStatus == VersionProcessingStatus.Indexed)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, VersionProcessingStatus.Ready), ct);
        }
        await db.SaveChangesAsync(ct);
        if (dropped.Count > 0 || rewritten > 0)
            log.LogInformation("Index sync: {Rewritten} payloads rewritten, {Dropped} versions removed", rewritten, dropped.Count);
    }
}

/// <summary>
/// Runs <see cref="SearchIndexMaintainer"/> every Ai:IndexSyncSeconds while Ai:Enabled is on. A
/// PostgreSQL advisory lock keeps it to one runner when several API instances share a database.
/// </summary>
public sealed class SearchIndexWorker(
    IServiceScopeFactory scopes,
    IOptions<AiOptions> ai,
    IOptions<ProcessingOptions> processing,
    TimeProvider clock,
    ILogger<SearchIndexWorker> log) : BackgroundService
{
    /// <summary>pg advisory lock key ("KBMSIDX").</summary>
    private const long LockKey = 0x4B424D53494458;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> parked = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ai.Value.Enabled || !processing.Value.WorkersEnabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, ai.Value.IndexSyncSeconds)), clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Search index pass failed; retrying in {Seconds}s", ai.Value.IndexSyncSeconds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();

        // Session-level lock on a connection held for the whole pass.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct);
            if (!locked) return;
            try
            {
                await scope.ServiceProvider.GetRequiredService<SearchIndexMaintainer>().RunAsync(parked, ct);
            }
            finally
            {
                await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({LockKey}) AS \"Value\"").SingleAsync(CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
