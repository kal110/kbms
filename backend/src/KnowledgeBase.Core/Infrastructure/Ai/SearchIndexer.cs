using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// The filterable part of a version's points (frame 10, "QDRANT POINT PAYLOAD"): who may see it,
/// whether it's the published or the owner's draft version, and the folder, type and period the
/// UI filters on. Computed from PostgreSQL, which stays the authority.
/// </summary>
public sealed record VersionPayload(Guid VersionId, Guid DocumentId, string Title, bool Indexable, IReadOnlyDictionary<string, object?> Fields, string Hash);

public sealed class IndexPayloadBuilder(KbDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Payloads for the given versions. A version is indexable while its document isn't deleted and
    /// it is the document's published version or its current one.
    /// </summary>
    public async Task<Dictionary<Guid, VersionPayload>> BuildAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken ct = default)
    {
        var ids = versionIds.Distinct().ToArray();
        var rows = await (
                from v in db.DocumentVersions.AsNoTracking()
                where ids.Contains(v.Id)
                join d in db.Documents on v.DocumentId equals d.Id
                select new
                {
                    VersionId = v.Id,
                    v.VersionNumber,
                    DocumentId = d.Id,
                    d.Title,
                    d.DocumentNumber,
                    d.DocumentTypeId,
                    TypeCode = d.DocumentType.Code,
                    d.DepartmentId,
                    d.AcademicYearId,
                    d.AcademicTermId,
                    d.FolderId,
                    FolderPath = d.Folder.Path,
                    d.InheritPermissions,
                    d.OwnerUserId,
                    d.CurrentVersionId,
                    d.PublishedVersionId,
                    d.LifecycleStatus,
                })
            .ToListAsync(ct);

        // Who can see each document: its folder's effective principals (when it inherits), its own
        // unexpired grants, and its owner, exactly as IAccessEvaluator.VisibleDocuments decides.
        var folderIds = rows.Where(r => r.InheritPermissions).Select(r => r.FolderId).Distinct().ToArray();
        var folderKeys = (await db.FolderEffectiveAccess.AsNoTracking()
                .Where(a => folderIds.Contains(a.FolderId) && a.Level != AccessLevel.None)
                .Select(a => new { a.FolderId, a.PrincipalKey })
                .ToListAsync(ct))
            .ToLookup(a => a.FolderId, a => a.PrincipalKey);
        var docIds = rows.Select(r => r.DocumentId).Distinct().ToArray();
        var now = clock.GetUtcNow();
        var docKeys = (await db.DocumentPermissions.AsNoTracking()
                .Where(p => p.ResourceType == ResourceType.Document && docIds.Contains(p.ResourceId)
                    && p.Level != AccessLevel.None && (p.ExpiresAt == null || p.ExpiresAt > now))
                .Select(p => new { p.ResourceId, p.PrincipalKey })
                .ToListAsync(ct))
            .ToLookup(p => p.ResourceId, p => p.PrincipalKey);

        var result = new Dictionary<Guid, VersionPayload>();
        foreach (var r in rows)
        {
            var acl = new SortedSet<string>(StringComparer.Ordinal) { PrincipalKeys.User(r.OwnerUserId) };
            if (r.InheritPermissions) acl.UnionWith(folderKeys[r.FolderId]);
            acl.UnionWith(docKeys[r.DocumentId]);

            var published = r.VersionId == r.PublishedVersionId;
            var current = r.VersionId == r.CurrentVersionId;
            var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = r.Title,
                ["document_number"] = r.DocumentNumber,
                ["document_type_id"] = r.DocumentTypeId.ToString(),
                ["document_type"] = r.TypeCode,
                ["department_id"] = r.DepartmentId?.ToString(),
                ["academic_year_id"] = r.AcademicYearId?.ToString(),
                ["academic_term_id"] = r.AcademicTermId?.ToString(),
                ["folder_ids"] = r.FolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries),
                ["acl_principals"] = acl.ToArray(),
                ["state"] = published ? "published" : "draft",
                ["is_current"] = current,
                ["lifecycle"] = r.LifecycleStatus == LifecycleStatus.Archived ? "archived" : "active",
                ["owner_id"] = r.OwnerUserId.ToString(),
            };
            var indexable = r.LifecycleStatus != LifecycleStatus.Deleted && (published || current);
            result[r.VersionId] = new VersionPayload(r.VersionId, r.DocumentId, r.Title, indexable, fields, Hash(fields));
        }
        return result;
    }

    public static string Hash(IReadOnlyDictionary<string, object?> fields) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
}

