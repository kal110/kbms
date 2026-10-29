using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Org;

namespace KnowledgeBase.Core.Domain.Library;

// Documents, immutable versions, folders, collections and tags (frames 1, 2, 11, 21, 22).

public enum FolderKind
{
    /// <summary>A hand-made library folder.</summary>
    Manual,
    /// <summary>"Colleges" — root of the academic tree.</summary>
    AcademicRoot,
    College,
    Department,
    Program,
    Faculty,
    /// <summary>"Non-academic departments" — root of the office tree.</summary>
    OfficeRoot,
    /// <summary>A non-academic department under the office root, e.g. CICT.</summary>
    Office,
    Staff,
    AcademicYear,
    Term,
    /// <summary>A course the person taught that term, under the term folder (per-course submissions).</summary>
    Course,
}

/// <summary>
/// A folder. System folders (IsSystem) are generated from the org tree and calendar, can't be
/// renamed, moved or deleted, and are unique per (ParentId, Kind, OrgRefId).
/// </summary>
public sealed class Folder : Entity, IHasTimestamps
{
    public Guid? ParentId { get; set; }
    public Folder? Parent { get; set; }
    public required string Name { get; set; }

    /// <summary>Materialized path of ancestor ids plus self: "/{rootId}/{childId}/".</summary>
    public string Path { get; set; } = "/";
    public FolderKind Kind { get; set; } = FolderKind.Manual;

    /// <summary>The org record a system folder stands for (college, department, program, user, year or term).</summary>
    public Guid? OrgRefId { get; set; }
    public bool IsSystem { get; set; }
    public bool InheritPermissions { get; set; } = true;
    public bool IsRestricted { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Folder> Children { get; set; } = [];
}

public enum LifecycleStatus
{
    Active,
    Archived,
    Deleted,
}

public sealed class Document : Entity, IHasTimestamps, IConcurrencyVersioned
{
    public required string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>e.g. "SYL-2026-0413"; unique per type when set.</summary>
    public string? DocumentNumber { get; set; }
    public Guid DocumentTypeId { get; set; }
    public DocumentType DocumentType { get; set; } = null!;
    public Guid FolderId { get; set; }
    public Folder Folder { get; set; } = null!;
    public Guid OwnerUserId { get; set; }
    public User Owner { get; set; } = null!;
    public Guid? DepartmentId { get; set; }

    /// <summary>Set as the type's academic tag needs (Year → year; Term → term + its year).</summary>
    public Guid? AcademicYearId { get; set; }
    public AcademicYear? AcademicYear { get; set; }
    public Guid? AcademicTermId { get; set; }
    public AcademicTerm? AcademicTerm { get; set; }

    public Guid? CurrentVersionId { get; set; }
    public DocumentVersion? CurrentVersion { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public DocumentVersion? PublishedVersion { get; set; }

    public LifecycleStatus LifecycleStatus { get; set; } = LifecycleStatus.Active;
    public bool InheritPermissions { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public uint Version { get; set; }

    public List<DocumentVersion> Versions { get; set; } = [];
    public List<DocumentTag> Tags { get; set; } = [];
}

public enum VersionProcessingStatus
{
    Pending,
    Processing,
    /// <summary>Processed (text extracted); not in the semantic index, e.g. while AI is off.</summary>
    Ready,
    Failed,
    /// <summary>Its chunks are in the active vector collection: the assistant can cite it (frame 8, step 12).</summary>
    Indexed,
}

/// <summary>Immutable once written: ObjectKey and Sha256 never change (DB trigger).</summary>
public sealed class DocumentVersion : Entity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    /// <summary>1, 2, 3… UNIQUE (DocumentId, VersionNumber).</summary>
    public int VersionNumber { get; set; }

    /// <summary>Storage key of the original bytes. UNIQUE, never updated.</summary>
    public required string ObjectKey { get; set; }
    public required string OriginalFilename { get; set; }
    public required string ContentType { get; set; }
    public long FileSizeBytes { get; set; }

    /// <summary>Lower-case hex, 64 chars (CHECK).</summary>
    public required string Sha256 { get; set; }
    public int? PageCount { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public string? ChangeDescription { get; set; }
    public VersionProcessingStatus ProcessingStatus { get; set; } = VersionProcessingStatus.Pending;

    /// <summary>Storage key of the extracted text, once the Extract stage ran.</summary>
    public string? TextObjectKey { get; set; }

    /// <summary>Restore lineage: the version this one was copied from.</summary>
    public Guid? CreatedFromVersionId { get; set; }
}

/// <summary>
/// One page of a version's extracted text, as exact search matches it: blocks joined and
/// whitespace collapsed (<see cref="PlainText.Collapse"/>). Written by the Extract stage, one row
/// per page (empty pages included), so a version with rows has been read.
/// </summary>
public sealed class VersionPage
{
    public Guid VersionId { get; set; }
    public int PageNumber { get; set; }
    public required string Text { get; set; }
}

public sealed class Collection : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool InheritPermissions { get; set; }
    public List<CollectionDocument> Documents { get; set; } = [];
}

public sealed class CollectionDocument
{
    public Guid CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
}

public sealed class Tag : Entity
{
    public required string Name { get; set; }
}

public sealed class DocumentTag
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

/// <summary>Blocks deletion and retention actions while active.</summary>
public sealed class LegalHold : Entity
{
    public Guid DocumentId { get; set; }
    public required string Reason { get; set; }
    public Guid PlacedByUserId { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public enum PublicationStatus
{
    Pending,
    Published,
    Unpublished,
}

/// <summary>History of which exact version was published, by whom and why (frame 9).</summary>
public sealed class PublicationRecord : Entity
{
    public Guid DocumentId { get; set; }
    public Guid DocumentVersionId { get; set; }
    public Guid? SubmissionId { get; set; }
    public PublicationStatus Status { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum UploadStatus
{
    Pending,
    Uploaded,
    Completed,
    Expired,
    Failed,
}

/// <summary>
/// One upload of one file (frame 8). The browser sends the bytes straight to storage through a
/// short-lived signed URL; "complete" then turns the stored object into a DocumentVersion.
/// </summary>
public sealed class UploadSession : Entity
{
    public Guid UserId { get; set; }

    /// <summary>Existing document when uploading a new version; null for a new document.</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>The version created on completion.</summary>
    public Guid? VersionId { get; set; }
    public required string ObjectKey { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long ExpectedSize { get; set; }
    public string? ExpectedSha256 { get; set; }

    /// <summary>S3 multipart upload id; null for the local disk store.</summary>
    public string? S3UploadId { get; set; }
    public UploadStatus Status { get; set; } = UploadStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
