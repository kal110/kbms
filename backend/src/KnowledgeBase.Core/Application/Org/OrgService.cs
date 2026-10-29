using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Org;

/// <summary>Who am I, and the org tree. Org structure is reference data every signed-in user may read.</summary>
public sealed class OrgService(KbDbContext db, ICurrentUser currentUser)
{
    private static readonly Role[] HolderRoles = [Role.Dean, Role.AcademicSupervisor, Role.DepartmentHead];

    public async Task<MeDto> GetMeAsync(CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == me.UserId, ct);

        var colleges = await db.Colleges.AsNoTracking().ToDictionaryAsync(c => c.Id, c => new UnitRefDto(c.Id, c.Code, c.Name), ct);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var programs = await db.Programs.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var staff = await db.DepartmentStaff.AsNoTracking().Where(s => s.UserId == me.UserId).ToListAsync(ct);

        UnitRefDto? ScopeOf(RoleGrant r) => r.ScopeType switch
        {
            RoleScope.College when r.ScopeId is { } id => colleges.GetValueOrDefault(id),
            RoleScope.Department when r.ScopeId is { } id && departments.TryGetValue(id, out var d) => new UnitRefDto(d.Id, d.Code, d.Name),
            RoleScope.Program when r.ScopeId is { } id && programs.TryGetValue(id, out var p) => new UnitRefDto(p.Id, p.Code, p.Name),
            _ => null,
        };

        var roles = me.Roles.Select(r => new RoleDto(r.Role, r.ScopeType, ScopeOf(r), r.Title)).ToList();
        var memberships = me.Programs
            .Where(p => programs.ContainsKey(p.ProgramId) && departments.ContainsKey(p.DepartmentId) && colleges.ContainsKey(p.CollegeId))
            .OrderByDescending(p => p.IsPrimary)
            .Select(p =>
            {
                var program = programs[p.ProgramId];
                var dept = departments[p.DepartmentId];
                return new ProgramMembershipDto(new UnitRefDto(program.Id, program.Code, program.Name),
                    new UnitRefDto(dept.Id, dept.Code, dept.Name), colleges[p.CollegeId], p.IsPrimary);
            })
            .ToList();
        var staffOf = staff
            .Where(s => departments.ContainsKey(s.DepartmentId))
            .Select(s =>
            {
                var d = departments[s.DepartmentId];
                return new StaffMembershipDto(new UnitRefDto(d.Id, d.Code, d.Name), s.Position);
            })
            .ToList();

