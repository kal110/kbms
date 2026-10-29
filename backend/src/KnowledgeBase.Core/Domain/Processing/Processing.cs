using System.Text.Json;
using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.Processing;

// Document processing (frames 6, 8, 25): Extract → OCR → Chunk → Embed → Index. The AI stages
// (Chunk, Embed, Index) run when Ai:Enabled is on and are recorded as skipped otherwise.

public enum ProcessingStage
{
    Extract,
    Ocr,
    Chunk,
    Embed,
    Index,
    Done,
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    DeadLettered,
}

/// <summary>One job per (DocumentVersionId, PipelineVersion); it walks the stages in order.</summary>
public sealed class ProcessingJob : Entity, IHasTimestamps
{
    public Guid DocumentVersionId { get; set; }
    public int PipelineVersion { get; set; } = 1;
    public ProcessingStage Stage { get; set; } = ProcessingStage.Extract;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Sanitized: never a stack trace or a storage credential.</summary>
    public string? LastError { get; set; }

    /// <summary>Per-stage results, e.g. {"extract":{"pages":9,"chars":18234},"embed":"skipped: AI disabled"}.</summary>
    public JsonDocument? Metrics { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Written in the same transaction as the state change; a dispatcher delivers it later.</summary>
public sealed class OutboxMessage : Entity
{
    /// <summary>e.g. "document.version-created", "submission.approved".</summary>
    public required string Type { get; set; }
    public required JsonDocument Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public enum IndexBuildStatus
{
    Pending,
    Building,
    /// <summary>The collection the alias points at; searches and new versions use it.</summary>
    Ready,
    Failed,
    Disabled,
    /// <summary>Replaced by a newer build; its collection has been dropped.</summary>
    Retired,
}

/// <summary>
/// A vector collection and how it was filled (frame 10: rebuild = new collection + alias swap). The
/// first one is created when AI is switched on; a rebuild (e.g. a new embedding model) adds another,
/// fills it in the background and swaps the alias once every indexable version is in it.
/// </summary>
public sealed class IndexBuild : Entity
{
    public required string Collection { get; set; }
    public required string Model { get; set; }
    public int Dimensions { get; set; }
    public int PipelineVersion { get; set; }
    public IndexBuildStatus Status { get; set; }
    public double Progress { get; set; }
    public int TotalVersions { get; set; }
    public int IndexedVersions { get; set; }

    /// <summary>Sanitized, like ProcessingJob.LastError.</summary>
    public string? LastError { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// One version's chunks in one vector collection. The payload hash lets the index sync rewrite a
/// point's ACL and state fields only when they changed (frame 10, "Permission changes").
/// </summary>
public sealed class VersionIndexEntry
{
    public required string Collection { get; set; }
    public Guid VersionId { get; set; }
    public Guid DocumentId { get; set; }
    public int PipelineVersion { get; set; }
    public int ChunkCount { get; set; }
    public int TokenCount { get; set; }

    /// <summary>SHA-256 of the filterable payload (ACL principals, state, folder, type, period).</summary>
    public required string PayloadHash { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>Replays the first response for a repeated Idempotency-Key from the same user and route.</summary>
public sealed class IdempotencyRecord
{
    public required string Key { get; set; }
    public Guid UserId { get; set; }
    public required string Route { get; set; }
    public required string RequestHash { get; set; }
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? ResponseContentType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
