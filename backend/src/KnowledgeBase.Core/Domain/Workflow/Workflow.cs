using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;

namespace KnowledgeBase.Core.Domain.Workflow;

// Submissions and their review (frames 3, 4, 9, 16). The exact document versions are pinned on
// submit, and every decision records which versions the reviewer actually saw.

public enum SubmissionStatus
{
    Draft,
    Submitted,
    UnderReview,
    RevisionRequested,
    Resubmitted,
    Approved,
    Rejected,
    Withdrawn,
}

public sealed class Submission : Entity, IHasTimestamps, IConcurrencyVersioned
{
    /// <summary>"SUB-2026-0187". Unique.</summary>
    public required string SubmissionNumber { get; set; }
    public required string Title { get; set; }
    public Guid SubmittedByUserId { get; set; }
    public User SubmittedBy { get; set; } = null!;

    /// <summary>Faculty submissions belong to a program (→ department → college).</summary>
    public Guid? ProgramId { get; set; }
    public AcademicProgram? Program { get; set; }

    /// <summary>Staff submissions of a non-academic department (frame 24).</summary>
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>The course it is for, when its type is per course; one of the submitter's courses that term.</summary>
    public Guid? CourseId { get; set; }
    public Course? Course { get; set; }

    public Guid AcademicTermId { get; set; }
    public AcademicTerm AcademicTerm { get; set; } = null!;
    public Guid AcademicYearId { get; set; }
    public Guid SubmissionTypeId { get; set; }
    public SubmissionType SubmissionType { get; set; } = null!;

    public SubmissionStatus Status { get; set; } = SubmissionStatus.Draft;
    public Guid? AssignedReviewerId { get; set; }
    public User? AssignedReviewer { get; set; }

    /// <summary>1-based order of the approval stage the submission is at.</summary>
    public int CurrentStage { get; set; } = 1;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }

    public List<SubmissionRequirementSet> RequirementSets { get; set; } = [];
    public List<SubmissionDocument> Documents { get; set; } = [];
    public List<ReviewAction> Actions { get; set; } = [];
    public List<SubmissionAssignment> Assignments { get; set; } = [];
}

/// <summary>The requirement sets pinned when the submission was created (college + dept + program).</summary>
public sealed class SubmissionRequirementSet
{
    public Guid SubmissionId { get; set; }
    public Guid RequirementSetId { get; set; }
    public RequirementSet RequirementSet { get; set; } = null!;
}

/// <summary>A file in a checklist slot. The composite FK makes the version belong to the document.</summary>
public sealed class SubmissionDocument : Entity
{
    public Guid SubmissionId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid DocumentVersionId { get; set; }

    /// <summary>Slot key of the pinned set; a slot can hold several files up to its MaxFiles.</summary>
    public required string RequirementKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ReviewActionType
{
    Created,
    Submitted,
    Opened,
    Commented,
    Approved,
    RevisionRequested,
    Rejected,
    Resubmitted,
    Withdrawn,
    Published,
    Reassigned,
}

/// <summary>Append-only (a DB trigger blocks UPDATE/DELETE). Comments are required to request revision or reject.</summary>
public sealed class ReviewAction : Entity
{
    public Guid SubmissionId { get; set; }
    public Guid ActorUserId { get; set; }
    public ReviewActionType ActionType { get; set; }
    public int StageOrder { get; set; }
    public SubmissionStatus? PreviousStatus { get; set; }
    public SubmissionStatus? NewStatus { get; set; }
    public string? Comments { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The exact versions on screen when the action was taken.</summary>
    public List<Guid> DocumentVersionIds { get; set; } = [];
}

public enum AssignmentReason
{
    Rule,
    /// <summary>Stage-1 fallback to the dean when the program has no active AS (frame 16).</summary>
    Fallback,
    Manual,
}

public sealed class SubmissionAssignment : Entity
{
    public Guid SubmissionId { get; set; }
    public int StageOrder { get; set; }
    public Guid ReviewerUserId { get; set; }
    public AssignmentReason Reason { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class Notification : Entity
{
    public Guid UserId { get; set; }

    /// <summary>e.g. "submission.revision-requested".</summary>
    public required string Type { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }

    /// <summary>App route the notification opens, e.g. "/submissions/{id}".</summary>
    public string? Link { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