        var (persona, label) = Describe(me, roles, memberships, staffOf, departments);
        return new MeDto(user.Id, user.ExternalId, user.DisplayName, user.Initials, user.Email, user.Title,
            persona, label, roles, memberships, staffOf, CapabilitiesOf(me));
    }

    public async Task<OrgTreeDto> GetTreeAsync(CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        var colleges = await db.Colleges.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).ToListAsync(ct);
        var programs = await db.Programs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
        var holders = await LoadHoldersAsync(ct);
        var facultyCounts = await db.FacultyPrograms.AsNoTracking()
            .GroupBy(f => f.ProgramId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var staffCounts = await db.DepartmentStaff.AsNoTracking()
            .GroupBy(s => s.DepartmentId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        OrgDepartmentDto Department(Department d)
        {
            var head = holders.GetValueOrDefault((Role.DepartmentHead, d.Id));
            var deptPrograms = programs.Where(p => p.DepartmentId == d.Id)
                .Select(p => new OrgProgramDto(p.Id, p.Code, p.Name, p.DepartmentId,
                    holders.GetValueOrDefault((Role.AcademicSupervisor, p.Id)).Person, facultyCounts.GetValueOrDefault(p.Id)))
                .ToList();
            return new OrgDepartmentDto(d.Id, d.Code, d.Name, d.ShortName, d.Kind, d.CollegeId, deptPrograms,
                head.Person, head.Title, staffCounts.GetValueOrDefault(d.Id));
        }

        var academic = colleges.Select(c => new OrgCollegeDto(c.Id, c.Code, c.Name,
                holders.GetValueOrDefault((Role.Dean, c.Id)).Person,
                departments.Where(d => d.Kind == DepartmentKind.Academic && d.CollegeId == c.Id).Select(Department).ToList()))
            .ToList();
        var nonAcademic = departments.Where(d => d.Kind == DepartmentKind.NonAcademic).Select(Department).ToList();
        return new OrgTreeDto(academic, nonAcademic);
    }

    public async Task<ProgramDetailDto> GetProgramAsync(Guid id, CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        var program = await db.Programs.AsNoTracking()
            .Include(p => p.Department).ThenInclude(d => d.College)
            .SingleOrDefaultAsync(p => p.Id == id && p.IsActive, ct)
            ?? throw new NotFoundException("Program", id);
        var faculty = await db.FacultyPrograms.AsNoTracking().Include(f => f.User)
            .Where(f => f.ProgramId == id && f.User.IsActive)
            .OrderBy(f => f.User.DisplayName)
            .ToListAsync(ct);
        var supervisor = await HolderAsync(Role.AcademicSupervisor, id, ct);
        var college = program.Department.College!;

        return new ProgramDetailDto(program.Id, program.Code, program.Name,
            new UnitRefDto(program.Department.Id, program.Department.Code, program.Department.Name),
            new UnitRefDto(college.Id, college.Code, college.Name),
            supervisor.Person,
            faculty.Select(f => new FacultyMemberDto(PersonDto.From(f.User), f.IsPrimary)).ToList());
    }

    public async Task<DepartmentDetailDto> GetDepartmentAsync(Guid id, CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        var dept = await db.Departments.AsNoTracking().Include(d => d.College)
            .SingleOrDefaultAsync(d => d.Id == id && d.IsActive, ct)
            ?? throw new NotFoundException("Department", id);
        var staff = await db.DepartmentStaff.AsNoTracking().Include(s => s.User)
            .Where(s => s.DepartmentId == id && s.User.IsActive)
            .OrderBy(s => s.User.DisplayName)
            .ToListAsync(ct);
        var programs = await db.Programs.AsNoTracking().Where(p => p.DepartmentId == id && p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
        var programIds = programs.Select(p => p.Id).ToList();
        var facultyCounts = await db.FacultyPrograms.AsNoTracking()
            .Where(f => programIds.Contains(f.ProgramId))
            .GroupBy(f => f.ProgramId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var holders = await LoadHoldersAsync(ct);
        var head = holders.GetValueOrDefault((Role.DepartmentHead, id));

        return new DepartmentDetailDto(dept.Id, dept.Code, dept.Name, dept.ShortName, dept.Kind,
            dept.College is { } c ? new UnitRefDto(c.Id, c.Code, c.Name) : null,
            head.Person, head.Title,
            staff.Select(s => new StaffMemberDto(PersonDto.From(s.User), s.Position)).ToList(),
            programs.Select(p => new OrgProgramDto(p.Id, p.Code, p.Name, p.DepartmentId,
                holders.GetValueOrDefault((Role.AcademicSupervisor, p.Id)).Person, facultyCounts.GetValueOrDefault(p.Id))).ToList());
    }

    /// <summary>People picker (assign a head, add staff): active users by name or email.</summary>
    public async Task<PagedResult<PersonDto>> SearchPeopleAsync(string? query, PageRequest page, CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        var users = db.Users.AsNoTracking().Where(u => u.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{EscapeLike(query.Trim())}%";
            users = users.Where(u => EF.Functions.ILike(u.DisplayName, pattern, "\\")
                || (u.Email != null && EF.Functions.ILike(u.Email, pattern, "\\")));
        }

        var total = await users.CountAsync(ct);
        var rows = await users.OrderBy(u => u.DisplayName).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<PersonDto>(rows.Select(PersonDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }

    internal async Task<(PersonDto? Person, string? Title)> HolderAsync(Role role, Guid scopeId, CancellationToken ct)
    {
        var holder = await db.RoleAssignments.AsNoTracking().Include(r => r.User)
            .Where(r => r.Role == role && r.ScopeId == scopeId && r.IsActive)
            .OrderByDescending(r => r.AssignedAt)
            .FirstOrDefaultAsync(ct);
        return holder is null ? (null, null) : (PersonDto.From(holder.User), holder.Title);
    }

    internal async Task<Dictionary<(Role, Guid), (PersonDto? Person, string? Title)>> LoadHoldersAsync(CancellationToken ct)
    {
        var assignments = await db.RoleAssignments.AsNoTracking().Include(r => r.User)
            .Where(r => r.IsActive && r.ScopeId != null && HolderRoles.Contains(r.Role))
            .ToListAsync(ct);
        return assignments
            .GroupBy(r => (r.Role, r.ScopeId!.Value))
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.OrderByDescending(x => x.AssignedAt).First();
                return ((PersonDto?)PersonDto.From(r.User), r.Title);
            });
    }

    private static IReadOnlyList<string> CapabilitiesOf(UserContext me)
    {
        var isDean = me.ScopesOf(Role.Dean).Any();
        var isSupervisor = me.ScopesOf(Role.AcademicSupervisor).Any();
        var isHead = me.ScopesOf(Role.DepartmentHead).Any();
        var isFaculty = me.Programs.Count > 0;
        var isStaff = me.StaffDepartmentIds.Count > 0;

        var caps = new List<string> { Capabilities.BrowseLibrary };
        if (isFaculty || isStaff || isSupervisor || isHead) caps.Add(Capabilities.Submit);
        // The VPAF reviews a non-academic head's own submissions (and offices with no head); an admin is the last resort.
        if (isSupervisor || isDean || isHead || me.IsVpaf || me.IsAdmin) caps.Add(Capabilities.Review);
        // Admin and VPAF set every required-documents list; an AS reads their program's list and verifies against it.
        if (me.IsAdmin || me.IsVpaf) caps.AddRange([Capabilities.SetProgramRequirements, Capabilities.SetScopedRequirements]);
        if (me.IsAdmin || me.IsVpaf || isSupervisor) caps.Add(Capabilities.ViewProgramRequirements);
        if (isSupervisor || isDean || isHead || me.IsVpaf || me.IsAdmin) caps.Add(Capabilities.ViewSubmissionDashboard);
        if (me.IsAdmin)
        {
            caps.AddRange([
                Capabilities.ManageSettings, Capabilities.ManageOrg,
                Capabilities.ViewProcessing, Capabilities.ViewAudit, Capabilities.IsAdmin,
            ]);
        }
        if (me.IsVpaf) caps.Add(Capabilities.IsVpaf);
        if (isDean) caps.Add(Capabilities.IsDean);
        if (isSupervisor) caps.Add(Capabilities.IsSupervisor);
        if (isHead) caps.Add(Capabilities.IsDepartmentHead);
        if (isFaculty) caps.Add(Capabilities.IsFaculty);
        if (isStaff) caps.Add(Capabilities.IsStaff);
        return caps;
    }

    /// <summary>The SPA persona and its role line, e.g. "Academic Supervisor · BSCS" (frontend personas.ts).</summary>
    private static (string Persona, string Label) Describe(
        UserContext me, IReadOnlyList<RoleDto> roles, IReadOnlyList<ProgramMembershipDto> programs,
        IReadOnlyList<StaffMembershipDto> staffOf, IReadOnlyDictionary<Guid, Department> departments)
    {
        string Scoped(Role role, string title) =>
            roles.FirstOrDefault(r => r.Role == role)?.Scope is { } scope ? $"{title} · {scope.Code}" : title;

        if (me.IsAdmin) return ("admin", "System Administrator");
        if (roles.FirstOrDefault(r => r.Role == Role.Vpaf) is { } vpaf) return ("vpaf", vpaf.Title ?? "VP for Academic Affairs");
        if (roles.Any(r => r.Role == Role.Dean)) return ("dean", Scoped(Role.Dean, "College Dean"));
        if (roles.Any(r => r.Role == Role.AcademicSupervisor)) return ("supervisor", Scoped(Role.AcademicSupervisor, "Academic Supervisor"));
        if (roles.FirstOrDefault(r => r.Role == Role.DepartmentHead) is { } head)
            return ("director", Scoped(Role.DepartmentHead, head.Title ?? "Head"));
        if (staffOf.Count > 0) return ("staff", $"Staff · {staffOf[0].Department.Code}");
        if (programs.Count > 0)
        {
            var dept = departments.GetValueOrDefault(programs[0].Department.Id);
            var unit = dept?.Name.StartsWith("Department of ", StringComparison.Ordinal) == true ? dept.Name["Department of ".Length..] : dept?.Name;
            return ("faculty", unit is null ? "Faculty" : $"Faculty · {unit}");
        }
        return ("faculty", "Member");
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
