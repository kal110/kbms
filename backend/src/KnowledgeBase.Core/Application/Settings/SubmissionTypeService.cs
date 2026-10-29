using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Settings;

/// <summary>
/// Submission types and their approval stages (frame 20): stage 1 is usually the unit head (AS or
/// non-academic Head), with a College Dean stage for types such as accreditation and policy.
/// </summary>
public sealed partial class SubmissionTypeService(KbDbContext db, ICurrentUser currentUser, IAuditWriter audit)
{
    private const int MaxStages = 3;

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,39}$")]
    private static partial Regex CodeFormat();

    /// <summary>Active types, for the "new submission" picker.</summary>
    public async Task<IReadOnlyList<SubmissionTypeDto>> ListAsync(CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        return await QueryAsync(activeOnly: true, ct);
    }

    /// <summary>Every type, active first, with the submissions and published lists that use it.</summary>
    public async Task<IReadOnlyList<SubmissionTypeAdminDto>> ListForAdminAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var types = await QueryAsync(activeOnly: false, ct);
        var usage = await UsageAsync(null, ct);
        return types.OrderByDescending(t => t.Active).ThenBy(t => t.Name).Select(t => ToAdminDto(t, usage)).ToList();
    }

    public async Task<SubmissionTypeAdminDto> CreateAsync(CreateSubmissionTypeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var code = request.Code?.Trim().ToUpperInvariant() ?? "";
        var errors = new FieldErrors();
        errors.Check(CodeFormat().IsMatch(code), "code", "Use 2–40 capital letters, digits or underscores, starting with a letter (e.g. SYLLABUS).");
        ValidateName(request.Name, errors);
        errors.Check((request.Description?.Length ?? 0) <= 500, "description", "Use at most 500 characters.");
        ValidateStages(request.Stages, errors);
        errors.Check(!request.PerCourse || request.ProgramScoped, "perCourse", PerCourseNeedsPrograms);
        errors.ThrowIfAny();

        if (await db.SubmissionTypes.AnyAsync(t => t.Code == code, ct))
            throw new ConflictException("code-taken", "Another submission type already uses this code.").With("code", code);

        var type = new SubmissionType
        {
            Code = code,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            ProgramScoped = request.ProgramScoped,
            PerCourse = request.PerCourse,
            Stages = request.Stages.Select((rule, i) => new ApprovalStage { StageOrder = i + 1, ReviewerRule = rule }).ToList(),
        };
        db.SubmissionTypes.Add(type);
        audit.Record("submission-type.created", "submission-type", type.Id, metadata: new { type.Code, type.Name, type.PerCourse, Stages = request.Stages });
        await db.SaveChangesAsync(ct);
        return ToAdminDto(ToDto(type), await UsageAsync(type.Id, ct));
    }

    /// <summary>
    /// Stages are edited in place by order, so in-flight submissions keep their stage numbers.
    /// A type can't stop being program-scoped while programs still keep sets for it, nor become
    /// program-scoped while non-academic departments keep sets for it. One-per-course is for
    /// program-kept types only, and can't flip while submissions of the type are in review.
    /// </summary>
    public async Task<SubmissionTypeAdminDto> UpdateAsync(Guid id, UpdateSubmissionTypeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var type = await db.SubmissionTypes.Include(t => t.Stages).SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Submission type", id);

        var errors = new FieldErrors();
        if (request.Name is not null) ValidateName(request.Name, errors);
        errors.Check((request.Description?.Length ?? 0) <= 500, "description", "Use at most 500 characters.");
        if (request.Stages is not null) ValidateStages(request.Stages, errors);
        var perCourse = request.PerCourse ?? type.PerCourse;
        errors.Check(!perCourse || (request.ProgramScoped ?? type.ProgramScoped), request.PerCourse is null ? "programScoped" : "perCourse",
            request.PerCourse is null ? "Turn off “One submission per course” first: courses belong to programs." : PerCourseNeedsPrograms);
        errors.ThrowIfAny();

        if (perCourse != type.PerCourse)
        {
            var open = await db.Submissions.CountAsync(s => s.SubmissionTypeId == id && OpenStatuses.Contains(s.Status), ct);
            if (open > 0)
                throw new ConflictException("type-has-open-submissions", "Submissions of this type are in review.",
                    $"Wait until the {open} submission(s) in review are decided, then change how this type is counted.").With("open", open);
        }

        if (request.ProgramScoped == false && type.ProgramScoped)
        {
            var programSets = await db.RequirementSets.AnyAsync(s => s.SubmissionTypeId == id
                && s.ScopeType == RequirementScope.Program && s.Status != RequirementSetStatus.Superseded, ct);
            if (programSets)
                throw new ConflictException("program-sets-exist", "Programs still keep requirement sets for this type.",
                    "Remove the type from every program's required-documents list first.");
        }
        if (request.ProgramScoped == true && !type.ProgramScoped)
        {
            var officeSets = await db.RequirementSets.AnyAsync(s => s.SubmissionTypeId == id
                && s.ScopeType == RequirementScope.Department && s.Status != RequirementSetStatus.Superseded
                && db.Departments.Any(d => d.Id == s.ScopeId && d.Kind == DepartmentKind.NonAcademic), ct);
            if (officeSets)
                throw new ConflictException("office-sets-exist", "Non-academic departments still keep requirement sets for this type.",
                    "Programs and offices can't share a type: remove it from those departments' lists first.");
        }

        if (request.Name is not null) type.Name = request.Name.Trim();
        if (request.Description is not null) type.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.ProgramScoped is { } programScoped) type.ProgramScoped = programScoped;
        type.PerCourse = perCourse;
        if (request.Active is { } active) type.IsActive = active;
        if (request.Stages is { } stages)
        {
            for (var i = 0; i < stages.Count; i++)
            {
                var existing = type.Stages.SingleOrDefault(s => s.StageOrder == i + 1);
                if (existing is null) type.Stages.Add(new ApprovalStage { StageOrder = i + 1, ReviewerRule = stages[i] });
                else existing.ReviewerRule = stages[i];
            }
            foreach (var extra in type.Stages.Where(s => s.StageOrder > stages.Count).ToList())
            {
                type.Stages.Remove(extra);
                db.ApprovalStages.Remove(extra);
            }
        }

        audit.Record("submission-type.updated", "submission-type", type.Id, metadata: new
        {
            type.Code, request.Name, request.ProgramScoped, request.PerCourse, request.Active, request.Stages,
        });
        await db.SaveChangesAsync(ct);
        return ToAdminDto(ToDto(type), await UsageAsync(type.Id, ct));
    }

    private async Task<IReadOnlyList<SubmissionTypeDto>> QueryAsync(bool activeOnly, CancellationToken ct)
    {
        var types = await db.SubmissionTypes.AsNoTracking().Include(t => t.Stages)
            .Where(t => !activeOnly || t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
        return types.Select(ToDto).ToList();
    }

    public static SubmissionTypeDto ToDto(SubmissionType t)
    {
        var stages = t.Stages.OrderBy(s => s.StageOrder).Select(s => new ApprovalStageDto(s.StageOrder, s.ReviewerRule)).ToList();
        return new SubmissionTypeDto(t.Id, t.Code, t.Name, t.Description, t.ProgramScoped, t.PerCourse,
            stages.Any(s => s.ReviewerRule == ReviewerRule.CollegeDean), t.IsActive, stages);
    }

    private static readonly SubmissionStatus[] OpenStatuses =
    [
        SubmissionStatus.Submitted, SubmissionStatus.UnderReview, SubmissionStatus.RevisionRequested, SubmissionStatus.Resubmitted,
    ];

    private sealed record Usage(Dictionary<Guid, (int Total, int Open)> Submissions, Dictionary<Guid, (int Programs, int Units)> Lists);

    /// <summary>Submissions (drafts left out) and published lists per type; one type when <paramref name="typeId"/> is set.</summary>
    private async Task<Usage> UsageAsync(Guid? typeId, CancellationToken ct)
    {
        var submissions = await db.Submissions.AsNoTracking()
            .Where(s => (typeId == null || s.SubmissionTypeId == typeId) && s.Status != SubmissionStatus.Draft)
            .GroupBy(s => s.SubmissionTypeId)
            .Select(g => new { g.Key, Total = g.Count(), Open = g.Count(s => OpenStatuses.Contains(s.Status)) })
            .ToDictionaryAsync(g => g.Key, g => (g.Total, g.Open), ct);
        var lists = await db.RequirementSets.AsNoTracking()
            .Where(s => (typeId == null || s.SubmissionTypeId == typeId) && s.Status == RequirementSetStatus.Published)
            .GroupBy(s => s.SubmissionTypeId)
            .Select(g => new
            {
                g.Key,
                Programs = g.Count(s => s.ScopeType == RequirementScope.Program),
                Units = g.Count(s => s.ScopeType != RequirementScope.Program),
            })
            .ToDictionaryAsync(g => g.Key, g => (g.Programs, g.Units), ct);
        return new Usage(submissions, lists);
    }

    private static SubmissionTypeAdminDto ToAdminDto(SubmissionTypeDto t, Usage usage)
    {
        var (total, open) = usage.Submissions.GetValueOrDefault(t.Id);
        var (programs, units) = usage.Lists.GetValueOrDefault(t.Id);
        return new SubmissionTypeAdminDto(t.Id, t.Code, t.Name, t.Description, t.ProgramScoped, t.PerCourse, t.DeanStage, t.Active, t.Stages,
            new SubmissionTypeUsageDto(total, open, programs, units));
    }

    private const string PerCourseNeedsPrograms = "Only types that programs keep lists for can be counted per course.";

    private static void ValidateName(string? name, FieldErrors errors) =>
        errors.Check(!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120, "name", "Enter a name of at most 120 characters.");

    private static void ValidateStages(IReadOnlyList<ReviewerRule>? stages, FieldErrors errors)
    {
        if (stages is null || stages.Count is 0 or > MaxStages)
        {
            errors.Add("stages", $"Add 1–{MaxStages} approval stages.");
            return;
        }
        errors.Check(stages.Distinct().Count() == stages.Count, "stages", "Each reviewer can appear in only one stage.");
        errors.Check(stages.All(Enum.IsDefined), "stages", "Unknown reviewer rule.");
    }
}
