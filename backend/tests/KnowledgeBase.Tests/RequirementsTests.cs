using KnowledgeBase.Core.Application.Requirements;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;

namespace KnowledgeBase.Tests;

public class RequirementsTests
{
    private static readonly Guid College = Guid.NewGuid();
    private static readonly Guid Department = Guid.NewGuid();
    private static readonly Guid Program = Guid.NewGuid();

    private static SubmissionRequirement Slot(string key, int order, bool required = true, int min = 1, int max = 1) => new()
    {
        Key = key,
        Label = key,
        DocumentTypeId = Guid.NewGuid(),
        IsRequired = required,
        MinFiles = min,
        MaxFiles = max,
        SortOrder = order,
    };

    [Fact]
    public void Merge_adds_broadest_first_and_lets_the_program_override_a_key()
    {
        var program = new ChecklistLayer(RequirementScope.Program, Program, Guid.NewGuid(),
            [Slot("syllabus", 0), Slot("reading_list", 1, required: false, min: 0, max: 3)]);
        var college = new ChecklistLayer(RequirementScope.College, College, Guid.NewGuid(), [Slot("faculty_load", 0)]);
        var department = new ChecklistLayer(RequirementScope.Department, Department, Guid.NewGuid(),
            [Slot("reading_list", 0, required: false, min: 0, max: 2)]);

        var merged = Checklists.Merge([program, college, department]);

        Assert.Equal(["faculty_load", "reading_list", "syllabus"], merged.Select(r => r.Key));
        var readingList = merged.Single(r => r.Key == "reading_list");
        Assert.Equal(RequirementScope.Program, readingList.Scope);
        Assert.Equal(3, readingList.MaxFiles);
    }

    [Fact]
    public void PickInForce_keeps_the_previous_version_until_the_new_one_takes_effect()
    {
        var thisTerm = new DateOnly(2026, 8, 10);
        var nextTerm = new DateOnly(2027, 1, 11);
        var v3 = new SetCandidate(Guid.NewGuid(), RequirementScope.Program, Program, 3, thisTerm);
        var v4 = new SetCandidate(Guid.NewGuid(), RequirementScope.Program, Program, 4, nextTerm);
        var college = new SetCandidate(Guid.NewGuid(), RequirementScope.College, College, 1, null);

        Assert.Equal([college.SetId, v3.SetId], Checklists.PickInForce([v3, v4, college], thisTerm).Select(c => c.SetId));
        Assert.Equal([college.SetId, v4.SetId], Checklists.PickInForce([v3, v4, college], nextTerm).Select(c => c.SetId));
        Assert.Equal([college.SetId], Checklists.PickInForce([v3, v4, college], new DateOnly(2026, 1, 12)).Select(c => c.SetId));
    }

    [Theory]
    [InlineData("Faculty load assignment", "faculty_load_assignment")]
    [InlineData("  Year-end equipment inventory ", "year_end_equipment_inventory")]
    [InlineData("2nd reader's report", "k_2nd_reader_s_report")]
    public void KeyFromLabel_matches_the_spa(string label, string key) => Assert.Equal(key, Checklists.KeyFromLabel(label));

    [Fact]
    public void Optional_slots_allow_no_files()
    {
        Assert.Equal((0, 2), Checklists.NormalizeFiles(RequirementRule.Optional, new FileLimit(1, 2)));
        Assert.Equal((1, 2), Checklists.NormalizeFiles(RequirementRule.Required, new FileLimit(1, 2)));
    }
}

/// <summary>Who sets and who reads each required-documents list.</summary>
public class RequirementPolicyTests
{
    private static readonly Guid College = Guid.NewGuid();
    private static readonly Guid Department = Guid.NewGuid();
    private static readonly Guid Program = Guid.NewGuid();
    private static readonly Guid Office = Guid.NewGuid();

    private static readonly ScopeInfo CollegeScope = new(RequirementScope.College, College, "CCS", "CCS", null, College, null);
    private static readonly ScopeInfo DepartmentScope =
        new(RequirementScope.Department, Department, "DCS", "DCS", DepartmentKind.Academic, College, Department);
    private static readonly ScopeInfo ProgramScope =
        new(RequirementScope.Program, Program, "BSCS", "BSCS", DepartmentKind.Academic, College, Department);
    private static readonly ScopeInfo OfficeScope = new(RequirementScope.Department, Office, "CICT", "CICT", DepartmentKind.NonAcademic, null, Office);
    private static readonly ScopeInfo[] All = [CollegeScope, DepartmentScope, ProgramScope, OfficeScope];

    private static UserContext Person(RoleGrant? role = null, params string[] keys) => new()
    {
        UserId = Guid.NewGuid(),
        DisplayName = "Someone",
        Roles = role is null ? [] : [role],
        Programs = [],
        StaffDepartmentIds = [],
        GroupIds = [],
        PrincipalKeys = new HashSet<string>(keys),
    };

    [Fact]
    public void Admin_and_vpaf_set_every_list()
    {
        var admin = Person(new RoleGrant(Role.SystemAdmin, RoleScope.Global, null, null));
        var vpaf = Person(new RoleGrant(Role.Vpaf, RoleScope.Global, null, "VP for Academic Affairs"));

        Assert.All(All, s => Assert.True(RequirementPolicy.CanManage(admin, s)));
        Assert.All(All, s => Assert.True(RequirementPolicy.CanManage(vpaf, s)));
    }

    [Fact]
    public void Academic_supervisor_reads_their_program_list_but_cannot_change_it()
    {
        var supervisor = Person(new RoleGrant(Role.AcademicSupervisor, RoleScope.Program, Program, "Academic Supervisor"),
            PrincipalKeys.Program(Program), PrincipalKeys.Department(Department), PrincipalKeys.College(College));

        Assert.All(All, s => Assert.False(RequirementPolicy.CanManage(supervisor, s)));
        Assert.True(RequirementPolicy.CanView(supervisor, ProgramScope));
        Assert.True(RequirementPolicy.CanView(supervisor, CollegeScope));
        Assert.False(RequirementPolicy.CanView(supervisor, OfficeScope));
    }

    [Fact]
    public void Non_academic_head_reads_their_department_list_but_cannot_change_it()
    {
        var head = Person(new RoleGrant(Role.DepartmentHead, RoleScope.Department, Office, "Director"), PrincipalKeys.Department(Office));

        Assert.All(All, s => Assert.False(RequirementPolicy.CanManage(head, s)));
        Assert.True(RequirementPolicy.CanView(head, OfficeScope));
        Assert.False(RequirementPolicy.CanView(head, ProgramScope));
    }
}
