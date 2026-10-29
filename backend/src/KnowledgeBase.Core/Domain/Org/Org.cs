using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.Org;

// Thin reference tables synced from the IdP / SIS (frames 11, 16, 24).
// College (Dean) → academic Department (no head) → Program (Academic Supervisor) → Faculty.
// Non-academic departments (e.g. CICT) sit outside the colleges and have a Head/Director + staff.

public sealed class User : Entity, IHasTimestamps
{
    /// <summary>Subject from the IdP (the JWT "sub"). Unique.</summary>
    public required string ExternalId { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<RoleAssignment> Roles { get; set; } = [];
    public List<FacultyProgram> Programs { get; set; } = [];
    public List<DepartmentStaff> StaffOf { get; set; } = [];
    public List<GroupMember> Groups { get; set; } = [];

    /// <summary>Their password, when people sign in to KBMS itself (no identity provider); null = none set.</summary>
    public UserCredential? Credential { get; set; }

    public string Initials => string.Concat(
        DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !Honorifics.Contains(p))
            .Take(2)
            .Select(p => char.ToUpperInvariant(p[0])));

    // "Dr. Ana Lim" → AL, but "J. Cruz" → JC: only titles are skipped, not every abbreviation.
    private static readonly HashSet<string> Honorifics =
        new(["Dr.", "Engr.", "Prof.", "Atty.", "Mr.", "Ms.", "Mrs.", "Fr.", "Sr."], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A password for signing in to KBMS itself, used while there is no identity provider (Auth:Authority
/// empty). Administrators set it; a temporary one is replaced at the next sign-in. The hash never
/// leaves this table.
/// </summary>
public sealed class UserCredential
{
    public const int MaxFailedSignIns = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>PasswordHasher's format: algorithm, iterations, salt and hash.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>An administrator's temporary password: the next sign-in has to choose a new one.</summary>
    public bool MustChangePassword { get; set; }
    public int FailedSignIns { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Tokens issued before this no longer work: the password was changed or reset since.</summary>
    public DateTimeOffset SessionsValidFrom { get; set; }

    public bool IsLocked(DateTimeOffset now) => LockedUntil > now;

    /// <summary>Counts a wrong password; the fifth in a row locks the account for <see cref="LockoutDuration"/>.</summary>
    public void RecordFailedSignIn(DateTimeOffset now)
    {
        FailedSignIns++;
        if (FailedSignIns < MaxFailedSignIns) return;
        FailedSignIns = 0;
        LockedUntil = now + LockoutDuration;
    }

    public void RecordSignIn()
    {
        FailedSignIns = 0;
        LockedUntil = null;
    }

    /// <summary>A new password: clears any lockout and ends every session issued before now.</summary>
    public void SetPassword(string hash, bool mustChange, DateTimeOffset now)
    {
        PasswordHash = hash;
        MustChangePassword = mustChange;
        FailedSignIns = 0;
        LockedUntil = null;
        ChangedAt = now;
        // Whole seconds, like a token's iat: the token issued right after this change stays valid.
        SessionsValidFrom = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
    }
}

public sealed class Group : Entity
{
    public string? ExternalId { get; set; }
    public required string Name { get; set; }
    public List<GroupMember> Members { get; set; } = [];
}

public sealed class GroupMember
{
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}

public sealed class College : Entity
{
    public string? ExternalId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Department> Departments { get; set; } = [];
}

public enum DepartmentKind
{
    Academic,
    NonAcademic,
}

public sealed class Department : Entity
{
    public string? ExternalId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? ShortName { get; set; }
    public DepartmentKind Kind { get; set; }

    /// <summary>Required for academic departments, null for non-academic ones (CHECK in the DB).</summary>
    public Guid? CollegeId { get; set; }
    public College? College { get; set; }
    public bool IsActive { get; set; } = true;

    public List<AcademicProgram> Programs { get; set; } = [];
    public List<DepartmentStaff> Staff { get; set; } = [];
}

public sealed class AcademicProgram : Entity
{
    public string? ExternalId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public List<FacultyProgram> Faculty { get; set; } = [];
}

/// <summary>Faculty membership in a program (a faculty member can teach in more than one).</summary>
public sealed class FacultyProgram
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid ProgramId { get; set; }
    public AcademicProgram Program { get; set; } = null!;
    public bool IsPrimary { get; set; } = true;
}

/// <summary>Staff of a non-academic department (frame 24).</summary>
public sealed class DepartmentStaff
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string? Position { get; set; }
}

public enum Role
{
    SystemAdmin,
    /// <summary>Vice President for Academic Affairs: university-wide, sets every college, department and program required-documents list.</summary>
    Vpaf,
    /// <summary>Scoped to a College.</summary>
    Dean,
    /// <summary>Scoped to a Program — the program's head.</summary>
    AcademicSupervisor,
    /// <summary>Scoped to a non-academic Department (Head or Director).</summary>
    DepartmentHead,
}

public enum RoleScope
{
    Global,
    College,
    Program,
    Department,
}

/// <summary>
/// Dean → College · AcademicSupervisor → Program · DepartmentHead → NonAcademic Department.
/// One active holder per scope (partial unique index).
/// </summary>
public sealed class RoleAssignment : Entity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Role Role { get; set; }
    public RoleScope ScopeType { get; set; }
    public Guid? ScopeId { get; set; }

    /// <summary>Display title for heads, e.g. "Director" or "Head".</summary>
    public string? Title { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset AssignedAt { get; set; }
}
