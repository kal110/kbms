using System.Text.Json;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Ai;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Application.Admin;

public sealed record ProcessingJobQuery(JobStatus? Status = null, ProcessingStage? Stage = null, string? Q = null, int Page = 1, int PageSize = 25);

public sealed record ProcessingJobRow(
    Guid Id,
    Guid DocumentId,
    Guid VersionId,
    string Title,
    int Version,
    string FileName,
    ProcessingStage Stage,
    JobStatus Status,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    string? Owner,
    DateTimeOffset UpdatedAt,
    JsonElement? Metrics);

public sealed record StageCount(string Stage, int Count);

public sealed record CorpusStats(int Documents, int Versions, long StorageBytes, long Pages);

/// <summary>
/// The vector index (frames 6, 10). SemanticEnabled means searches work right now; otherwise Reason
/// says why (AI off, the first build still running, a model change waiting for a rebuild).
/// </summary>
/// <param name="ActiveBuild">The collection the alias points at.</param>
/// <param name="LastBuild">The build in flight, or the most recent one.</param>
/// <param name="IndexedVersions">Versions with chunks in the active collection.</param>
/// <param name="PendingVersions">Processed, current or published versions not in the active collection yet.</param>
public sealed record SearchIndexStatus(
    bool AiEnabled,
    bool SemanticEnabled,
    string? Reason,
    string KeywordSearch,
    string CollectionAlias,
    string? EmbeddingModel,
    int EmbeddingDimensions,
    string? ChatModel,
    ChatProvider ChatProvider,
    IndexBuildRow? ActiveBuild,
    IndexBuildRow? LastBuild,
    int IndexedVersions,
    long Chunks,
    long Tokens,
    int PendingVersions);

