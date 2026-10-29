using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Requirements;

/// <summary>A college, department or program a requirement set can be scoped to, with its parents.</summary>
public sealed record ScopeInfo(
    RequirementScope Type,
    Guid Id,
    string Code,
    string Name,
    DepartmentKind? Kind,
    Guid? CollegeId,
    Guid? DepartmentId)
{
    public ScopeSourceDto ToSource() => new(Type, Id, Code, Name);
}

/// <summary>Looks up scope names and parents. The org tables are small, so lookups load them whole.</summary>
public sealed class ScopeDirectory(KbDbContext db)
{
    private Dictionary<(RequirementScope, Guid), ScopeInfo>? _all;

    public static RequirementScope Parse(string scope) => scope.ToLowerInvariant() switch
    {
        "college" or "colleges" => RequirementScope.College,
        "department" or "departments" => RequirementScope.Department,
        "program" or "programs" => RequirementScope.Program,
        _ => throw new NotFoundException("Scope", scope),
    };

    public async Task<ScopeInfo?> FindAsync(RequirementScope type, Guid id, CancellationToken ct = default) =>
        (await AllAsync(ct)).GetValueOrDefault((type, id));

    public async Task<ScopeInfo> GetAsync(RequirementScope type, Guid id, CancellationToken ct = default) =>
        await FindAsync(type, id, ct) ?? throw new NotFoundException(type.ToString(), id);

    public async Task<IReadOnlyCollection<ScopeInfo>> ListAsync(CancellationToken ct = default) => (await AllAsync(ct)).Values;

    private async Task<Dictionary<(RequirementScope, Guid), ScopeInfo>> AllAsync(CancellationToken ct)
    {
        if (_all is not null) return _all;

        var colleges = await db.Colleges.AsNoTracking().Where(c => c.IsActive)
            .Select(c => new ScopeInfo(RequirementScope.College, c.Id, c.Code, c.Name, null, c.Id, null))
            .ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive)
            .Select(d => new ScopeInfo(RequirementScope.Department, d.Id, d.Code, d.Name, d.Kind, d.CollegeId, d.Id))
            .ToListAsync(ct);
        var programs = await db.Programs.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new ScopeInfo(RequirementScope.Program, p.Id, p.Code, p.Name, DepartmentKind.Academic, p.Department.CollegeId, p.DepartmentId))
            .ToListAsync(ct);

        return _all = colleges.Concat(departments).Concat(programs).ToDictionary(s => (s.Type, s.Id));
    }
}

/// <summary>Who may edit or read a scope's requirement sets (frames 18, 23, 24).</summary>
public static class RequirementPolicy
{
    /// <summary>
    /// Only the admin and the VPAF set lists, for every college, department and program. An Academic Supervisor or a
    /// non-academic Head reads their unit's list and verifies submissions against it, but doesn't set it.
    /// </summary>
    public static bool CanManage(UserContext me, ScopeInfo scope) => me.IsAdmin || me.IsVpaf;

    /// <summary>Managers, the Dean of the scope's college and its members (an AS of their program, a Head of their department).</summary>
    public static bool CanView(UserContext me, ScopeInfo scope) =>
        CanManage(me, scope)
        || (scope.CollegeId is { } college && me.IsDeanOf(college))
        // Members read the lists of their own units, so an AS sees what their program inherits.
        || me.PrincipalKeys.Contains(MemberKey(scope));

    private static string MemberKey(ScopeInfo scope) => scope.Type switch
    {
        RequirementScope.College => PrincipalKeys.College(scope.Id),
        RequirementScope.Department => PrincipalKeys.Department(scope.Id),
        _ => PrincipalKeys.Program(scope.Id),
    };

    /// <summary>Breadth order used when merging checklists: college, then department, then program.</summary>
    public static int Rank(RequirementScope scope) => scope switch
    {
        RequirementScope.College => 0,
        RequirementScope.Department => 1,
        _ => 2,
    };
}
