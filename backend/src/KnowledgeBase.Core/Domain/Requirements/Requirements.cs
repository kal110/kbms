using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.DocumentTypes;

namespace KnowledgeBase.Core.Domain.Requirements;

// Submission types, their approval stages, and the required-document sets (frames 18, 20, 23, 24).
// A set is scoped to a College, a Department or a Program. A submitter's checklist is the union of
// the sets on their path, broadest first; lower levels can add slots but not drop inherited ones.

public sealed class SubmissionType : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>True when programs keep their own set (edited by the AS).</summary>
    public bool ProgramScoped { get; set; } = true;

    /// <summary>One submission per course the faculty teaches that term (e.g. syllabus), instead of one per person.</summary>
    public bool PerCourse { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ApprovalStage> Stages { get; set; } = [];
}

public enum ReviewerRule
{
    /// <summary>The program's Academic Supervisor, or the non-academic department's Head/Director.</summary>
    UnitHead,
    /// <summary>The Dean of the submitter's college.</summary>
    CollegeDean,
}

public sealed class ApprovalStage : Entity
{
    public Guid SubmissionTypeId { get; set; }
    public SubmissionType SubmissionType { get; set; } = null!;
    public int StageOrder { get; set; }
    public ReviewerRule ReviewerRule { get; set; }
}

public enum RequirementScope
{
    College,
    Department,
    Program,
}

public enum RequirementSetStatus
{
    Draft,
    Published,
    Superseded,
}

public sealed class RequirementSet : Entity, IHasTimestamps, IConcurrencyVersioned
{
    public RequirementScope ScopeType { get; set; }
    public Guid ScopeId { get; set; }
    public Guid SubmissionTypeId { get; set; }
    public SubmissionType SubmissionType { get; set; } = null!;

    /// <summary>1, 2, 3… per (scope, submission type). Published sets are read-only.</summary>
    public int SetVersion { get; set; }
    public RequirementSetStatus Status { get; set; } = RequirementSetStatus.Draft;
    public Guid? EffectiveFromTermId { get; set; }
    public string? Notes { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }

    public List<SubmissionRequirement> Requirements { get; set; } = [];
}

/// <summary>One slot of a set: "Course syllabus · SYL · required · exactly 1 PDF".</summary>
public sealed class SubmissionRequirement : Entity
{
    public Guid RequirementSetId { get; set; }
    public RequirementSet RequirementSet { get; set; } = null!;

    /// <summary>Stable slot key, e.g. "syllabus"; unique within the set.</summary>
    public required string Key { get; set; }
    public required string Label { get; set; }
    public Guid DocumentTypeId { get; set; }
    public DocumentType DocumentType { get; set; } = null!;
    public bool IsRequired { get; set; } = true;
    public int MinFiles { get; set; } = 1;
    public int MaxFiles { get; set; } = 1;
    public int SortOrder { get; set; }

    /// <summary>Optional due date inside the term.</summary>
    public DateOnly? DueOn { get; set; }
}
