using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Requirements;

namespace KnowledgeBase.Core.Application.Settings;

// Settings (frames 17, 19, 20): document types, the academic calendar and submission types.

/// <summary>A type as pickers show it (upload, requirement slots).</summary>
public sealed record DocumentTypeDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    AcademicTag Tag,
    TypeScope Scope,
    bool Active,
    Guid? ReplacedByTypeId);

public sealed record RetentionPolicyDto(Guid Id, string Name, int? RetainYears, RetentionTrigger Trigger, RetentionAction ActionAfter);

/// <summary>A requirement slot that asks for the type: "BSCS → Course syllabus · required".</summary>
public sealed record DocumentTypeUsageDto(
    RequirementScope ScopeType,
    Guid ScopeId,
    string ScopeCode,
    string ScopeName,
    string SubmissionTypeName,
    int SetVersion,
    bool Required,
    bool Draft);

/// <summary>Settings → Document types row (frame 17).</summary>
public sealed record DocumentTypeAdminDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string? NumberPattern,
    AcademicTag Tag,
    TypeScope Scope,
    RetentionPolicyDto? Retention,
    bool Active,
    Guid? ReplacedByTypeId,
    string? ReplacedByName,
    int DocumentCount,
    bool CodeLocked,
    IReadOnlyList<DocumentTypeUsageDto> UsedBy,
    DateTimeOffset UpdatedAt,
    string Etag);

public sealed record CreateDocumentTypeRequest(
    string Code,
    string Name,
    string? Description,
    string? NumberPattern,
    AcademicTag Tag,
    TypeScope Scope,
    Guid? RetentionPolicyId);

/// <summary>Null leaves a field unchanged; "" clears Description or NumberPattern.</summary>
public sealed record UpdateDocumentTypeRequest(
    string? Code,
    string? Name,
    string? Description,
    string? NumberPattern,
    AcademicTag? Tag,
    TypeScope? Scope,
    Guid? RetentionPolicyId,
    bool? ClearRetentionPolicy);

public sealed record RetireDocumentTypeRequest(Guid? ReplacedByTypeId);

/// <summary>Counts shown on the calendar page (frame 19); admins only.</summary>
public sealed record AcademicTermUsageDto(int Documents, int Submissions, int OpenSubmissions, int ApprovedSubmissions, int RequirementSets);

public sealed record AcademicTermDto(
    Guid Id,
    Guid AcademicYearId,
    string AcademicYearCode,
    string AcademicYearName,
    string Code,
    string Name,
    string ShortName,
    int SortOrder,
    DateOnly StartsOn,
    DateOnly EndsOn,
    TermStatus Status,
    DateOnly? SubmissionOpensOn,
    DateOnly? SubmissionClosesOn,
    bool AllowResubmitAfterClose,
    bool SubmissionWindowOpen,
    bool FromSis,
    AcademicTermUsageDto? Usage,
    string Etag);

public sealed record AcademicYearDto(
    Guid Id,
    string Code,
    string Name,
    DateOnly StartsOn,
    DateOnly EndsOn,
    bool FromSis,
    IReadOnlyList<AcademicTermDto> Terms);

/// <summary>Null leaves a field unchanged; ClearSubmissionWindow removes both window dates.</summary>
public sealed record UpdateAcademicTermRequest(
    DateOnly? SubmissionOpensOn,
    DateOnly? SubmissionClosesOn,
    bool? AllowResubmitAfterClose,
    bool? ClearSubmissionWindow);

public sealed record ApprovalStageDto(int StageOrder, ReviewerRule ReviewerRule);

public sealed record SubmissionTypeDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    bool ProgramScoped,
    bool PerCourse,
    bool DeanStage,
    bool Active,
    IReadOnlyList<ApprovalStageDto> Stages);

/// <summary>
/// What uses a submission type: submissions (Open = still in review or waiting on a revision) and
/// the published required-documents lists of programs and of colleges/departments.
/// </summary>
public sealed record SubmissionTypeUsageDto(int Submissions, int Open, int ProgramLists, int UnitLists);

/// <summary>A submission type for Settings → Submission types, with what uses it.</summary>
public sealed record SubmissionTypeAdminDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    bool ProgramScoped,
    bool PerCourse,
    bool DeanStage,
    bool Active,
    IReadOnlyList<ApprovalStageDto> Stages,
    SubmissionTypeUsageDto Usage);

/// <summary>Stages in order: stage 1 first. PerCourse (one submission per course taught) needs ProgramScoped.</summary>
public sealed record CreateSubmissionTypeRequest(
    string Code,
    string Name,
    string? Description,
    bool ProgramScoped,
    IReadOnlyList<ReviewerRule> Stages,
    bool PerCourse = false);

public sealed record UpdateSubmissionTypeRequest(
    string? Name,
    string? Description,
    bool? ProgramScoped,
    bool? Active,
    IReadOnlyList<ReviewerRule>? Stages,
    bool? PerCourse = null);
