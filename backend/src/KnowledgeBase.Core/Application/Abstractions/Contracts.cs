using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Requirements;

namespace KnowledgeBase.Core.Application.Abstractions;

// Contracts modules use to call each other. Each is implemented by exactly one module (noted per
// interface) and consumed by the others, so modules never reach into each other's tables for rules.

/// <summary>
/// Folder/document permissions with inheritance (Access module, frames 13–14). Every read path
/// filters through this, and a resource the caller can't see is reported as 404.
/// </summary>
public interface IAccessEvaluator
{
    /// <summary>The caller's effective level on a folder (None when not visible).</summary>
    Task<AccessLevel> GetFolderAccessAsync(UserContext user, Guid folderId, CancellationToken ct = default);

    /// <summary>The caller's effective level on a document (its folder's, unless it breaks inheritance).</summary>
    Task<AccessLevel> GetDocumentAccessAsync(UserContext user, Guid documentId, CancellationToken ct = default);

    /// <summary>Documents the caller has at least <paramref name="min"/> on, as a composable query.</summary>
    IQueryable<Document> VisibleDocuments(UserContext user, AccessLevel min = AccessLevel.View);

    /// <summary>Folders the caller has at least <paramref name="min"/> on, as a composable query.</summary>
    IQueryable<Folder> VisibleFolders(UserContext user, AccessLevel min = AccessLevel.View);

    /// <summary>Rebuilds FolderEffectiveAccess for a folder and everything below it.</summary>
    Task RecomputeSubtreeAsync(Guid folderId, CancellationToken ct = default);
}

/// <summary>Who the submitter is and what the document is, for auto-filing (frames 22, 24).</summary>
public sealed record SubmissionFolderRequest(
    Guid SubmitterUserId,
    Guid? ProgramId,
    Guid? DepartmentId,
    Guid AcademicTermId,
    Guid DocumentTypeId,
    Guid? CourseId = null);

/// <summary>
/// The system academic and office folder trees (Library module, frames 21–22, 24):
/// Colleges / College / Department / Program / Faculty / AY / Term and
/// Non-academic departments / Department / Staff / AY / Term.
/// </summary>
public interface IAcademicFolderService
{
    /// <summary>
    /// The folder a submitted document is filed in: the submitter's own folder, then the term folder
    /// for Term types, the year folder for Year types, or the person folder for untagged types.
    /// A per-course submission's Term-type documents go one level further, into the course folder.
    /// Missing system folders are created on the way.
    /// </summary>
    Task<Guid> ResolveSubmissionFolderAsync(SubmissionFolderRequest request, CancellationToken ct = default);

    /// <summary>Creates or repairs every system folder and its system grants from the org tree (OrgFolderSync).</summary>
    Task<OrgFolderSyncResult> SyncAsync(CancellationToken ct = default);
}

public sealed record OrgFolderSyncResult(int FoldersCreated, int FoldersUpdated, int GrantsWritten);

/// <summary>What a new document needs when it's created from an upload.</summary>
public sealed record NewDocumentSpec(
    Guid FolderId,
    Guid DocumentTypeId,
    string Title,
    Guid OwnerUserId,
    Guid? AcademicYearId,
    Guid? AcademicTermId,
    string? Description = null);

/// <summary>Turns completed uploads into documents and versions (Library module, frame 8).</summary>
public interface IDocumentFactory
{
    /// <summary>
    /// Creates a document and its v1 from a completed upload session. Validates the type's academic
    /// tag against the year/term and assigns the document number. Does not save; the caller does.
    /// </summary>
    Task<DocumentVersion> CreateDocumentAsync(Guid uploadSessionId, NewDocumentSpec spec, CancellationToken ct = default);

    /// <summary>Adds a new immutable version to an existing document from a completed upload session.</summary>
    Task<DocumentVersion> AddVersionAsync(Guid documentId, Guid uploadSessionId, string? changeDescription, CancellationToken ct = default);
}

/// <summary>A slot of the effective checklist and the set it came from.</summary>
public sealed record EffectiveRequirement(
    Guid RequirementId,
    Guid RequirementSetId,
    RequirementScope Scope,
    Guid ScopeId,
    string Key,
    string Label,
    Guid DocumentTypeId,
    bool IsRequired,
    int MinFiles,
    int MaxFiles,
    DateOnly? DueOn);

public sealed record EffectiveChecklist(
    Guid SubmissionTypeId,
    IReadOnlyList<Guid> RequirementSetIds,
    IReadOnlyList<EffectiveRequirement> Requirements);

/// <summary>Merges the published sets on the submitter's path, broadest first (Requirements module, frame 24).</summary>
public interface IRequirementResolver
{
    /// <summary>
    /// The checklist a submitter gets for a submission type: College → Department → Program sets for
    /// faculty, or the non-academic Department set for staff. Uses the sets in force for the term.
    /// With <paramref name="programId"/> (a course's program, or the program the submission is for)
    /// the path runs through that program instead of the submitter's primary one; with
    /// <paramref name="departmentId"/> (someone who is both faculty and staff, submitting for their
    /// department) it is that department's path.
    /// </summary>
    Task<EffectiveChecklist> GetEffectiveChecklistAsync(
        Guid userId, Guid submissionTypeId, Guid academicTermId, Guid? programId = null, Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>The checklist of sets already pinned on a submission.</summary>
    Task<EffectiveChecklist> GetPinnedChecklistAsync(IReadOnlyList<Guid> requirementSetIds, CancellationToken ct = default);
}

/// <summary>Queues a stored version for Extract → OCR → Chunk → Embed → Index (Admin/Processing module).</summary>
public interface IProcessingPipeline
{
    /// <summary>Adds the job and its outbox message to the current unit of work; the caller saves.</summary>
    void Enqueue(DocumentVersion version);
}

/// <summary>Adds an AuditEvent to the current unit of work, committed with the change it describes.</summary>
public interface IAuditWriter
{
    void Record(string action, string resourceType, Guid? resourceId, Guid? versionId = null, object? metadata = null, Guid? actorUserId = null);
}

/// <summary>Adds an OutboxMessage to the current unit of work (same transaction as the state change).</summary>
public interface IOutbox
{
    void Enqueue(string type, object payload);
}

/// <summary>Handles one outbox message type in process. With RabbitMQ these become queue consumers.</summary>
public interface IOutboxHandler
{
    string MessageType { get; }
    Task HandleAsync(System.Text.Json.JsonDocument payload, CancellationToken ct);
}
