using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Application.Settings;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Org;

/// <summary>
/// Settings → Users &amp; roles: people, the university-wide roles (System Administrator, VPAF) and
/// faculty membership. Dean, Academic Supervisor, Head and staff go through <see cref="OrgAdminService"/>.
/// People normally arrive from the identity provider; adding one here registers their sign-in id early.
/// </summary>
public sealed partial class UserAdminService(
    KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, IAcademicFolderService folders, TimeProvider clock)
{
    private const string VpafTitle = "VP for Academic Affairs";

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailFormat();

    public async Task<PagedResult<UserAdminDto>> ListAsync(
        string? query, UserRoleFilter? role, bool? active, PageRequest page, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var users = db.Users.AsNoTracking();
        if (active is { } isActive) users = users.Where(u => u.IsActive == isActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{EscapeLike(query.Trim())}%";
            users = users.Where(u => EF.Functions.ILike(u.DisplayName, pattern, "\\")
                || EF.Functions.ILike(u.ExternalId, pattern, "\\")
                || (u.Email != null && EF.Functions.ILike(u.Email, pattern, "\\")));
        }

        users = role switch
        {
            UserRoleFilter.Admin => users.Where(u => u.Roles.Any(r => r.IsActive && r.Role == Role.SystemAdmin)),
            UserRoleFilter.Vpaf => users.Where(u => u.Roles.Any(r => r.IsActive && r.Role == Role.Vpaf)),
            UserRoleFilter.Dean => users.Where(u => u.Roles.Any(r => r.IsActive && r.Role == Role.Dean)),
            UserRoleFilter.Supervisor => users.Where(u => u.Roles.Any(r => r.IsActive && r.Role == Role.AcademicSupervisor)),
            UserRoleFilter.Head => users.Where(u => u.Roles.Any(r => r.IsActive && r.Role == Role.DepartmentHead)),
            UserRoleFilter.Faculty => users.Where(u => u.Programs.Any(p => p.Program.IsActive)),
            UserRoleFilter.Staff => users.Where(u => u.StaffOf.Any()),
            UserRoleFilter.None => users.Where(u => !u.Roles.Any(r => r.IsActive) && !u.Programs.Any() && !u.StaffOf.Any()),
            _ => users,
        };

        var total = await users.CountAsync(ct);
        var rows = await users.OrderBy(u => u.DisplayName).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<UserAdminDto>(await DescribeAsync(rows, ct), page.SafePage, page.SafePageSize, total);
    }

    public async Task<UserAdminDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        return await LoadAsync(id, ct);
    }

    public async Task<UserAdminDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var externalId = request.ExternalId?.Trim() ?? "";
        var errors = new FieldErrors();
        errors.Check(externalId.Length is > 0 and <= 200 && !externalId.Any(char.IsWhiteSpace), "externalId",
            "Enter the sign-in id from the identity provider: no spaces, at most 200 characters.");
        ValidateName(request.DisplayName, errors);
        ValidateEmail(request.Email, errors);
        ValidateTitle(request.Title, errors);
        errors.ThrowIfAny();

        if (await db.Users.AnyAsync(u => u.ExternalId == externalId, ct))
            throw new ConflictException("external-id-taken", "Another user already signs in with this id.").With("externalId", externalId);

        var user = new User
        {
            ExternalId = externalId,
            DisplayName = request.DisplayName.Trim(),
            Email = Clean(request.Email),
            Title = Clean(request.Title),
        };
        db.Users.Add(user);
        audit.Record("user.created", "user", user.Id, metadata: new { user.ExternalId, user.DisplayName, user.Email });
        await db.SaveChangesAsync(ct);
        return await LoadAsync(user.Id, ct);
    }

    /// <summary>
    /// A person who holds a role can't be deactivated until someone else takes it over, so no
    /// college, program or office is left with a reviewer who can't sign in. Memberships stay, and
    /// so do their folders and documents.
    /// </summary>
    public async Task<UserAdminDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        me.RequireAdmin();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);

        var errors = new FieldErrors();
        if (request.DisplayName is not null) ValidateName(request.DisplayName, errors);
        ValidateEmail(request.Email, errors);
        ValidateTitle(request.Title, errors);
        errors.ThrowIfAny();

        if (request.Active == false && user.IsActive)
        {
            if (user.Id == me.UserId)
                throw new BusinessRuleException("deactivate-self", "You can't deactivate your own account.");
            var held = await HeldRolesAsync(user.Id, ct);
            if (held.Count > 0)
                throw new BusinessRuleException("user-holds-roles", "Hand over their roles first.",
                    $"{user.DisplayName} is still {string.Join(", ", held)}. Assign someone else or remove the role, then deactivate.");
        }

        if (request.DisplayName is not null) user.DisplayName = request.DisplayName.Trim();
        if (request.Email is not null) user.Email = Clean(request.Email);
        if (request.Title is not null) user.Title = Clean(request.Title);
        if (request.Active is { } active) user.IsActive = active;

        audit.Record("user.updated", "user", user.Id, metadata: new { request.DisplayName, request.Email, request.Title, request.Active });
        await db.SaveChangesAsync(ct);
        if (request.Active == true) await folders.SyncAsync(ct);
        return await LoadAsync(user.Id, ct);
    }

    public async Task<UserAdminDto> AddAdministratorAsync(AssignHolderRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        await RequireActiveUserAsync(request.UserId, ct);
        if (await db.RoleAssignments.AnyAsync(r => r.UserId == request.UserId && r.Role == Role.SystemAdmin && r.IsActive, ct))
            throw new ConflictException("already-admin", "This person is already a system administrator.");

        db.RoleAssignments.Add(new RoleAssignment
        {
            UserId = request.UserId,
            Role = Role.SystemAdmin,
            ScopeType = RoleScope.Global,
            Title = "System Administrator",
            AssignedAt = clock.GetUtcNow(),
        });
        audit.Record("user.admin-added", "user", request.UserId);
        await db.SaveChangesAsync(ct);
        return await LoadAsync(request.UserId, ct);
    }

    /// <summary>Nobody removes their own administrator role, so there is always at least one administrator.</summary>
    public async Task<UserAdminDto> RemoveAdministratorAsync(Guid userId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        me.RequireAdmin();
        if (userId == me.UserId)
            throw new BusinessRuleException("remove-own-admin", "You can't remove your own administrator role.",
                "Ask another administrator to do it.");

        var assignments = await db.RoleAssignments.Where(r => r.UserId == userId && r.Role == Role.SystemAdmin && r.IsActive).ToListAsync(ct);
        if (assignments.Count == 0) throw new NotFoundException("Administrator role", userId);
        foreach (var a in assignments) a.IsActive = false;
        audit.Record("user.admin-removed", "user", userId);
        await db.SaveChangesAsync(ct);
        return await LoadAsync(userId, ct);
    }

    /// <summary>The university has one VPAF: assigning the role moves it from the current holder.</summary>
    public async Task<UserAdminDto> AssignVpafAsync(AssignHolderRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        await RequireActiveUserAsync(request.UserId, ct);
        var current = await db.RoleAssignments.Where(r => r.Role == Role.Vpaf && r.IsActive).ToListAsync(ct);
        var previous = current.Where(r => r.UserId != request.UserId).ToList();
        foreach (var r in previous) r.IsActive = false;
        if (current.Count == previous.Count)
        {
            db.RoleAssignments.Add(new RoleAssignment
            {
                UserId = request.UserId,
                Role = Role.Vpaf,
                ScopeType = RoleScope.Global,
                Title = VpafTitle,
                AssignedAt = clock.GetUtcNow(),
            });
        }

        audit.Record("user.vpaf-assigned", "user", request.UserId, metadata: new { Previous = previous.Select(p => p.UserId).ToList() });
        await db.SaveChangesAsync(ct);
        return await LoadAsync(request.UserId, ct);
    }

    public async Task RemoveVpafAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var current = await db.RoleAssignments.Where(r => r.Role == Role.Vpaf && r.IsActive).ToListAsync(ct);
        if (current.Count == 0) throw new NotFoundException("VPAF assignment");
        foreach (var r in current) r.IsActive = false;
        audit.Record("user.vpaf-removed", "user", current[0].UserId, metadata: new { Previous = current.Select(p => p.UserId).ToList() });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds a person to a program's faculty and creates their faculty folder straight away. Their
    /// first program is their primary one whatever <see cref="AddFacultyRequest.IsPrimary"/> says.
    /// </summary>
    public async Task<UserAdminDto> AddFacultyAsync(Guid programId, AddFacultyRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        if (!await db.Programs.AnyAsync(p => p.Id == programId && p.IsActive, ct)) throw new NotFoundException("Program", programId);
        await RequireActiveUserAsync(request.UserId, ct);

        var memberships = await db.FacultyPrograms.Where(f => f.UserId == request.UserId).ToListAsync(ct);
        if (memberships.Any(f => f.ProgramId == programId))
            throw new ConflictException("already-faculty", "This person is already on the program's faculty.");

        var primary = request.IsPrimary || memberships.All(f => !f.IsPrimary);
        if (primary) memberships.ForEach(f => f.IsPrimary = false);
        db.FacultyPrograms.Add(new FacultyProgram { UserId = request.UserId, ProgramId = programId, IsPrimary = primary });
        audit.Record("program.faculty-added", "program", programId, metadata: new { request.UserId, IsPrimary = primary });
        await db.SaveChangesAsync(ct);
        await folders.SyncAsync(ct);
        return await LoadAsync(request.UserId, ct);
    }

    public async Task<UserAdminDto> SetPrimaryProgramAsync(Guid programId, Guid userId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var memberships = await db.FacultyPrograms.Where(f => f.UserId == userId).ToListAsync(ct);
        if (!memberships.Any(f => f.ProgramId == programId)) throw new NotFoundException("Faculty membership", userId);

        memberships.ForEach(f => f.IsPrimary = f.ProgramId == programId);
        audit.Record("program.faculty-primary", "program", programId, metadata: new { UserId = userId });
        await db.SaveChangesAsync(ct);
        return await LoadAsync(userId, ct);
    }

    /// <summary>Their folder and documents stay; they just stop submitting for the program. Another program becomes primary.</summary>
    public async Task<UserAdminDto> RemoveFacultyAsync(Guid programId, Guid userId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var memberships = await db.FacultyPrograms.Where(f => f.UserId == userId).ToListAsync(ct);
        var membership = memberships.SingleOrDefault(f => f.ProgramId == programId) ?? throw new NotFoundException("Faculty membership", userId);

        db.FacultyPrograms.Remove(membership);
        if (membership.IsPrimary && memberships.FirstOrDefault(f => f != membership) is { } next) next.IsPrimary = true;
        audit.Record("program.faculty-removed", "program", programId, metadata: new { UserId = userId });
        await db.SaveChangesAsync(ct);
        return await LoadAsync(userId, ct);
    }

    private async Task<UserAdminDto> LoadAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
        return (await DescribeAsync([user], ct))[0];
    }

    private async Task<IReadOnlyList<UserAdminDto>> DescribeAsync(IReadOnlyList<User> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var roles = await db.RoleAssignments.AsNoTracking().Where(r => ids.Contains(r.UserId) && r.IsActive).ToListAsync(ct);
        var faculty = await db.FacultyPrograms.AsNoTracking().Where(f => ids.Contains(f.UserId)).ToListAsync(ct);
        var staff = await db.DepartmentStaff.AsNoTracking().Where(s => ids.Contains(s.UserId)).ToListAsync(ct);
        var colleges = await db.Colleges.AsNoTracking().ToDictionaryAsync(c => c.Id, c => new UnitRefDto(c.Id, c.Code, c.Name), ct);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var programs = await db.Programs.AsNoTracking().Where(p => p.IsActive).ToDictionaryAsync(p => p.Id, ct);

        UnitRefDto Dept(Department d) => new(d.Id, d.Code, d.Name);
        UnitRefDto? ScopeOf(RoleAssignment r) => r.ScopeType switch
        {
            RoleScope.College when r.ScopeId is { } id => colleges.GetValueOrDefault(id),
            RoleScope.Department when r.ScopeId is { } id && departments.TryGetValue(id, out var d) => Dept(d),
            RoleScope.Program when r.ScopeId is { } id && programs.TryGetValue(id, out var p) => new UnitRefDto(p.Id, p.Code, p.Name),
            _ => null,
        };
        ProgramMembershipDto? Membership(FacultyProgram f) =>
            programs.TryGetValue(f.ProgramId, out var p) && departments.TryGetValue(p.DepartmentId, out var d)
                && d.CollegeId is { } collegeId && colleges.TryGetValue(collegeId, out var college)
                ? new ProgramMembershipDto(new UnitRefDto(p.Id, p.Code, p.Name), Dept(d), college, f.IsPrimary)
                : null;

        return users.Select(u => new UserAdminDto(
                u.Id, u.ExternalId, u.DisplayName, u.Initials, u.Email, u.Title, u.IsActive,
                roles.Where(r => r.UserId == u.Id).OrderBy(r => r.Role)
                    .Select(r => new RoleDto(r.Role, r.ScopeType, ScopeOf(r), r.Title)).ToList(),
                faculty.Where(f => f.UserId == u.Id).OrderByDescending(f => f.IsPrimary)
                    .Select(Membership).OfType<ProgramMembershipDto>().ToList(),
                staff.Where(s => s.UserId == u.Id && departments.ContainsKey(s.DepartmentId))
                    .Select(s => new StaffMembershipDto(Dept(departments[s.DepartmentId]), s.Position)).ToList(),
                u.CreatedAt))
            .ToList();
    }

    /// <summary>"Dean of CCS", "Academic Supervisor of BSCS", …: the roles that keep someone from being deactivated.</summary>
    private async Task<IReadOnlyList<string>> HeldRolesAsync(Guid userId, CancellationToken ct)
    {
        var held = (await DescribeAsync([await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct)], ct))[0].Roles;
        return held.Select(r => r.Role switch
        {
            Role.SystemAdmin => "a System Administrator",
            Role.Vpaf => "the VP for Academic Affairs",
            Role.Dean => $"Dean of {r.Scope?.Code}",
            Role.AcademicSupervisor => $"Academic Supervisor of {r.Scope?.Code}",
            _ => $"{r.Title ?? "Head"} of {r.Scope?.Code}",
        }).ToList();
    }

    private async Task RequireActiveUserAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct))
            throw new ValidationAppException("userId", "Pick an active user.");
    }

    private static void ValidateName(string? name, FieldErrors errors) =>
        errors.Check(!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 200, "displayName", "Enter a name of at most 200 characters.");

    private static void ValidateEmail(string? email, FieldErrors errors)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        errors.Check(email.Trim().Length <= 254 && EmailFormat().IsMatch(email.Trim()), "email", "Enter an email address like name@usa.edu.ph.");
    }

    private static void ValidateTitle(string? title, FieldErrors errors) =>
        errors.Check((title?.Trim().Length ?? 0) <= 120, "title", "Use at most 120 characters.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
