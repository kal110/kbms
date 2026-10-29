using KnowledgeBase.Core.Domain.Org;

namespace KnowledgeBase.Core.Application.Org;

// Org structure (frames 16, 18, 23, 24): College (Dean) → academic Department (no head) →
// Program (Academic Supervisor) → Faculty; non-academic departments with a Head/Director and staff.

public sealed record UnitRefDto(Guid Id, string Code, string Name);

public sealed record PersonDto(Guid Id, string ExternalId, string DisplayName, string Initials, string? Email, string? Title)
{
    public static PersonDto From(User u) => new(u.Id, u.ExternalId, u.DisplayName, u.Initials, u.Email, u.Title);
}

public sealed record RoleDto(Role Role, RoleScope ScopeType, UnitRefDto? Scope, string? Title);

public sealed record ProgramMembershipDto(UnitRefDto Program, UnitRefDto Department, UnitRefDto College, bool IsPrimary);

public sealed record StaffMembershipDto(UnitRefDto Department, string? Position);

/// <summary>
/// The signed-in user. <see cref="Persona"/> matches the SPA's persona ids (faculty, supervisor,
/// dean, admin, vpaf, director, staff) and <see cref="Capabilities"/> drives its navigation.
/// </summary>
public sealed record MeDto(
    Guid Id,
    string ExternalId,
    string DisplayName,
    string Initials,
    string? Email,
    string? Title,
    string Persona,
    string RoleLabel,
    IReadOnlyList<RoleDto> Roles,
    IReadOnlyList<ProgramMembershipDto> Programs,
    IReadOnlyList<StaffMembershipDto> Departments,
    IReadOnlyList<string> Capabilities,
    bool HasPassword);

public sealed record OrgProgramDto(Guid Id, string Code, string Name, Guid DepartmentId, PersonDto? Supervisor, int FacultyCount);

public sealed record OrgDepartmentDto(
    Guid Id,
    string Code,
    string Name,
    string? ShortName,
    DepartmentKind Kind,
    Guid? CollegeId,
    IReadOnlyList<OrgProgramDto> Programs,
    PersonDto? Head,
    string? HeadTitle,
    int StaffCount);

public sealed record OrgCollegeDto(Guid Id, string Code, string Name, PersonDto? Dean, IReadOnlyList<OrgDepartmentDto> Departments);

public sealed record OrgTreeDto(IReadOnlyList<OrgCollegeDto> Colleges, IReadOnlyList<OrgDepartmentDto> NonAcademicDepartments);

public sealed record FacultyMemberDto(PersonDto Person, bool IsPrimary);

public sealed record ProgramDetailDto(
    Guid Id,
    string Code,
    string Name,
    UnitRefDto Department,
    UnitRefDto College,
    PersonDto? Supervisor,
    IReadOnlyList<FacultyMemberDto> Faculty);

public sealed record StaffMemberDto(PersonDto Person, string? Position);

public sealed record DepartmentDetailDto(
    Guid Id,
    string Code,
    string Name,
    string? ShortName,
    DepartmentKind Kind,
    UnitRefDto? College,
    PersonDto? Head,
    string? HeadTitle,
    IReadOnlyList<StaffMemberDto> Staff,
    IReadOnlyList<OrgProgramDto> Programs);

public sealed record AssignHeadRequest(Guid UserId, string? Title);

public sealed record AssignHolderRequest(Guid UserId);

public sealed record AddStaffRequest(Guid UserId, string? Position);

public sealed record UpdateStaffRequest(string? Position);

/// <summary>Capability names in <see cref="MeDto.Capabilities"/>.</summary>
public static class Capabilities
{
    public const string BrowseLibrary = "canBrowseLibrary";
    public const string Submit = "canSubmit";
    public const string Review = "canReview";
    public const string SetProgramRequirements = "canSetProgramRequirements";
    public const string SetScopedRequirements = "canSetScopedRequirements";
    public const string ViewProgramRequirements = "canViewProgramRequirements";
    public const string ViewSubmissionDashboard = "canViewSubmissionDashboard";
    public const string ManageSettings = "canManageSettings";
    public const string ManageOrg = "canManageOrg";
    public const string ViewProcessing = "canViewProcessing";
    public const string ViewAudit = "canViewAudit";
    public const string IsAdmin = "isAdmin";
    public const string IsVpaf = "isVpaf";
    public const string IsDean = "isDean";
    public const string IsSupervisor = "isSupervisor";
    public const string IsDepartmentHead = "isDepartmentHead";
    public const string IsFaculty = "isFaculty";
    public const string IsStaff = "isStaff";
}

