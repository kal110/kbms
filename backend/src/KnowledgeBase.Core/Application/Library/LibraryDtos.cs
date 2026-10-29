using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Workflow;

namespace KnowledgeBase.Core.Application.Library;

// Shapes of the library screens (frames 1, 2, 8, 14, 21, 22). They follow frontend/src/data/library.ts.

/// <summary>What the library shows as a document's status chip.</summary>
public enum DocumentState
{
    Draft,
    InReview,
    Published,
    Archived,
}

public sealed record DocumentTypeRef(Guid Id, string Code, string Name, AcademicTag AcademicTag);

public sealed record PersonRef(Guid Id, string DisplayName);

public sealed record FolderRef(Guid Id, string Name, FolderKind Kind);

public sealed record PeriodRef(
    Guid? AcademicYearId,
    string? AcademicYearCode,
    string? AcademicYearName,
    Guid? AcademicTermId,
    string? AcademicTermCode,
    string? AcademicTermName,
    string? AcademicTermShortName);

public sealed record VersionRef(
    Guid Id,
    int Number,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? PageCount,
    VersionProcessingStatus ProcessingStatus,
    DateTimeOffset UploadedAt);

public sealed record DocumentListItem(
    Guid Id,
    string Title,
    string? DocumentNumber,
    DocumentTypeRef Type,
    FolderRef Folder,
    PersonRef Owner,
    LifecycleStatus Lifecycle,
    DocumentState State,
    VersionRef? CurrentVersion,
    int? PublishedVersionNumber,
    PeriodRef Period,
    DateTimeOffset UpdatedAt);

/// <summary>Superseded, Published, or the current unpublished one (Draft / InReview).</summary>
public enum VersionState
{
    Superseded,
    Draft,
    InReview,
    Published,
}

public sealed record VersionDto(
    Guid Id,
    int Number,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int? PageCount,
    PersonRef UploadedBy,
    DateTimeOffset UploadedAt,
    string? ChangeDescription,
    VersionProcessingStatus ProcessingStatus,
    VersionState State,
    bool IsCurrent,
    bool IsPublished,
    int? CreatedFromVersionNumber,
    string? ApprovedInSubmission);

public sealed record SubmissionRef(Guid Id, string Number, SubmissionStatus Status, Guid DocumentVersionId);

public sealed record DocumentDetail(
    Guid Id,
    string Title,
    string? Description,
    string? DocumentNumber,
    DocumentTypeRef Type,
    FolderRef Folder,
    IReadOnlyList<FolderRef> Breadcrumbs,
    PersonRef Owner,
    LifecycleStatus Lifecycle,
    DocumentState State,
    PeriodRef Period,
    bool InheritPermissions,
    IReadOnlyList<string> Tags,
    VersionDto? CurrentVersion,
    VersionDto? PublishedVersion,
    IReadOnlyList<VersionDto> Versions,
    IReadOnlyList<SubmissionRef> Submissions,
    bool OnLegalHold,
    AccessLevel MyLevel,
    IReadOnlyList<string> AllowedActions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ETag);

public sealed record PeriodUpdate(Guid? AcademicYearId, Guid? AcademicTermId);

/// <summary>PATCH body: only the fields present change. <c>period</c> replaces year and term together.</summary>
public sealed record UpdateDocumentRequest(
    string? Title,
    string? Description,
    Guid? DocumentTypeId,
    PeriodUpdate? Period,
    IReadOnlyList<string>? Tags);

public sealed record RestoreVersionRequest(string? ChangeDescription);

public sealed record FieldChange(string Field, string? From, string? To);

public sealed record VersionComparison(VersionDto From, VersionDto To, bool SameContent, IReadOnlyList<FieldChange> Changes);

/// <summary>A short-lived signed link to the bytes (5 minutes, frame 8).</summary>
public sealed record ContentLink(string Url, DateTimeOffset ExpiresAt, Guid VersionId, int VersionNumber, string FileName, bool Inline);

/// <summary>One page of a version's extracted text, whitespace collapsed (Pages = how many it has).</summary>
public sealed record VersionPageText(Guid VersionId, int VersionNumber, int Page, int Pages, string Text);

public sealed record DocumentListQuery(
    string? Q = null,
    Guid? TypeId = null,
    Guid? YearId = null,
    Guid? TermId = null,
    DocumentState? State = null,
    bool IncludeSubfolders = false,
    string? Sort = null,
    int Page = 1,
    int PageSize = 25);

// ---- Folders ------------------------------------------------------------------------------

