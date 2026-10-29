using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Org;

/// <summary>
/// Admin maintenance of role holders and staff (frames 16, 24). Folder access follows the role keys
/// (dean:, as:, head:), so changing a holder moves their access without touching any grant.
/// </summary>
public sealed class OrgAdminService(
    KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, OrgService org, IAcademicFolderService folders, TimeProvider clock)
{
    public async Task<DepartmentDetailDto> AssignDepartmentHeadAsync(Guid departmentId, AssignHeadRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var dept = await RequireNonAcademicAsync(departmentId, ct);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Head" : request.Title.Trim();
        if (title.Length > 60) throw new ValidationAppException("title", "Use at most 60 characters.");

        await AssignHolderAsync(Role.DepartmentHead, RoleScope.Department, dept.Id, request.UserId, title,
            "department.head-assigned", "department", ct);
        return await org.GetDepartmentAsync(dept.Id, ct);
    }

    public async Task<DepartmentDetailDto> RemoveDepartmentHeadAsync(Guid departmentId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var dept = await RequireNonAcademicAsync(departmentId, ct);
        await VacateAsync(Role.DepartmentHead, dept.Id, "department.head-removed", "department", ct);
        return await org.GetDepartmentAsync(dept.Id, ct);
    }

    public async Task<DepartmentDetailDto> AddStaffAsync(Guid departmentId, AddStaffRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var dept = await RequireNonAcademicAsync(departmentId, ct);
        var position = NormalizePosition(request.Position);
        await RequireActiveUserAsync(request.UserId, ct);
        if (await db.DepartmentStaff.AnyAsync(s => s.DepartmentId == dept.Id && s.UserId == request.UserId, ct))
            throw new ConflictException("already-staff", "This person is already on the department's staff.");

        db.DepartmentStaff.Add(new DepartmentStaff { DepartmentId = dept.Id, UserId = request.UserId, Position = position });
        audit.Record("department.staff-added", "department", dept.Id, metadata: new { request.UserId, Position = position });
        await db.SaveChangesAsync(ct);
        await folders.SyncAsync(ct); // their staff folder, so it shows in the library straight away
        return await org.GetDepartmentAsync(dept.Id, ct);
    }

    public async Task<DepartmentDetailDto> UpdateStaffAsync(Guid departmentId, Guid userId, UpdateStaffRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var staff = await db.DepartmentStaff.SingleOrDefaultAsync(s => s.DepartmentId == departmentId && s.UserId == userId, ct)
            ?? throw new NotFoundException("Staff member", userId);

        staff.Position = NormalizePosition(request.Position);
        audit.Record("department.staff-updated", "department", departmentId, metadata: new { UserId = userId, staff.Position });
        await db.SaveChangesAsync(ct);
        return await org.GetDepartmentAsync(departmentId, ct);
    }

    public async Task RemoveStaffAsync(Guid departmentId, Guid userId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var staff = await db.DepartmentStaff.SingleOrDefaultAsync(s => s.DepartmentId == departmentId && s.UserId == userId, ct)
            ?? throw new NotFoundException("Staff member", userId);

        db.DepartmentStaff.Remove(staff);
        audit.Record("department.staff-removed", "department", departmentId, metadata: new { UserId = userId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProgramDetailDto> AssignProgramSupervisorAsync(Guid programId, AssignHolderRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        await RequireProgramAsync(programId, ct);
        await AssignHolderAsync(Role.AcademicSupervisor, RoleScope.Program, programId, request.UserId, "Academic Supervisor",
            "program.supervisor-assigned", "program", ct);
        return await org.GetProgramAsync(programId, ct);
    }

    public async Task<ProgramDetailDto> RemoveProgramSupervisorAsync(Guid programId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        await RequireProgramAsync(programId, ct);
        await VacateAsync(Role.AcademicSupervisor, programId, "program.supervisor-removed", "program", ct);
        return await org.GetProgramAsync(programId, ct);
    }

    public async Task<OrgCollegeDto> AssignCollegeDeanAsync(Guid collegeId, AssignHolderRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        await RequireCollegeAsync(collegeId, ct);
        await AssignHolderAsync(Role.Dean, RoleScope.College, collegeId, request.UserId, "College Dean",
            "college.dean-assigned", "college", ct);
        return await CollegeAsync(collegeId, ct);
    }

    public async Task<OrgCollegeDto> RemoveCollegeDeanAsync(Guid collegeId, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        await RequireCollegeAsync(collegeId, ct);
        await VacateAsync(Role.Dean, collegeId, "college.dean-removed", "college", ct);
        return await CollegeAsync(collegeId, ct);
    }

    /// <summary>
    /// One active holder per scope (partial unique index): the previous holder is deactivated and
    /// saved first, then the new assignment is added, both in one transaction.
    /// </summary>
    private async Task AssignHolderAsync(Role role, RoleScope scopeType, Guid scopeId, Guid userId, string title,
        string action, string resourceType, CancellationToken ct)
    {
        await RequireActiveUserAsync(userId, ct);
        var current = await db.RoleAssignments.Where(r => r.Role == role && r.ScopeId == scopeId && r.IsActive).ToListAsync(ct);
        var kept = current.FirstOrDefault(r => r.UserId == userId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var previous = current.Where(r => r != kept).ToList();
        foreach (var r in previous) r.IsActive = false;
        if (previous.Count > 0) await db.SaveChangesAsync(ct);

        if (kept is null)
        {
            db.RoleAssignments.Add(new RoleAssignment
            {
                UserId = userId,
                Role = role,
                ScopeType = scopeType,
                ScopeId = scopeId,
                Title = title,
                AssignedAt = clock.GetUtcNow(),
            });
        }
        else
        {
            kept.Title = title;
        }

        audit.Record(action, resourceType, scopeId, metadata: new
        {
            Role = role, UserId = userId, Title = title, Previous = previous.Select(p => p.UserId).ToList(),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Supervisors and heads submit for their unit, so they get a person folder there straight away.
        if (role is Role.AcademicSupervisor or Role.DepartmentHead) await folders.SyncAsync(ct);
    }

    private async Task VacateAsync(Role role, Guid scopeId, string action, string resourceType, CancellationToken ct)
    {
        var current = await db.RoleAssignments.Where(r => r.Role == role && r.ScopeId == scopeId && r.IsActive).ToListAsync(ct);
        if (current.Count == 0) throw new NotFoundException($"{role} assignment", scopeId);

        foreach (var r in current) r.IsActive = false;
        audit.Record(action, resourceType, scopeId, metadata: new { Role = role, Previous = current.Select(p => p.UserId).ToList() });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Department> RequireNonAcademicAsync(Guid departmentId, CancellationToken ct)
    {
        var dept = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId && d.IsActive, ct)
            ?? throw new NotFoundException("Department", departmentId);
        if (dept.Kind == DepartmentKind.Academic)
            throw new BusinessRuleException("department-academic",
                "Academic departments have no head or staff; their programs have Academic Supervisors and faculty.");
        return dept;
    }

    private async Task RequireProgramAsync(Guid programId, CancellationToken ct)
    {
        if (!await db.Programs.AnyAsync(p => p.Id == programId && p.IsActive, ct)) throw new NotFoundException("Program", programId);
    }

    private async Task RequireCollegeAsync(Guid collegeId, CancellationToken ct)
    {
        if (!await db.Colleges.AnyAsync(c => c.Id == collegeId && c.IsActive, ct)) throw new NotFoundException("College", collegeId);
    }

    private async Task RequireActiveUserAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct))
            throw new ValidationAppException("userId", "Pick an active user.");
    }

    private async Task<OrgCollegeDto> CollegeAsync(Guid collegeId, CancellationToken ct) =>
        (await org.GetTreeAsync(ct)).Colleges.Single(c => c.Id == collegeId);

    private static string? NormalizePosition(string? position)
    {
        if (string.IsNullOrWhiteSpace(position)) return null;
        var value = position.Trim();
        return value.Length <= 120 ? value : throw new ValidationAppException("position", "Use at most 120 characters.");
    }
}