// ---- Settings → Users & roles (admin)

/// <summary>A person with every active role and membership, for Settings → Users &amp; roles.</summary>
public sealed record UserAdminDto(
    Guid Id,
    string ExternalId,
    string DisplayName,
    string Initials,
    string? Email,
    string? Title,
    bool Active,
    IReadOnlyList<RoleDto> Roles,
    IReadOnlyList<ProgramMembershipDto> Programs,
    IReadOnlyList<StaffMembershipDto> Departments,
    DateTimeOffset CreatedAt,
    UserPasswordDto? Password);

/// <summary>
/// Their password for signing in to KBMS (no identity provider); null when none is set. MustChange:
/// a temporary one, replaced at the next sign-in. LockedUntil: locked after wrong passwords.
/// </summary>
public sealed record UserPasswordDto(bool MustChange, DateTimeOffset? LockedUntil, DateTimeOffset ChangedAt);

/// <summary>MustChange makes it a temporary password the person replaces at their next sign-in.</summary>
public sealed record SetPasswordRequest(string Password, bool MustChange);

/// <summary>What a user holds, for the list filter. None = no role and no program or department.</summary>
public enum UserRoleFilter
{
    Admin,
    Vpaf,
    Dean,
    Supervisor,
    Head,
    Faculty,
    Staff,
    None,
}

/// <summary>ExternalId is the sign-in id: the identity provider's subject (the token's sub).</summary>
public sealed record CreateUserRequest(string ExternalId, string DisplayName, string? Email, string? Title);

/// <summary>Null leaves a field unchanged; "" clears email or title. Active false deactivates (sign-in stops).</summary>
public sealed record UpdateUserRequest(string? DisplayName, string? Email, string? Title, bool? Active);

/// <summary>IsPrimary makes this the person's main program (their department-level folder and checklist).</summary>
public sealed record AddFacultyRequest(Guid UserId, bool IsPrimary);

// ---- Settings → Colleges & departments (admin)

/// <summary>Department = academic department (under a college); Office = non-academic department (CICT, Registrar…).</summary>
public enum OrgUnitKind
{
    College,
    Department,
    Office,
    Program,
}

/// <summary>
/// What a unit holds. People = a program's faculty or an office's staff; Units = a college's
/// departments or a department's programs; Documents = documents filed in its folders.
/// Blockers say what keeps it from being deleted (e.g. "7 faculty members"); empty = it can be.
/// </summary>
public sealed record OrgUnitUsageDto(
    int Units,
    int People,
    int Courses,
    int Submissions,
    int Documents,
    int RequirementLists,
    IReadOnlyList<string> Blockers);

/// <summary>A unit of the org structure. Holder is the Dean, Academic Supervisor or Head; ParentId the college or department.</summary>
public sealed record OrgUnitAdminDto(
    Guid Id,
    OrgUnitKind Kind,
    string Code,
    string Name,
    string? ShortName,
    Guid? ParentId,
    PersonDto? Holder,
    string? HolderTitle,
    OrgUnitUsageDto Usage,
    IReadOnlyList<OrgUnitAdminDto> Children);

public sealed record OrgStructureDto(IReadOnlyList<OrgUnitAdminDto> Colleges, IReadOnlyList<OrgUnitAdminDto> Offices);

public sealed record CreateCollegeRequest(string Code, string Name);

/// <summary>Null leaves a field unchanged.</summary>
public sealed record UpdateCollegeRequest(string? Code, string? Name);

/// <summary>Academic departments need a college; non-academic ones have none. The kind can't change later.</summary>
public sealed record CreateDepartmentRequest(string Code, string Name, string? ShortName, DepartmentKind Kind, Guid? CollegeId);

/// <summary>Null leaves a field unchanged; "" clears the short name. CollegeId moves an academic department to another college.</summary>
public sealed record UpdateDepartmentRequest(string? Code, string? Name, string? ShortName, Guid? CollegeId);

public sealed record CreateProgramRequest(string Code, string Name, Guid DepartmentId);

/// <summary>Null leaves a field unchanged. DepartmentId moves the program, with its faculty, courses and folders.</summary>
public sealed record UpdateProgramRequest(string? Code, string? Name, Guid? DepartmentId);
