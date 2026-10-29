using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Security;

/// <summary>
/// Supplies who is calling. The API reads it from the validated JWT; a future MCP host or a
/// background job supplies its own. Core never touches HttpContext.
/// </summary>
public interface IIdentitySource
{
    /// <summary>The IdP subject (User.ExternalId), or null when anonymous.</summary>
    string? ExternalId { get; }
    string? IpAddress { get; }
}

/// <summary>A role the user holds and the unit it is scoped to.</summary>
public sealed record RoleGrant(Role Role, RoleScope ScopeType, Guid? ScopeId, string? Title);

/// <summary>Everything authorization needs about the caller, loaded once per request.</summary>
public sealed class UserContext
{
    public required Guid UserId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<RoleGrant> Roles { get; init; }

    /// <summary>Programs the user teaches in, with their department and college.</summary>
    public required IReadOnlyList<ProgramMembership> Programs { get; init; }

    /// <summary>Non-academic departments the user is staff of.</summary>
    public required IReadOnlyList<Guid> StaffDepartmentIds { get; init; }
    public required IReadOnlyList<Guid> GroupIds { get; init; }

    /// <summary>Every principal key the caller matches grants with (see <see cref="PrincipalKeys"/>).</summary>
    public required IReadOnlySet<string> PrincipalKeys { get; init; }

    public bool IsAdmin => Roles.Any(r => r.Role == Role.SystemAdmin);
    public bool IsVpaf => Roles.Any(r => r.Role == Role.Vpaf);
    public bool IsDeanOf(Guid collegeId) => Has(Role.Dean, collegeId);
    public bool IsSupervisorOf(Guid programId) => Has(Role.AcademicSupervisor, programId);
    public bool IsHeadOf(Guid departmentId) => Has(Role.DepartmentHead, departmentId);
    public IEnumerable<Guid> ScopesOf(Role role) => Roles.Where(r => r.Role == role && r.ScopeId is not null).Select(r => r.ScopeId!.Value);

    private bool Has(Role role, Guid scopeId) => Roles.Any(r => r.Role == role && r.ScopeId == scopeId);
}

public sealed record ProgramMembership(Guid ProgramId, Guid DepartmentId, Guid CollegeId, bool IsPrimary);

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? IpAddress { get; }

    /// <summary>The caller's user id once <see cref="GetAsync"/> has run in this scope; null before.</summary>
    Guid? LoadedUserId { get; }

    /// <summary>Loads (and caches for the scope) the caller's context. Throws 401 when anonymous or unknown.</summary>
    Task<UserContext> GetAsync(CancellationToken ct = default);
}

/// <summary>Scoped: resolves the identity to a User row plus roles and memberships.</summary>
public sealed class CurrentUser(IIdentitySource identity, KbDbContext db) : ICurrentUser
{
    private UserContext? _cached;

    public bool IsAuthenticated => !string.IsNullOrEmpty(identity.ExternalId);
    public string? IpAddress => identity.IpAddress;
    public Guid? LoadedUserId => _cached?.UserId;

    public async Task<UserContext> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var externalId = identity.ExternalId ?? throw new UnauthenticatedException();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.ExternalId == externalId && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName })
            .SingleOrDefaultAsync(ct)
            ?? throw new UnauthenticatedException("The signed-in account isn't registered in the knowledge base.");

        var roles = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.UserId == user.Id && r.IsActive)
            .Select(r => new RoleGrant(r.Role, r.ScopeType, r.ScopeId, r.Title))
            .ToListAsync(ct);

        var programs = await db.FacultyPrograms.AsNoTracking()
            .Where(fp => fp.UserId == user.Id && fp.Program.IsActive)
            .Select(fp => new ProgramMembership(fp.ProgramId, fp.Program.DepartmentId, fp.Program.Department.CollegeId!.Value, fp.IsPrimary))
            .ToListAsync(ct);

        var staffOf = await db.DepartmentStaff.AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .Select(s => s.DepartmentId)
            .ToListAsync(ct);

        var groups = await db.GroupMembers.AsNoTracking()
            .Where(g => g.UserId == user.Id)
            .Select(g => g.GroupId)
            .ToListAsync(ct);

        // Scoped roles also make the holder a member of their unit, e.g. an AS is in prog:/dept:/college:.
        var supervised = roles.Where(r => r.Role == Role.AcademicSupervisor && r.ScopeId is not null).Select(r => r.ScopeId!.Value).ToList();
        var supervisedUnits = await db.Programs.AsNoTracking()
            .Where(p => supervised.Contains(p.Id))
            .Select(p => new { p.Id, p.DepartmentId, CollegeId = p.Department.CollegeId })
            .ToListAsync(ct);

        var keys = new HashSet<string> { PrincipalKeys.User(user.Id) };
        foreach (var g in groups) keys.Add(PrincipalKeys.Group(g));
        foreach (var p in programs)
        {
            keys.Add(PrincipalKeys.Program(p.ProgramId));
            keys.Add(PrincipalKeys.Department(p.DepartmentId));
            keys.Add(PrincipalKeys.College(p.CollegeId));
        }
        foreach (var d in staffOf) keys.Add(PrincipalKeys.Department(d));
        foreach (var r in roles.Where(r => r.ScopeId is not null))
        {
            var scope = r.ScopeId!.Value;
            switch (r.Role)
            {
                case Role.Dean:
                    keys.Add(PrincipalKeys.Dean(scope));
                    keys.Add(PrincipalKeys.College(scope));
                    break;
                case Role.AcademicSupervisor:
                    keys.Add(PrincipalKeys.Supervisor(scope));
                    keys.Add(PrincipalKeys.Program(scope));
                    break;
                case Role.DepartmentHead:
                    keys.Add(PrincipalKeys.Head(scope));
                    keys.Add(PrincipalKeys.Department(scope));
                    break;
            }
        }
        if (roles.Any(r => r.Role == Role.Vpaf)) keys.Add(PrincipalKeys.Vpaf);
        foreach (var u in supervisedUnits)
        {
            keys.Add(PrincipalKeys.Department(u.DepartmentId));
            if (u.CollegeId is { } c) keys.Add(PrincipalKeys.College(c));
        }

        return _cached = new UserContext
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Roles = roles,
            Programs = programs,
            StaffDepartmentIds = staffOf,
            GroupIds = groups,
            PrincipalKeys = keys,
        };
    }
}

public static class UserContextGuards
{
    /// <summary>Role-level gate for admin-only capabilities (Admin.Settings, Admin.Processing, …).</summary>
    public static void RequireAdmin(this UserContext user)
    {
        if (!user.IsAdmin) throw new ForbiddenException("Only a system administrator can do this.");
    }
}
