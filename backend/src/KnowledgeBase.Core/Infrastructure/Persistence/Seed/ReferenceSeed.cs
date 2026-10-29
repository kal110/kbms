using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static KnowledgeBase.Core.Infrastructure.Persistence.Seed.SeedKeys;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Seed;

/// <summary>"Database:Admin" section: the System Administrator the seed creates.</summary>
public sealed class AdminSeedOptions
{
    public const string Section = "Database:Admin";

    /// <summary>The IdP subject (the JWT "sub"); with dev tokens, the sign-in id.</summary>
    public string ExternalId { get; set; } = "admin";
    public string DisplayName { get; set; } = "System Administrator";
    public string? Email { get; set; } = "admin@usa.edu.ph";
}

/// <summary>
/// Order 0: the System Administrator, plus the academic calendar, retention policies, document types
/// and submission types as editable defaults. People, colleges, departments and programs are not
/// seeded; the admin adds them under Settings. Each part is seeded only while its table is empty.
/// </summary>
public sealed class ReferenceSeed(IOptions<AdminSeedOptions> admin, TimeProvider clock) : ISeedContributor
{
    public int Order => 0;
    public string Name => "System Administrator and default settings (calendar, retention, document and submission types)";

    public async Task SeedAsync(KbDbContext db, CancellationToken ct)
    {
        await SeedAdminAsync(db, ct);
        if (!await db.AcademicYears.AnyAsync(ct)) SeedCalendar(db);
        if (!await db.DocumentTypes.AnyAsync(ct)) await SeedDocumentTypesAsync(db, ct);
        if (!await db.SubmissionTypes.AnyAsync(ct)) SeedSubmissionTypes(db);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Makes sure someone can sign in and administer: skipped while any System Administrator is active.</summary>
    private async Task SeedAdminAsync(KbDbContext db, CancellationToken ct)
    {
        if (await db.RoleAssignments.AnyAsync(r => r.Role == Role.SystemAdmin && r.IsActive && r.User.IsActive, ct)) return;

        var o = admin.Value;
        var externalId = string.IsNullOrWhiteSpace(o.ExternalId) ? "admin" : o.ExternalId.Trim();
        var user = await db.Users.SingleOrDefaultAsync(u => u.ExternalId == externalId, ct);
        if (user is null)
        {
            user = new User
            {
                ExternalId = externalId,
                DisplayName = string.IsNullOrWhiteSpace(o.DisplayName) ? "System Administrator" : o.DisplayName.Trim(),
                Email = string.IsNullOrWhiteSpace(o.Email) ? null : o.Email.Trim(),
            };
            db.Users.Add(user);
        }
        user.IsActive = true;
        db.RoleAssignments.Add(new RoleAssignment
        {
            User = user,
            Role = Role.SystemAdmin,
            ScopeType = RoleScope.Global,
            AssignedAt = clock.GetUtcNow(),
        });
    }

    /// <summary>AY 2025–26 and 2026–27 with their semesters, summers and submission windows.</summary>
    private static void SeedCalendar(KbDbContext db)
    {
        var ay2025 = Year(Years.Ay2025, "AY 2025–26", "2025-08-11", "2026-07-31");
        var ay2026 = Year(Years.Ay2026, "AY 2026–27", "2026-08-10", "2027-07-23");
        db.AcademicYears.AddRange(ay2025, ay2026);
        db.AcademicTerms.AddRange(
            Term(ay2025, Terms.Ay2025First, "1st Semester", "1st Sem", 1, "2025-08-11", "2025-12-19", TermStatus.Closed, "2025-08-11", "2025-11-28", true),
            Term(ay2025, Terms.Ay2025Second, "2nd Semester", "2nd Sem", 2, "2026-01-12", "2026-05-22", TermStatus.Closed, "2026-01-12", "2026-04-30", true),
            Term(ay2025, Terms.Ay2025Summer, "Summer", "Summer", 3, "2026-06-08", "2026-07-31", TermStatus.Closed, "2026-06-08", "2026-07-10", false),
            Term(ay2026, Terms.Ay2026First, "1st Semester", "1st Sem", 1, "2026-08-10", "2026-12-18", TermStatus.Active, "2026-08-10", "2026-11-30", true),
            Term(ay2026, Terms.Ay2026Second, "2nd Semester", "2nd Sem", 2, "2027-01-11", "2027-05-21", TermStatus.Planned, "2027-01-11", "2027-04-30", true),
            Term(ay2026, Terms.Ay2026Summer, "Summer", "Summer", 3, "2027-06-07", "2027-07-23", TermStatus.Planned, "2027-06-07", "2027-07-09", false));
    }

    /// <summary>Retention policies (kept if some already exist) and the document types that use them.</summary>
    private static async Task SeedDocumentTypesAsync(KbDbContext db, CancellationToken ct)
    {
        var policies = await db.RetentionPolicies.ToListAsync(ct);
        RetentionPolicy Retention(string name, int? years, RetentionTrigger trigger)
        {
            var policy = policies.FirstOrDefault(p => p.Name == name);
            if (policy is not null) return policy;
            policy = new RetentionPolicy { Name = name, RetainYears = years, Trigger = trigger, ActionAfter = RetentionAction.Review };
            db.RetentionPolicies.Add(policy);
            return policy;
        }

        var term5 = Retention("5 years after the term", 5, RetentionTrigger.AfterTerm);
        var term3 = Retention("3 years after the term", 3, RetentionTrigger.AfterTerm);
        var year5 = Retention("5 years after the academic year", 5, RetentionTrigger.AfterAcademicYear);
        var upload10 = Retention("10 years", 10, RetentionTrigger.AfterUpload);
        var separation5 = Retention("5 years after separation", 5, RetentionTrigger.AfterSeparation);
        var permanent = Retention("Permanent", null, RetentionTrigger.Permanent);

        var syllabus = Type(DocumentTypes.Syllabus, "Course syllabus", "Outcome-based syllabus for a course offering.", "SYL-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.Academic, term5);
        db.DocumentTypes.AddRange(
            syllabus,
            Type(DocumentTypes.OutcomesMatrix, "Course outcomes matrix", "Maps course outcomes to program outcomes.", "COM-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.Academic, term5),
            Type(DocumentTypes.Rubric, "Assessment rubric", "Scoring rubric for a graded requirement.", "RUB-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.Academic, term5),
            Type(DocumentTypes.ReadingList, "Reading list", "Required and suggested readings for a course.", null, AcademicTag.Term, TypeScope.Academic, term3),
            Type(DocumentTypes.LabManual, "Lab manual", "Laboratory activities and procedures for a course.", "LAB-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.Academic, term5),
            Type(DocumentTypes.ResearchProposal, "Research proposal", "Proposal for a research project.", "RP-{yyyy}-{seq:4}", AcademicTag.Year, TypeScope.Academic, upload10),
            Type(DocumentTypes.ThesisGuidelines, "Thesis guidelines", "Program guidelines for theses and capstones.", "THG-{yyyy}-{seq:3}", AcademicTag.Year, TypeScope.Academic, permanent),
            Type(DocumentTypes.Memorandum, "Memorandum", "Office memorandum.", "MEMO-{yyyy}-{seq:2}", AcademicTag.Year, TypeScope.All, upload10),
            Type(DocumentTypes.Accreditation, "Accreditation evidence", "Evidence filed for an accreditation visit.", "ACC-{yyyy}-{seq:3}", AcademicTag.Year, TypeScope.Academic, permanent),
            Type(DocumentTypes.Policy, "Policy", "Institutional or college policy.", "AP-{yyyy}-{seq:3}", AcademicTag.None, TypeScope.All, permanent),
            Type(DocumentTypes.BoardResolution, "Board resolution", "Resolution of the Board of Trustees.", "BR-{yyyy}-{seq:3}", AcademicTag.None, TypeScope.All, permanent),
            Type(DocumentTypes.FacultyLoad, "Faculty load", "Teaching load assignment for the term.", "FLA-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.Academic, term5),
            Type(DocumentTypes.AccomplishmentReport, "Accomplishment report", "Summary of accomplishments for the period.", "ACR-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.All, term5),
            Type(DocumentTypes.WorkPlan, "Work plan", "Planned activities and targets for the year.", "WPL-{yyyy}-{seq:3}", AcademicTag.Year, TypeScope.All, year5),
            Type(DocumentTypes.Inventory, "Inventory report", "Equipment and asset inventory.", "INV-{yyyy}-{seq:3}", AcademicTag.Year, TypeScope.NonAcademic, upload10),
            Type(DocumentTypes.Incident, "Incident report", "Report of a service or security incident.", "INC-{yyyy}-{seq:4}", AcademicTag.Term, TypeScope.NonAcademic, term3),
            Type(DocumentTypes.Form, "Form", "Blank form or template.", null, AcademicTag.None, TypeScope.All, permanent),
            Type(DocumentTypes.CurriculumVitae, "Curriculum vitae", "Faculty curriculum vitae.", null, AcademicTag.None, TypeScope.Academic, separation5));
    }

    /// <summary>Submission types and their approval stages (unit head, then the dean where marked).</summary>
    private static void SeedSubmissionTypes(KbDbContext db) => db.SubmissionTypes.AddRange(
        Submission(SubmissionTypes.Syllabus, "Course syllabus", "Syllabus and course outcomes for a course offering.", programScoped: true, perCourse: true),
        Submission(SubmissionTypes.Proposal, "Research proposal", "Research proposal for program review.", programScoped: true),
        Submission(SubmissionTypes.Thesis, "Thesis guidelines", "Program guidelines for theses and capstones.", programScoped: true),
        Submission(SubmissionTypes.LabManual, "Lab manual", "Laboratory manual for a course.", programScoped: true, perCourse: true),
        Submission(SubmissionTypes.Accreditation, "Accreditation evidence", "Evidence for an accreditation visit; the Dean approves after the AS.", programScoped: true, deanStage: true),
        Submission(SubmissionTypes.Policy, "Policy revision", "New or revised policy; the Dean approves after the unit head.", programScoped: false, deanStage: true),
        Submission(SubmissionTypes.Incident, "Incident report", "Service or security incident report of a non-academic office.", programScoped: false),
        Submission(SubmissionTypes.Inventory, "Inventory report", "Equipment and asset inventory of a non-academic office.", programScoped: false),
        Submission(SubmissionTypes.Semestral, "Semestral report", "Office report for the semester.", programScoped: false),
        Submission(SubmissionTypes.WorkPlan, "Annual work plan", "Office work plan for the academic year.", programScoped: false),
        Submission(SubmissionTypes.Project, "Project docs", "Documentation of an office project.", programScoped: false),
        Submission(SubmissionTypes.Portfolio, "Course portfolio", "Course portfolio with assessments and reports.", programScoped: true));

    private static AcademicYear Year(string code, string name, string starts, string ends) => new()
    {
        Code = code,
        Name = name,
        StartsOn = DateOnly.Parse(starts),
        EndsOn = DateOnly.Parse(ends),
        FromSis = true,
    };

    private static AcademicTerm Term(AcademicYear year, string code, string name, string shortName, int order, string starts, string ends,
        TermStatus status, string windowOpens, string windowCloses, bool allowResubmit) => new()
    {
        AcademicYear = year,
        Code = code,
        Name = name,
        ShortName = shortName,
        SortOrder = order,
        StartsOn = DateOnly.Parse(starts),
        EndsOn = DateOnly.Parse(ends),
        Status = status,
        SubmissionOpensOn = DateOnly.Parse(windowOpens),
        SubmissionClosesOn = DateOnly.Parse(windowCloses),
        AllowResubmitAfterClose = allowResubmit,
        FromSis = true,
    };

    private static DocumentType Type(string code, string name, string description, string? pattern, AcademicTag tag, TypeScope scope,
        RetentionPolicy retention) => new()
    {
        Code = code,
        Name = name,
        Description = description,
        NumberPattern = pattern,
        AcademicTag = tag,
        Scope = scope,
        RetentionPolicy = retention,
    };

    private static SubmissionType Submission(string code, string name, string description, bool programScoped,
        bool deanStage = false, bool perCourse = false)
    {
        var type = new SubmissionType
        {
            Code = code, Name = name, Description = description, ProgramScoped = programScoped, PerCourse = perCourse,
        };
        type.Stages.Add(new ApprovalStage { StageOrder = 1, ReviewerRule = ReviewerRule.UnitHead });
        if (deanStage) type.Stages.Add(new ApprovalStage { StageOrder = 2, ReviewerRule = ReviewerRule.CollegeDean });
        return type;
    }
}
