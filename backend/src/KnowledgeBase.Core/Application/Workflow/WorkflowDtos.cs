using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;

namespace KnowledgeBase.Core.Application.Workflow;

// Requests and responses of the Workflow module. Shapes follow the frontend's mock data
// (frontend/src/data/submissions.ts) so the SPA can swap its stores for these endpoints.

// ---------------------------------------------------------------- requests

/// <summary>
/// CourseId is required for per-course types and must be a course the caller teaches that term; it sets the program.
/// DepartmentId files it under a department the caller works in or heads instead of their program (faculty who are also staff).
/// </summary>
public sealed record CreateSubmissionRequest(
    Guid SubmissionTypeId, string Title, Guid? AcademicTermId = null, Guid? ProgramId = null, Guid? CourseId = null,
    Guid? DepartmentId = null);

public sealed record UpdateSubmissionRequest(string? Title);

/// <summary>
/// Attach a completed upload to a checklist slot. Pass <paramref name="DocumentId"/> to add a new
/// version of a file already in the slot; a full single-file slot gets a new version automatically.
/// </summary>
public sealed record AddAttachmentRequest(
    string RequirementKey,
    Guid UploadSessionId,
    Guid? DocumentId = null,
    string? Title = null,
    string? ChangeDescription = null);

/// <summary>A reviewer's decision, with the exact versions that were on screen.</summary>
public sealed record DecisionRequest(string? Comments, IReadOnlyList<Guid>? ReviewedVersionIds);

public sealed record ResubmitRequest(string? Comments);

public sealed record ReassignRequest(Guid ReviewerUserId, string? Reason);

/// <summary>
/// scope: mine (default) · reviewing (assigned to me now or before) · unit (programs I supervise,
/// departments I head, colleges I lead) · college (Dean or admin; optional collegeId).
/// </summary>
public sealed record SubmissionListQuery(
    string? Scope = null,
    SubmissionStatus? Status = null,
    Guid? TermId = null,
    Guid? TypeId = null,
    Guid? CollegeId = null,
    string? Q = null,
    int Page = 1,
    int PageSize = 25);

// ---------------------------------------------------------------- shared refs

public sealed record PersonRef(Guid Id, string Name, string? Title);

public sealed record TypeRef(Guid Id, string Code, string Name);

/// <summary>The course a per-course submission is for.</summary>
public sealed record CourseRef(Guid Id, string Code, string Title);

public sealed record TermRef(Guid Id, string Code, string Name, string ShortName, string YearCode, string YearName, TermStatus Status);

/// <summary>Kind is "Program" or "Department".</summary>
public sealed record UnitRef(
    string Kind,
    Guid Id,
    string Code,
    string Name,
    Guid DepartmentId,
    string DepartmentName,
    Guid? CollegeId,
    string? CollegeCode);

// ---------------------------------------------------------------- list and detail

