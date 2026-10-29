using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;

namespace KnowledgeBase.Core.Application.Requirements;

// Required-document sets (frames 18, 20, 23, 24). Item shapes follow the SPA's Requirement
// (frontend/src/data/requirements.ts): rule Required/Optional, files { min, max }, due.

public enum RequirementRule
{
    Required,
    Optional,
}

public sealed record FileLimit(int Min, int Max);

public sealed record ScopeSourceDto(RequirementScope Type, Guid Id, string Code, string Name);

/// <summary>A checklist slot. <see cref="Source"/> is set when the slot comes from a broader scope.</summary>
public sealed record RequirementItemDto(
    Guid Id,
    string Key,
    string Label,
    Guid DocumentTypeId,
    string DocumentTypeCode,
    string DocumentTypeName,
    RequirementRule Rule,
    FileLimit Files,
    DateOnly? Due,
    ScopeSourceDto? Source);

public sealed record RequirementItemInput(
    string? Key,
    string Label,
    Guid DocumentTypeId,
    RequirementRule Rule,
    FileLimit Files,
    DateOnly? Due);

public sealed record PersonRefDto(Guid Id, string DisplayName);

public sealed record TermRefDto(Guid Id, string Code, string Name, string ShortName, string AcademicYearName);

public sealed record SubmissionTypeRefDto(Guid Id, string Code, string Name, bool ProgramScoped, bool DeanStage);

public sealed record RequirementSetDto(
    Guid Id,
    ScopeSourceDto Scope,
    SubmissionTypeRefDto SubmissionType,
    int Version,
    RequirementSetStatus Status,
    TermRefDto? EffectiveFrom,
    string? Notes,
    PersonRefDto? CreatedBy,
    PersonRefDto? PublishedBy,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt,
    int OpenSubmissions,
    IReadOnlyList<RequirementItemDto> Items,
    string Etag);

/// <summary>One submission type at a scope: what is published, the draft being edited, and what the scope inherits.</summary>
public sealed record ScopeSetGroupDto(
    SubmissionTypeRefDto SubmissionType,
    RequirementSetDto? Published,
    RequirementSetDto? Draft,
    IReadOnlyList<RequirementItemDto> Inherited);

public sealed record ScopeRequirementSetsDto(
    ScopeSourceDto Scope,
    DepartmentKind? Kind,
    bool CanManage,
    IReadOnlyList<ScopeSetGroupDto> Sets,
    IReadOnlyList<SubmissionTypeRefDto> AvailableTypes);

public sealed record CreateRequirementSetRequest(Guid SubmissionTypeId, Guid? CopyFromSetId);

/// <summary>Replaces the draft's notes, effective term and full item list.</summary>
public sealed record UpdateRequirementSetRequest(string? Notes, Guid? EffectiveFromTermId, IReadOnlyList<RequirementItemInput> Items);

public sealed record RequirementScopeNodeDto(
    RequirementScope ScopeType,
    Guid Id,
    string Code,
    string Name,
    DepartmentKind? Kind,
    bool CanManage,
    int PublishedCount,
    int DraftCount,
    IReadOnlyList<RequirementScopeNodeDto> Children);

/// <summary>
/// Scopes the caller sets or reads lists for: colleges → departments → programs and non-academic departments
/// (admin, VPAF), or the programs an AS supervises (read-only).
/// </summary>
public sealed record RequirementScopeTreeDto(IReadOnlyList<RequirementScopeNodeDto> Academic, IReadOnlyList<RequirementScopeNodeDto> NonAcademic);

public sealed record EffectiveChecklistDto(
    SubmissionTypeRefDto SubmissionType,
    TermRefDto Term,
    IReadOnlyList<Guid> RequirementSetIds,
    IReadOnlyList<RequirementItemDto> Items);
