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

    public string Initials => string.Concat(
        DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !Honorifics.Contains(p))
            .Take(2)
            .Select(p => char.ToUpperInvariant(p[0])));

    // "Dr. Ana Lim" → AL, but "J. Cruz" → JC: only titles are skipped, not every abbreviation.
    private static readonly HashSet<string> Honorifics =
        new(["Dr.", "Engr.", "Prof.", "Atty.", "Mr.", "Ms.", "Mrs.", "Fr.", "Sr."], StringComparer.OrdinalIgnoreCase);
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