public sealed record SubmissionRow(
    Guid Id,
    string Number,
    string Title,
    SubmissionStatus Status,
    TypeRef SubmissionType,
    CourseRef? Course,
    TermRef Term,
    PersonRef Submitter,
    UnitRef Unit,
    int CurrentStage,
    PersonRef? Reviewer,
    int AttachmentCount,
    int? MissingRequired,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? FinalizedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SubmissionDetail(
    Guid Id,
    string Number,
    string Title,
    SubmissionStatus Status,
    TypeRef SubmissionType,
    CourseRef? Course,
    TermRef Term,
    PersonRef Submitter,
    UnitRef Unit,
    int CurrentStage,
    IReadOnlyList<StageInfo> Stages,
    AssignmentInfo? Assignment,
    IReadOnlyList<PinnedSetRef> RequirementSets,
    IReadOnlyList<ChecklistItem> Checklist,
    IReadOnlyList<Guid> CurrentVersionIds,
    IReadOnlyList<ReviewerComment> Comments,
    IReadOnlyList<string> AllowedActions,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? FinalizedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

/// <summary>State: Upcoming · Current · Done · Skipped. Reviewer is the assignee, or the predicted one.</summary>
public sealed record StageInfo(int Order, ReviewerRule Rule, string State, PersonRef? Reviewer, AssignmentReason? Reason, DateTimeOffset? CompletedAt);

public sealed record AssignmentInfo(int StageOrder, PersonRef Reviewer, AssignmentReason Reason, DateTimeOffset AssignedAt);

public sealed record PinnedSetRef(Guid Id, RequirementScope Scope, Guid ScopeId, int SetVersion);

/// <summary>A checklist slot; Source is null for files whose slot isn't in the pinned sets any more.</summary>
public sealed record ChecklistItem(
    string Key,
    string Label,
    TypeRef? DocumentType,
    bool IsRequired,
    int MinFiles,
    int MaxFiles,
    DateOnly? DueOn,
    RequirementScope? Source,
    bool Satisfied,
    IReadOnlyList<AttachmentInfo> Attachments);

/// <summary>
/// One file in a slot. VersionNumber is what the submission points at now; Pinned is what was
/// last submitted, Reviewed what the reviewer last decided on, Published what the library shows.
/// </summary>
public sealed record AttachmentInfo(
    Guid Id,
    Guid DocumentId,
    string DocumentTitle,
    Guid VersionId,
    int VersionNumber,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? PageCount,
    string Sha256,
    DateTimeOffset UploadedAt,
    int? PinnedVersionNumber,
    int? ReviewedVersionNumber,
    int? PublishedVersionNumber);

public sealed record ReviewerComment(PersonRef Author, ReviewActionType Action, int Stage, string Body, DateTimeOffset At);

/// <summary>Kind is a ReviewActionType name or "Uploaded"; Tone is neutral · info · warning · success · danger · pending.</summary>
public sealed record TimelineEntry(
    DateTimeOffset At,
    string Kind,
    string Text,
    string Tone,
    PersonRef? Actor,
    int? Stage,
    string? Comment,
    IReadOnlyList<VersionRef> Versions);

public sealed record VersionRef(Guid DocumentId, Guid VersionId, int VersionNumber, string Label);

// ---------------------------------------------------------------- review queue

public sealed record ReviewQueueItem(
    Guid Id,
    string Number,
    string Title,
    SubmissionStatus Status,
    TypeRef SubmissionType,
    CourseRef? Course,
    TermRef Term,
    PersonRef Submitter,
    UnitRef Unit,
    int Stage,
    string StageLabel,
    AssignmentReason Reason,
    string? StageNote,
    DateTimeOffset WaitingSince,
    double WaitingHours,
    int WaitingDays,
    bool Overdue,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ResubmittedAt,
    DateTimeOffset? LastReviewedAt,
    IReadOnlyList<QueueAttachment> Attachments);

public sealed record QueueAttachment(
    string RequirementKey,
    string Label,
    string DocumentTypeCode,
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    int? PreviousVersionNumber,
    string Sha256,
    int? PageCount);

// ---------------------------------------------------------------- submissions dashboard: shared items

public sealed record StatusCounts(int Submitted, int UnderReview, int Resubmitted, int RevisionRequested, int Approved, int Rejected);

/// <summary>UnitCode: the program or non-academic department. UnitHeadVacant: routed past a vacant AS or Head.</summary>
public sealed record OverdueItem(
    Guid SubmissionId,
    string Number,
    string Title,
    string UnitCode,
    PersonRef Submitter,
    PersonRef? Reviewer,
    bool ReviewerIsYou,
    bool UnitHeadVacant,
    int AgeDays);

public sealed record ActivityItem(DateTimeOffset At, ReviewActionType Action, PersonRef Actor, Guid SubmissionId, string Number, string Title, string UnitCode);

// ---------------------------------------------------------------- submissions dashboard (AS, Dean, Head, VPAF)

/// <summary>
/// University → College → Program for academic units; NonAcademic (every non-academic department) → Department
/// (one of them, by staff member) for offices like CICT.
/// </summary>
public enum DashboardLevel
{
    University,
    College,
    Program,
    NonAcademic,
    Department,
}

/// <summary>A dashboard scope; Id is null for the two roots (the university's colleges and the non-academic departments).</summary>
public sealed record DashboardScopeRef(DashboardLevel Level, Guid? Id, string Code, string Name);

/// <summary>
/// A scope's submissions for one term. Totals is the scope itself as a row; Rows break it down one level:
/// colleges (university), programs grouped by department (college), departments (non-academic) or people
/// (program, department). Trail lists the levels above that the caller may open; Options, the caller's other
/// scopes (for the VPAF and admin, the two roots).
/// </summary>
public sealed record SubmissionDashboard(
    DashboardLevel Level,
    DashboardScopeRef Scope,
    IReadOnlyList<DashboardScopeRef> Trail,
    IReadOnlyList<DashboardScopeRef> Options,
    TermRef Term,
    DashboardRow Totals,
    IReadOnlyList<DashboardRow> Rows,
    IReadOnlyList<DashboardTypeRow> ByType,
    IReadOnlyList<OverdueItem> Overdue,
    IReadOnlyList<ActivityItem> RecentActivity);

/// <summary>
/// Lead: the Dean, AS or Head. FacultyCount: people expected to submit (the term's teaching load for the program, or its
/// faculty when the term has no load; a non-academic department's staff); CompliancePercent: share of them with at
/// least one submission this term.
/// CourseExpected: one per course taught × per-course type the program has a list for; CourseSubmitted: those with
/// a live submission. For a person, Code is their title.
/// </summary>
public sealed record DashboardRow(
    Guid Id,
    string Code,
    string Name,
    string? Group,
    PersonRef? Lead,
    int FacultyCount,
    int FacultySubmitted,
    int CompliancePercent,
    int CourseExpected,
    int CourseSubmitted,
    int Submissions,
    int Open,
    int Overdue,
    double? AvgDaysToDecision,
    int Published,
    int PublishedLast7,
    StatusCounts ByStatus,
    DateTimeOffset? LastSubmittedAt);

public sealed record DashboardTypeRow(TypeRef Type, int Submissions, int Open, int Overdue, StatusCounts ByStatus);

// ---------------------------------------------------------------- notifications

public sealed record NotificationItem(Guid Id, string Type, string Title, string? Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationSummary(int Unread);
