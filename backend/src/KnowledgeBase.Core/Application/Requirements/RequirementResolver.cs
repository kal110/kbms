using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Requirements;

/// <summary>
/// <see cref="IRequirementResolver"/>: a submitter's checklist is the sets in force on their path,
/// broadest first — College → Department → Program for faculty (primary program; an AS without a
/// faculty membership uses the program they supervise), the non-academic Department for staff and heads.
/// A submission for a given program (a course's program) uses that program's path, and one for a
/// given department (faculty who are also staff) that department's.
/// </summary>
public sealed class RequirementResolver(KbDbContext db) : IRequirementResolver
{
    private sealed record UnitPath(Guid ProgramId, Guid DepartmentId, Guid? CollegeId);

    public async Task<EffectiveChecklist> GetEffectiveChecklistAsync(
        Guid userId, Guid submissionTypeId, Guid academicTermId, Guid? programId = null, Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var termStart = await db.AcademicTerms.AsNoTracking()
            .Where(t => t.Id == academicTermId)
            .Select(t => (DateOnly?)t.StartsOn)
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Academic term", academicTermId);

        var path = programId is { } p ? await ProgramPathAsync(p, ct)
            : departmentId is { } d ? await DepartmentPathAsync(d, ct)
            : await SubmitterPathAsync(userId, ct);
        if (path.Count == 0) return new EffectiveChecklist(submissionTypeId, [], []);

        var scopeIds = path.Select(p => p.Id).ToList();
        var termStarts = await db.AcademicTerms.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.StartsOn, ct);
        var sets = await db.RequirementSets.AsNoTracking()
            .Where(s => s.SubmissionTypeId == submissionTypeId && scopeIds.Contains(s.ScopeId) && s.Status != RequirementSetStatus.Draft)
            .Select(s => new { s.Id, s.ScopeType, s.ScopeId, s.SetVersion, s.EffectiveFromTermId })
            .ToListAsync(ct);

        var candidates = sets
            .Where(s => path.Contains((s.ScopeType, s.ScopeId)))
            .Select(s => new SetCandidate(s.Id, s.ScopeType, s.ScopeId, s.SetVersion,
                s.EffectiveFromTermId is { } from && termStarts.TryGetValue(from, out var start) ? start : null));
        var picked = Checklists.PickInForce(candidates, termStart);
        return await BuildAsync(submissionTypeId, picked.Select(p => p.SetId).ToList(), ct);
    }

    public async Task<EffectiveChecklist> GetPinnedChecklistAsync(IReadOnlyList<Guid> requirementSetIds, CancellationToken ct = default)
    {
        if (requirementSetIds.Count == 0) return new EffectiveChecklist(Guid.Empty, [], []);
        var typeId = await db.RequirementSets.AsNoTracking()
            .Where(s => requirementSetIds.Contains(s.Id))
            .Select(s => s.SubmissionTypeId)
            .FirstOrDefaultAsync(ct);
        return await BuildAsync(typeId, requirementSetIds, ct);
    }

    /// <summary>The scopes whose sets apply to a submitter, broadest first. Empty for people with no unit.</summary>
    public async Task<IReadOnlyList<(RequirementScope Scope, Guid Id)>> SubmitterPathAsync(Guid userId, CancellationToken ct = default)
    {
        var unit = await db.FacultyPrograms.AsNoTracking()
            .Where(f => f.UserId == userId && f.Program.IsActive)
            .OrderByDescending(f => f.IsPrimary)
            .Select(f => new UnitPath(f.ProgramId, f.Program.DepartmentId, f.Program.Department.CollegeId))
            .FirstOrDefaultAsync(ct);
        unit ??= await db.RoleAssignments.AsNoTracking()
            .Where(r => r.UserId == userId && r.IsActive && r.Role == Role.AcademicSupervisor)
            .Join(db.Programs, r => r.ScopeId, p => (Guid?)p.Id, (r, p) => new UnitPath(p.Id, p.DepartmentId, p.Department.CollegeId))
            .FirstOrDefaultAsync(ct);
        if (unit is not null) return PathOf(unit);

        return await StaffPathAsync(userId, ct);
    }

    /// <summary>College → Department → Program for one program; empty when it's inactive or unknown.</summary>
    public async Task<IReadOnlyList<(RequirementScope Scope, Guid Id)>> ProgramPathAsync(Guid programId, CancellationToken ct = default)
    {
        var unit = await db.Programs.AsNoTracking()
            .Where(p => p.Id == programId && p.IsActive)
            .Select(p => new UnitPath(p.Id, p.DepartmentId, p.Department.CollegeId))
            .FirstOrDefaultAsync(ct);
        return unit is null ? [] : PathOf(unit);
    }

    /// <summary>A non-academic department alone, or College → Department for an academic one; empty when inactive or unknown.</summary>
    private async Task<IReadOnlyList<(RequirementScope Scope, Guid Id)>> DepartmentPathAsync(Guid departmentId, CancellationToken ct)
    {
        var department = await db.Departments.AsNoTracking()
            .Where(x => x.Id == departmentId && x.IsActive)
            .Select(x => new { x.Id, x.CollegeId })
            .FirstOrDefaultAsync(ct);
        if (department is null) return [];
        return department.CollegeId is { } college
            ? [(RequirementScope.College, college), (RequirementScope.Department, department.Id)]
            : [(RequirementScope.Department, department.Id)];
    }

    private static IReadOnlyList<(RequirementScope Scope, Guid Id)> PathOf(UnitPath unit) =>
        unit.CollegeId is { } college
            ? [(RequirementScope.College, college), (RequirementScope.Department, unit.DepartmentId), (RequirementScope.Program, unit.ProgramId)]
            : [(RequirementScope.Department, unit.DepartmentId), (RequirementScope.Program, unit.ProgramId)];

    private async Task<IReadOnlyList<(RequirementScope Scope, Guid Id)>> StaffPathAsync(Guid userId, CancellationToken ct)
    {
        var department = await db.DepartmentStaff.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => (Guid?)s.DepartmentId)
            .FirstOrDefaultAsync(ct);
        department ??= await db.RoleAssignments.AsNoTracking()
            .Where(r => r.UserId == userId && r.IsActive && r.Role == Role.DepartmentHead)
            .Select(r => r.ScopeId)
            .FirstOrDefaultAsync(ct);
        return department is { } d ? [(RequirementScope.Department, d)] : [];
    }

    private async Task<EffectiveChecklist> BuildAsync(Guid submissionTypeId, IReadOnlyList<Guid> setIds, CancellationToken ct)
    {
        var sets = await db.RequirementSets.AsNoTracking().Include(s => s.Requirements)
            .Where(s => setIds.Contains(s.Id))
            .ToListAsync(ct);
        var layers = sets.Select(s => new ChecklistLayer(s.ScopeType, s.ScopeId, s.Id, s.Requirements)).ToList();
        var ordered = sets.OrderBy(s => RequirementPolicy.Rank(s.ScopeType)).Select(s => s.Id).ToList();
        return new EffectiveChecklist(submissionTypeId, ordered, Checklists.Merge(layers));
    }
}