public sealed record FolderNode(
    Guid Id,
    Guid? ParentId,
    string Name,
    FolderKind Kind,
    bool IsSystem,
    bool IsRestricted,
    bool InheritPermissions,
    Guid? OrgRefId,
    Guid? OwnerUserId,
    Guid? DepartmentId,
    int SortOrder,
    AccessLevel MyLevel,
    bool Accessible,
    bool HasChildren,
    int DocumentCount);

/// <summary>The org record a system folder stands for: a college, program, person, year…</summary>
public sealed record OrgRef(string Kind, Guid Id, string? Code, string Name);

public sealed record FolderDetail(
    FolderNode Folder,
    IReadOnlyList<FolderRef> Breadcrumbs,
    OrgRef? Org,
    string? Context,
    IReadOnlyList<string> AllowedActions);

public sealed record FolderContents(FolderNode Folder, IReadOnlyList<FolderNode> Folders, PagedResult<DocumentListItem> Documents);

/// <summary>
/// The visible documents in a folder and below that share an academic year (none for untagged
/// types) and a type. Grouping doesn't move anything: each document stays in its submitter's folder.
/// </summary>
/// <param name="Owners">How many people the documents belong to.</param>
public sealed record DocumentGroup(
    Guid? AcademicYearId,
    string? AcademicYearCode,
    string? AcademicYearName,
    DocumentTypeRef Type,
    int Documents,
    int Owners,
    DateTimeOffset LastUpdatedAt);

/// <param name="Groups">Newest academic year first, then by type name; untagged types last.</param>
public sealed record FolderGroups(Guid FolderId, int Total, IReadOnlyList<DocumentGroup> Groups);

public sealed record CreateFolderRequest(Guid? ParentId, string Name, bool? InheritPermissions, bool? IsRestricted);

/// <summary>Rename, move (to another parent) or mark restricted. Only the fields present change.</summary>
public sealed record UpdateFolderRequest(string? Name, Guid? ParentId, bool? IsRestricted);

// ---- Uploads ------------------------------------------------------------------------------

public sealed record CreateUploadRequest(string FileName, string ContentType, long Size, string? Sha256, Guid? DocumentId);

public sealed record UploadTicket(
    Guid UploadId,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt,
    long MaxSize);

/// <summary>
/// Completes an upload: either a new document (folder, type, title, period) or a new version of
/// <c>documentId</c> (when the upload was started for a document, that one is used).
/// </summary>
public sealed record CompleteUploadRequest(
    Guid? FolderId,
    Guid? DocumentTypeId,
    string? Title,
    string? Description,
    Guid? AcademicYearId,
    Guid? AcademicTermId,
    IReadOnlyList<string>? Tags,
    Guid? DocumentId,
    string? ChangeDescription);

public sealed record StoredObject(Stream Content, string ContentType, string? FileName, bool Inline, long? Length);

// ---- Search -------------------------------------------------------------------------------

/// <param name="Mode">
/// "smart" (default): keywords in the title, number and description, plus passages close in meaning
/// once the AI index is on. "exact": the words exactly as typed (case aside), in the title, number,
/// description or text of the current version. A query in double quotes is exact too.
/// </param>
public sealed record SearchQuery(
    string? Q = null,
    Guid? TypeId = null,
    Guid? YearId = null,
    Guid? TermId = null,
    Guid? FolderId = null,
    DocumentState? State = null,
    string? Mode = null,
    int Page = 1,
    int PageSize = 20);

public static class SearchModes
{
    public const string Smart = "smart";
    public const string Exact = "exact";
}

/// <summary>What a hit was found by.</summary>
public enum SearchMatch
{
    /// <summary>Its title, number or description.</summary>
    Details,

    /// <summary>The exact phrase in the text of its current version.</summary>
    Text,

    /// <summary>A passage of its current version close in meaning to the query.</summary>
    Meaning,
}

/// <param name="Snippet">The passage that matched (Text, Meaning), else the start of the description.</param>
/// <param name="Page">The page the snippet comes from (Text, Meaning).</param>
/// <param name="PageMatches">Exact search: how many pages of the current version contain the phrase.</param>
public sealed record SearchHit(DocumentListItem Document, string? Snippet, double Score, SearchMatch Match = SearchMatch.Details, int? Page = null, int PageMatches = 0);

/// <param name="Mode">The mode that ran: "smart" or "exact".</param>
/// <param name="Semantic">Search by meaning took part (smart mode, AI index ready).</param>
/// <param name="Notice">Why search by meaning didn't take part although AI is on (e.g. the index is being built).</param>
public sealed record SearchResult(string Query, IReadOnlyList<SearchHit> Items, int Total, int Page, int PageSize, bool Semantic, string Mode, string? Notice = null);