public sealed record IndexBuildRow(
    Guid Id,
    string Collection,
    string Model,
    int Dimensions,
    IndexBuildStatus Status,
    double Progress,
    int TotalVersions,
    int IndexedVersions,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record IndexingStatus(
    IReadOnlyList<StageCount> Stages,
    IReadOnlyDictionary<string, int> JobsByStatus,
    CorpusStats Corpus,
    SearchIndexStatus Index,
    int OutboxBacklog,
    int MaxAttempts,
    int PipelineVersion);

public sealed record RetryResult(int Requeued);

/// <summary>Admin → Processing &amp; index (frame 6). Admin only; shows titles but never content.</summary>
public sealed class ProcessingAdminService(
    KbDbContext db,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IOptions<ProcessingOptions> processing,
    IOptions<AiOptions> ai,
    SearchIndexState indexState)
{
    public async Task<PagedResult<ProcessingJobRow>> ListJobsAsync(ProcessingJobQuery query, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();

        var jobs = Jobs();
        if (query.Status is { } status) jobs = jobs.Where(x => x.Job.Status == status);
        if (query.Stage is { } stage) jobs = jobs.Where(x => x.Job.Stage == stage);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{query.Q.Trim()}%";
            jobs = jobs.Where(x => EF.Functions.ILike(x.Document.Title, pattern) || EF.Functions.ILike(x.Version.OriginalFilename, pattern));
        }

        var request = new PageRequest(query.Page, query.PageSize);
        var total = await jobs.CountAsync(ct);
        var page = await jobs
            // Problems first, then the most recently touched.
            .OrderBy(x => x.Job.Status == JobStatus.DeadLettered ? 0 : x.Job.Status == JobStatus.Failed ? 1 : x.Job.Status == JobStatus.Running ? 2 : x.Job.Status == JobStatus.Queued ? 3 : 4)
            .ThenByDescending(x => x.Job.UpdatedAt)
            .Skip(request.Skip)
            .Take(request.SafePageSize)
            .ToListAsync(ct);

        return new PagedResult<ProcessingJobRow>(page.Select(ToRow).ToList(), request.SafePage, request.SafePageSize, total);
    }

    private sealed class JobSource
    {
        public required ProcessingJob Job { get; init; }
        public required DocumentVersion Version { get; init; }
        public required Document Document { get; init; }
        public string? Owner { get; init; }
    }

    private IQueryable<JobSource> Jobs() =>
        from j in db.ProcessingJobs.AsNoTracking()
        join v in db.DocumentVersions on j.DocumentVersionId equals v.Id
        join d in db.Documents on v.DocumentId equals d.Id
        select new JobSource { Job = j, Version = v, Document = d, Owner = d.Owner.DisplayName };

    private ProcessingJobRow ToRow(JobSource x) => new(
        x.Job.Id, x.Document.Id, x.Version.Id, x.Document.Title, x.Version.VersionNumber, x.Version.OriginalFilename,
        x.Job.Stage, x.Job.Status, x.Job.Attempt, processing.Value.MaxAttempts, x.Job.NextAttemptAt, x.Job.LastError,
        x.Owner, x.Job.UpdatedAt, x.Job.Metrics?.RootElement.Clone());

    public async Task<IndexingStatus> GetIndexingStatusAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();

        var byStatus = await db.ProcessingJobs.AsNoTracking()
            .GroupBy(j => j.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var inFlight = await db.ProcessingJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued)
            .GroupBy(j => new { j.Status, j.Stage })
            .Select(g => new { g.Key.Status, g.Key.Stage, Count = g.Count() })
            .ToListAsync(ct);

        // The frontend's pipeline strip: Queued, then each running stage, then done.
        var stages = new List<StageCount>
        {
            new("Queued", inFlight.Where(x => x.Status == JobStatus.Queued).Sum(x => x.Count)),
        };
        foreach (var stage in new[] { ProcessingStage.Extract, ProcessingStage.Ocr, ProcessingStage.Chunk, ProcessingStage.Embed, ProcessingStage.Index })
            stages.Add(new(stage.ToString(), inFlight.Where(x => x.Status == JobStatus.Running && x.Stage == stage).Sum(x => x.Count)));
        stages.Add(new("Done", byStatus.Where(x => x.Key == JobStatus.Succeeded).Sum(x => x.Count)));

        var documents = await db.Documents.CountAsync(d => d.LifecycleStatus != LifecycleStatus.Deleted, ct);
        var versions = await db.DocumentVersions.CountAsync(ct);
        var bytes = await db.DocumentVersions.SumAsync(v => (long?)v.FileSizeBytes, ct) ?? 0;
        var pages = await db.DocumentVersions.SumAsync(v => (long?)v.PageCount, ct) ?? 0;
        var backlog = await db.OutboxMessages.CountAsync(m => m.ProcessedAt == null, ct);

        return new IndexingStatus(
            stages,
            byStatus.ToDictionary(x => x.Key.ToString(), x => x.Count),
            new CorpusStats(documents, versions, bytes, pages),
            await GetSearchIndexStatusAsync(ct),
            backlog,
            processing.Value.MaxAttempts,
            processing.Value.PipelineVersion);
    }

    private async Task<SearchIndexStatus> GetSearchIndexStatusAsync(CancellationToken ct)
    {
        var a = ai.Value;
        var builds = db.IndexBuilds.AsNoTracking();
        var lastBuild = await builds
            .OrderBy(b => b.Status == IndexBuildStatus.Pending || b.Status == IndexBuildStatus.Building ? 0 : 1)
            .ThenByDescending(b => b.CreatedAt)
            .Select(BuildRow)
            .FirstOrDefaultAsync(ct);
        var active = await builds
            .Where(b => b.Status == IndexBuildStatus.Ready)
            .OrderByDescending(b => b.CompletedAt)
            .Select(BuildRow)
            .FirstOrDefaultAsync(ct);

        int indexed = 0, pending = 0;
        long chunks = 0, tokens = 0;
        if (active is not null)
        {
            var entries = db.VersionIndexEntries.Where(e => e.Collection == active.Collection && e.ChunkCount > 0);
            indexed = await entries.CountAsync(ct);
            chunks = await entries.SumAsync(e => (long)e.ChunkCount, ct);
            tokens = await entries.SumAsync(e => (long)e.TokenCount, ct);
            pending = await (
                    from v in db.DocumentVersions
                    join d in db.Documents on v.DocumentId equals d.Id
                    where d.LifecycleStatus != LifecycleStatus.Deleted
                        && (d.CurrentVersionId == v.Id || d.PublishedVersionId == v.Id)
                        && (v.ProcessingStatus == VersionProcessingStatus.Ready || v.ProcessingStatus == VersionProcessingStatus.Indexed)
                        && !db.VersionIndexEntries.Any(e => e.Collection == active.Collection && e.VersionId == v.Id)
                    select v.Id)
                .CountAsync(ct);
        }

        string? reason;
        if (!a.Enabled) reason = "Semantic search and the AI assistant are off (Ai:Enabled = false).";
        else (_, reason) = await indexState.WritableAsync(ct);
        if (reason is not null) reason = char.ToUpperInvariant(reason[0]) + reason[1..].TrimEnd('.') + ".";

        return new SearchIndexStatus(
            AiEnabled: a.Enabled,
            SemanticEnabled: reason is null,
            Reason: reason,
            KeywordSearch: "PostgreSQL full-text search (simple) with trigram matching on title and number",
            CollectionAlias: a.VectorStore.CollectionAlias,
            EmbeddingModel: a.Embedding.Model,
            EmbeddingDimensions: a.Embedding.Dimensions,
            ChatModel: a.Chat.Model,
            ChatProvider: a.Chat.Provider,
            ActiveBuild: active,
            LastBuild: lastBuild,
            IndexedVersions: indexed,
            Chunks: chunks,
            Tokens: tokens,
            PendingVersions: pending);
    }

    private static readonly System.Linq.Expressions.Expression<Func<IndexBuild, IndexBuildRow>> BuildRow = b => new IndexBuildRow(
        b.Id, b.Collection, b.Model, b.Dimensions, b.Status, b.Progress, b.TotalVersions, b.IndexedVersions, b.LastError, b.CreatedAt, b.CompletedAt);

    /// <summary>Puts a failed or dead-lettered job back in the queue from the start.</summary>
    public async Task<ProcessingJobRow> RetryAsync(Guid jobId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();

        var job = await db.ProcessingJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new NotFoundException("Processing job", jobId);
        if (job.Status is not (JobStatus.Failed or JobStatus.DeadLettered))
            throw new BusinessRuleException("invalid-transition", "Only failed or dead-lettered jobs can be retried.", $"This job is {job.Status}.");

        var version = await db.DocumentVersions.SingleAsync(v => v.Id == job.DocumentVersionId, ct);
        Requeue(job, version);
        audit.Record("processing-job.retried", "ProcessingJob", job.Id, version.Id);
        await db.SaveChangesAsync(ct);

        return ToRow(await Jobs().SingleAsync(x => x.Job.Id == jobId, ct));
    }

    public async Task<RetryResult> RetryAllFailedAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();

        var failed = await (
                from j in db.ProcessingJobs
                where j.Status == JobStatus.Failed || j.Status == JobStatus.DeadLettered
                join v in db.DocumentVersions on j.DocumentVersionId equals v.Id
                select new { j, v })
            .ToListAsync(ct);
        foreach (var x in failed) Requeue(x.j, x.v);
        audit.Record("processing-job.retried-all", "ProcessingJob", null, metadata: new { count = failed.Count });
        await db.SaveChangesAsync(ct);
        return new RetryResult(failed.Count);
    }

    /// <summary>
    /// Re-embeds every indexable version into a new collection and swaps the alias when it's full
    /// (frame 6). Searches keep using the old collection until then. The model is the configured
    /// one (Ai:Embedding:Model); <paramref name="model"/> only guards against a stale screen.
    /// </summary>
    public async Task<IndexBuildRow> RebuildIndexAsync(string? model, CancellationToken ct = default)
    {
        var user = await currentUser.GetAsync(ct);
        user.RequireAdmin();
        var a = ai.Value;
        if (!a.Enabled)
            throw new FeatureDisabledException("Vector index rebuild", "Switch on Ai:Enabled first; keyword search needs no rebuild.");
        if (!string.IsNullOrWhiteSpace(model) && model != a.Embedding.Model)
            throw new BusinessRuleException("model-not-configured", "The embedding model is set in configuration.",
                $"The server embeds with {a.Embedding.Model}; change Ai:Embedding:Model to use {model}.");
        if (await indexState.InFlightAsync(ct) is { } running)
            throw new ConflictException("rebuild-running", "A rebuild is already running.", $"{running.Collection} is {running.Progress:P0} done.");

        IndexBuild build;
        try
        {
            build = await indexState.StartBuildAsync(user.UserId, ct);
        }
        catch (DbUpdateException)
        {
            // The worker queued one at the same moment (one in-flight build, enforced by a unique index).
            throw new ConflictException("rebuild-running", "A rebuild is already running.");
        }
        audit.Record("search-index.rebuild-requested", "IndexBuild", build.Id, metadata: new { build.Collection, build.Model, build.Dimensions });
        await db.SaveChangesAsync(ct);
        return BuildRow.Compile()(build);
    }

    private static void Requeue(ProcessingJob job, DocumentVersion version)
    {
        job.Status = JobStatus.Queued;
        job.Stage = ProcessingStage.Extract;
        job.Attempt = 0;
        job.NextAttemptAt = null;
        job.LastError = null;
        version.ProcessingStatus = VersionProcessingStatus.Pending;
    }
}