public sealed record VersionIndexResult(bool Indexed, string? Skipped, int Chunks, int Tokens, long EmbedMs, long IndexMs)
{
    public static VersionIndexResult Skip(string reason) => new(false, reason, 0, 0, 0, 0);
}

/// <summary>The Embed and Index stages for one version (frame 8, steps 10–12).</summary>
public interface IVersionIndexer
{
    /// <summary>
    /// Indexes into the active collection. Returns a skip reason, without throwing, when there is no
    /// collection to write to yet (the index worker catches the version up later).
    /// </summary>
    Task<VersionIndexResult> IndexAsync(Guid versionId, IReadOnlyList<TextChunk> chunks, Action<ProcessingStage> onStage, CancellationToken ct);
}

/// <summary>Which collection is live and which one is being filled (IndexBuild rows).</summary>
public sealed class SearchIndexState(
    KbDbContext db,
    IEmbeddingProvider embedder,
    IOptions<AiOptions> ai,
    IOptions<ProcessingOptions> processing,
    TimeProvider clock)
{
    /// <summary>
    /// Queues a build into a new collection "{alias}_v{n}"; the index worker fills it and swaps the
    /// alias. At most one build is in flight (a unique index enforces it).
    /// </summary>
    public async Task<IndexBuild> StartBuildAsync(Guid? requestedBy, CancellationToken ct = default)
    {
        var alias = ai.Value.VectorStore.CollectionAlias;
        var prefix = $"{alias}_v";
        var existing = await db.IndexBuilds.AsNoTracking().Select(b => b.Collection).ToListAsync(ct);
        var next = 1 + existing
            .Select(c => c.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(c[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        var build = new IndexBuild
        {
            Collection = $"{prefix}{next}",
            Model = embedder.ModelId,
            Dimensions = embedder.Dimensions,
            PipelineVersion = processing.Value.PipelineVersion,
            Status = IndexBuildStatus.Pending,
            RequestedByUserId = requestedBy,
            CreatedAt = clock.GetUtcNow(),
        };
        db.IndexBuilds.Add(build);
        await db.SaveChangesAsync(ct);
        return build;
    }

    public Task<IndexBuild?> ActiveAsync(CancellationToken ct = default) =>
        db.IndexBuilds.AsNoTracking()
            .Where(b => b.Status == IndexBuildStatus.Ready)
            .OrderByDescending(b => b.CompletedAt)
            .FirstOrDefaultAsync(ct);

    public Task<IndexBuild?> InFlightAsync(CancellationToken ct = default) =>
        db.IndexBuilds.AsNoTracking()
            .Where(b => b.Status == IndexBuildStatus.Pending || b.Status == IndexBuildStatus.Building)
            .FirstOrDefaultAsync(ct);

    /// <summary>Vectors from today's embedding model fit this collection.</summary>
    public bool Matches(IndexBuild build) => build.Model == embedder.ModelId && build.Dimensions == embedder.Dimensions;

    /// <summary>Why the active collection can't take writes or queries, or null when it can.</summary>
    public async Task<(IndexBuild? Active, string? Problem)> WritableAsync(CancellationToken ct = default)
    {
        var active = await ActiveAsync(ct);
        if (active is null)
        {
            return (null, await InFlightAsync(ct) is { } building
                ? $"the search index is being built ({building.Progress:P0})"
                : "the search index hasn't been built yet");
        }
        if (!Matches(active))
            return (active, $"the index holds {active.Model} ({active.Dimensions} dims) but the embedding model is {embedder.ModelId} ({embedder.Dimensions} dims); rebuild the index");
        return (active, null);
    }
}

public sealed class SearchIndexer(
    KbDbContext db,
    IEmbeddingProvider embedder,
    IVectorIndex index,
    IndexPayloadBuilder payloads,
    SearchIndexState state,
    IOptions<ProcessingOptions> processing,
    TimeProvider clock) : IVersionIndexer
{
    /// <summary>Namespace of the deterministic point ids (UUIDv5).</summary>
    private static readonly Guid PointNamespace = new("6b1d3f0e-6f43-4c1a-9a43-7f5d2c3e8a01");

    public async Task<VersionIndexResult> IndexAsync(Guid versionId, IReadOnlyList<TextChunk> chunks, Action<ProcessingStage> onStage, CancellationToken ct)
    {
        var (active, problem) = await state.WritableAsync(ct);
        if (active is null || problem is not null)
        {
            onStage(ProcessingStage.Embed);
            onStage(ProcessingStage.Index);
            return VersionIndexResult.Skip($"deferred: {problem}");
        }
        return await IndexIntoAsync(active.Collection, versionId, chunks, onStage, ct);
    }

    /// <summary>
    /// Embeds the chunks and upserts them into <paramref name="collection"/>, then removes the
    /// version's points from older pipeline versions (only after the new ones are written, so
    /// search never has a gap). Records the VersionIndexEntry; the caller saves.
    /// </summary>
    public async Task<VersionIndexResult> IndexIntoAsync(string collection, Guid versionId, IReadOnlyList<TextChunk> chunks, Action<ProcessingStage> onStage, CancellationToken ct)
    {
        var payload = (await payloads.BuildAsync([versionId], ct)).GetValueOrDefault(versionId);
        if (payload is null || !payload.Indexable)
        {
            await index.DeleteVersionAsync(collection, versionId, ct: ct);
            await db.VersionIndexEntries.Where(e => e.Collection == collection && e.VersionId == versionId).ExecuteDeleteAsync(ct);
            onStage(ProcessingStage.Embed);
            onStage(ProcessingStage.Index);
            return VersionIndexResult.Skip("skipped: the version is neither current nor published");
        }

        var pipeline = processing.Value.PipelineVersion;
        var tokens = chunks.Sum(c => c.Tokens);

        onStage(ProcessingStage.Embed);
        var watch = Stopwatch.StartNew();
        var texts = chunks.Select(c => c.EmbeddingText(payload.Title)).ToList();
        var vectors = await embedder.EmbedAsync(texts, ct);
        var embedMs = watch.ElapsedMilliseconds;

        onStage(ProcessingStage.Index);
        watch.Restart();
        var version = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.VersionNumber).SingleAsync(ct);
        var points = chunks.Select((chunk, i) =>
        {
            var fields = new Dictionary<string, object?>(payload.Fields)
            {
                ["document_id"] = payload.DocumentId.ToString(),
                ["version_id"] = versionId.ToString(),
                ["version_number"] = version,
                ["chunk_index"] = chunk.Index,
                ["page_start"] = chunk.PageStart,
                ["page_end"] = chunk.PageEnd,
                ["section"] = chunk.Section,
                ["text"] = chunk.Text,
                ["tokens"] = chunk.Tokens,
                ["embedding_model"] = embedder.ModelId,
                ["pipeline_version"] = pipeline,
            };
            return new VectorPoint(PointId(versionId, chunk.Index, pipeline), vectors[i], SparseEncoder.EncodeDocument(texts[i]), fields);
        }).ToList();

        if (points.Count > 0) await index.UpsertAsync(collection, points, ct);
        await index.DeleteVersionAsync(collection, versionId, keepPipelineVersion: pipeline, ct);
        var indexMs = watch.ElapsedMilliseconds;

        var now = clock.GetUtcNow();
        var entry = await db.VersionIndexEntries.SingleOrDefaultAsync(e => e.Collection == collection && e.VersionId == versionId, ct);
        if (entry is null)
        {
            entry = new VersionIndexEntry { Collection = collection, VersionId = versionId, PayloadHash = payload.Hash };
            db.VersionIndexEntries.Add(entry);
        }
        entry.DocumentId = payload.DocumentId;
        entry.PipelineVersion = pipeline;
        entry.ChunkCount = points.Count;
        entry.TokenCount = tokens;
        entry.PayloadHash = payload.Hash;
        entry.IndexedAt = now;
        entry.SyncedAt = now;

        return new VersionIndexResult(true, null, points.Count, tokens, embedMs, indexMs);
    }

    /// <summary>UUIDv5(versionId:chunkIndex:pipelineVersion): a retry overwrites instead of duplicating.</summary>
    public static Guid PointId(Guid versionId, int chunkIndex, int pipelineVersion) =>
        DeterministicGuid.Create(PointNamespace, $"{versionId:N}:{chunkIndex}:{pipelineVersion}");
}

/// <summary>RFC 9562 name-based UUIDs (version 5, SHA-1).</summary>
public static class DeterministicGuid
{
    public static Guid Create(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var hash = SHA1.HashData([.. nsBytes, .. nameBytes]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
