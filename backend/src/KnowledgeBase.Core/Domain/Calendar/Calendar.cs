using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.Calendar;

// Academic calendar (frames 19–20): AcademicYear 1—* AcademicTerm. Exactly one Active term.

public sealed class AcademicYear : Entity
{
    /// <summary>"2026-27".</summary>
    public required string Code { get; set; }
    /// <summary>"AY 2026–27".</summary>
    public required string Name { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public bool FromSis { get; set; }
    public List<AcademicTerm> Terms { get; set; } = [];
}

public enum TermStatus
{
    Planned,
    Active,
    Closed,
}

public sealed class AcademicTerm : Entity, IConcurrencyVersioned
{
    public Guid AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;

    /// <summary>"2026-1", "2026-2", "2026-S".</summary>
    public required string Code { get; set; }
    /// <summary>"1st Semester".</summary>
    public required string Name { get; set; }
    /// <summary>"1st Sem".</summary>
    public required string ShortName { get; set; }
    public int SortOrder { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public TermStatus Status { get; set; } = TermStatus.Planned;

    /// <summary>The submission window must sit inside the term.</summary>
    public DateOnly? SubmissionOpensOn { get; set; }
    public DateOnly? SubmissionClosesOn { get; set; }
    public bool AllowResubmitAfterClose { get; set; }
    public bool FromSis { get; set; }
    public uint Version { get; set; }
}
