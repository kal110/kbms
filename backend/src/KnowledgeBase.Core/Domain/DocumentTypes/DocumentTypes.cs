using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.DocumentTypes;

// Settings → Document types (frame 17). Every document carries exactly one type; the type's
// academic tag decides whether the document must carry a year, a term, or neither.

public enum AcademicTag
{
    None,
    Year,
    Term,
}

public enum TypeScope
{
    All,
    Academic,
    NonAcademic,
}

public sealed class DocumentType : Entity, IHasTimestamps, IConcurrencyVersioned
{
    /// <summary>Short code, e.g. "SYL". Unique; locked once any document uses the type.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>e.g. "SYL-{yyyy}-{seq:4}"; empty means documents of this type get no number.</summary>
    public string? NumberPattern { get; set; }
    public AcademicTag AcademicTag { get; set; }
    public TypeScope Scope { get; set; } = TypeScope.All;
    public Guid? RetentionPolicyId { get; set; }
    public RetentionPolicy? RetentionPolicy { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Set when the type is retired in favour of another.</summary>
    public Guid? ReplacedByTypeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public enum RetentionTrigger
{
    AfterTerm,
    AfterAcademicYear,
    AfterUpload,
    AfterSeparation,
    Permanent,
}

public enum RetentionAction
{
    Review,
    Archive,
    Delete,
}

public sealed class RetentionPolicy : Entity
{
    public required string Name { get; set; }
    public int? RetainYears { get; set; }
    public RetentionTrigger Trigger { get; set; }
    public RetentionAction ActionAfter { get; set; } = RetentionAction.Review;
}

/// <summary>Next sequence number per type and year for {seq} in the number pattern.</summary>
public sealed class DocumentNumberSequence
{
    public Guid DocumentTypeId { get; set; }
    public int Year { get; set; }
    public int NextValue { get; set; } = 1;
}
