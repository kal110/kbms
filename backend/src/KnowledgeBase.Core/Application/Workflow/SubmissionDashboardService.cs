using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Workflow;

/// <summary>
/// One submissions dashboard at three levels: the university (VPAF, admin) by college, a college (its Dean) by
/// program, and a program (its Academic Supervisor) by faculty member. Each level drills into the next. Non-academic
/// departments have two levels of their own: all of them (VPAF, admin) by department, and one (its Head) by staff member.
/// </summary>
public sealed class SubmissionDashboardService(KbDbContext db, ICurrentUser currentUser, WorkflowDirectory directory, TimeProvider clock)
{
    /// <summary>A program, or a non-academic department (Office), with the people expected to submit for it.</summary>
    private sealed record Unit(
        Guid Id, string Code, string Name, Guid? CollegeId, Guid DepartmentId, string DepartmentName, bool Office, IReadOnlyCollection<Guid> People);

    private sealed record Item(
        Guid Id, string Number, string Title, Guid UnitId, Guid TypeId, Guid? CourseId, SubmissionStatus Status, Guid SubmitterId,
        DateTimeOffset? SubmittedAt, DateTimeOffset? FinalizedAt);

    private sealed record LoadLine(Guid UserId, Guid CourseId, Guid ProgramId);

    private static readonly DashboardScopeRef UniversityRoot = new(DashboardLevel.University, null, "All", "All colleges");
    private static readonly DashboardScopeRef NonAcademicRoot = new(DashboardLevel.NonAcademic, null, "Non-academic", "Non-academic departments");

    public async Task<SubmissionDashboard> GetAsync(
        Guid? collegeId, Guid? programId, Guid? departmentId, bool nonAcademic, Guid? termId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var wide = me.IsAdmin || me.IsVpaf;
        var deanOf = me.ScopesOf(Role.Dean).ToHashSet();
        var supervises = me.ScopesOf(Role.AcademicSupervisor).ToHashSet();
        var heads = me.ScopesOf(Role.DepartmentHead).ToHashSet();
        if (!wide && deanOf.Count == 0 && supervises.Count == 0 && heads.Count == 0)
            throw new ForbiddenException("The submissions dashboard is for Academic Supervisors, Deans, department Heads and the VPAF.");

        var term = await db.AcademicTerms.AsNoTracking()
            .Where(t => termId == null ? t.Status == TermStatus.Active : t.Id == termId)
            .Select(t => new TermRef(t.Id, t.Code, t.Name, t.ShortName, t.AcademicYear.Code, t.AcademicYear.Name, t.Status))
            .FirstOrDefaultAsync(ct)
            ?? throw new ValidationAppException("termId", termId is null ? "There is no active term; pick one." : "That academic term doesn't exist.");

        // The term's teaching load decides who teaches in each program; a term without a load falls back to membership.
        var load = await db.TeachingAssignments.AsNoTracking()
            .Where(a => a.AcademicTermId == term.Id)
            .Select(a => new LoadLine(a.UserId, a.CourseId, a.Course.ProgramId))
            .Distinct()
            .ToListAsync(ct);
        var colleges = await db.Colleges.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
            .Select(c => new DashboardScopeRef(DashboardLevel.College, c.Id, c.Code, c.Name))
            .ToListAsync(ct);
        var programs = (await db.Programs.AsNoTracking()
            .Where(p => p.IsActive && p.Department.IsActive && p.Department.CollegeId != null)
            .OrderBy(p => p.Department.Name).ThenBy(p => p.Code)
            .Select(p => new Unit(p.Id, p.Code, p.Name, p.Department.CollegeId, p.DepartmentId, p.Department.Name, false,
                p.Faculty.Select(f => f.UserId).ToList()))
            .ToListAsync(ct))
            .Select(p => load.Count == 0 ? p : p with { People = load.Where(l => l.ProgramId == p.Id).Select(l => l.UserId).ToHashSet() })
            .ToList();
        // Non-academic departments expect their staff; they have no teaching load.
        var offices = await db.Departments.AsNoTracking()
            .Where(d => d.IsActive && d.Kind == DepartmentKind.NonAcademic)
            .OrderBy(d => d.Name)
            .Select(d => new Unit(d.Id, d.Code, d.Name, null, d.Id, d.Name, true, d.Staff.Select(s => s.UserId).ToList()))
            .ToListAsync(ct);

        bool CanSeeCollege(Guid? id) => wide || (id is { } c && deanOf.Contains(c));
        bool CanSeeOffice(Guid id) => wide || heads.Contains(id);
        static DashboardScopeRef ProgramRef(Unit p) => new(DashboardLevel.Program, p.Id, p.Code, p.Name);
        static DashboardScopeRef OfficeRef(Unit d) => new(DashboardLevel.Department, d.Id, d.Code, d.Name);
        const string NotYours = "You can see the dashboard of your own program, college or department only.";

        // The asked-for scope, else the caller's widest one.
        Unit? program = null;
        Unit? office = null;
        DashboardScopeRef? college = null;
        if (programId is { } pid)
        {
            program = programs.FirstOrDefault(p => p.Id == pid) ?? throw new NotFoundException("Program", pid);
            if (!CanSeeCollege(program.CollegeId) && !supervises.Contains(pid)) throw new ForbiddenException(NotYours);
        }
        else if (departmentId is { } did)
        {
            office = offices.FirstOrDefault(d => d.Id == did) ?? throw new NotFoundException("Department", did);
            if (!CanSeeOffice(did)) throw new ForbiddenException(NotYours);
        }
        else if (collegeId is { } cid)
        {
            college = colleges.FirstOrDefault(c => c.Id == cid) ?? throw new NotFoundException("College", cid);
            if (!CanSeeCollege(cid)) throw new ForbiddenException(NotYours);
        }
        else if (nonAcademic)
        {
            if (!wide) throw new ForbiddenException("Only an administrator or the VPAF can see every non-academic department.");
        }
        else if (!wide && deanOf.Count > 0)
        {
            college = colleges.FirstOrDefault(c => deanOf.Contains(c.Id!.Value)) ?? throw new NotFoundException("College", deanOf.First());
        }
        else if (!wide && supervises.Count > 0)
        {
            program = programs.FirstOrDefault(p => supervises.Contains(p.Id)) ?? throw new NotFoundException("Program", supervises.First());
        }
        else if (!wide)
        {
            office = offices.FirstOrDefault(d => heads.Contains(d.Id)) ?? throw new NotFoundException("Department", heads.First());
        }
        if (program is not null) college = colleges.FirstOrDefault(c => c.Id == program.CollegeId);

        var level = program is not null ? DashboardLevel.Program
            : office is not null ? DashboardLevel.Department
            : college is not null ? DashboardLevel.College
            : nonAcademic ? DashboardLevel.NonAcademic
            : DashboardLevel.University;
        var scope = level switch
        {
            DashboardLevel.Program => ProgramRef(program!),
            DashboardLevel.Department => OfficeRef(office!),
            DashboardLevel.College => college!,
            DashboardLevel.NonAcademic => NonAcademicRoot,
            _ => UniversityRoot,
        };
        List<Unit> inScope = level switch
        {
            DashboardLevel.Program => [program!],
            DashboardLevel.Department => [office!],
            DashboardLevel.College => programs.Where(p => p.CollegeId == college!.Id).ToList(),
            DashboardLevel.NonAcademic => offices,
            _ => programs.Where(p => colleges.Any(c => c.Id == p.CollegeId)).ToList(),
        };

        var holders = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.IsActive && r.User.IsActive && r.ScopeId != null
                && (r.Role == Role.Dean || r.Role == Role.AcademicSupervisor || r.Role == Role.DepartmentHead))
            .Select(r => new { r.Role, ScopeId = r.ScopeId!.Value, Person = new PersonRef(r.User.Id, r.User.DisplayName, r.User.Title) })
            .ToListAsync(ct);
        PersonRef? Holder(Role role, Guid? id) => holders.FirstOrDefault(h => h.Role == role && h.ScopeId == id)?.Person;

        var unitIds = inScope.Select(u => u.Id).ToList();
        var submissions = await db.Submissions.AsNoTracking()
            .Where(s => ((s.ProgramId != null && unitIds.Contains(s.ProgramId.Value))
                    || (s.ProgramId == null && s.DepartmentId != null && unitIds.Contains(s.DepartmentId.Value)))
                && s.AcademicTermId == term.Id && s.Status != SubmissionStatus.Draft && s.Status != SubmissionStatus.Withdrawn)
            .Select(s => new Item(s.Id, s.SubmissionNumber, s.Title, s.ProgramId ?? s.DepartmentId!.Value, s.SubmissionTypeId, s.CourseId,
                s.Status, s.SubmittedByUserId, s.SubmittedAt, s.FinalizedAt))
            .ToListAsync(ct);
        var submissionIds = submissions.Select(s => s.Id).ToList();
        var typeIds = submissions.Select(s => s.TypeId).Distinct().ToList();
        var types = await db.SubmissionTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new TypeRef(t.Id, t.Code, t.Name), ct);

        var assignments = await db.SubmissionAssignments.AsNoTracking()
            .Where(a => submissionIds.Contains(a.SubmissionId))
            .ToListAsync(ct);
        var actions = await db.ReviewActions.AsNoTracking()
            .Where(a => submissionIds.Contains(a.SubmissionId))
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);
        var everyone = inScope.SelectMany(u => u.People).ToHashSet();
        var people = await directory.GetPeopleAsync(assignments.Select(a => a.ReviewerUserId)
            .Concat(actions.Select(a => a.ActorUserId))
            .Concat(submissions.Select(s => s.SubmitterId))
            .Concat(everyone), ct);
        PersonRef Person(Guid id) => people.GetValueOrDefault(id) ?? new PersonRef(id, "Unknown user", null);

        var now = clock.GetUtcNow();
        var openAssignment = assignments.Where(a => a.CompletedAt is null)
            .GroupBy(a => a.SubmissionId)
            .ToDictionary(g => g.Key, g => g.MaxBy(a => a.AssignedAt)!);
        bool IsOverdue(Item s) =>
            WorkflowRules.IsPending(s.Status) && openAssignment.TryGetValue(s.Id, out var a) && now - a.AssignedAt > WorkflowRules.OverdueAfter;

        // Days from assignment to the decision taken on it.
        var decisionDays = actions
            .Where(a => SubmissionQueries.IsDecision(a.ActionType))
            .Select(a => new
            {
                a.SubmissionId,
                Assigned = assignments
                    .Where(x => x.SubmissionId == a.SubmissionId && x.StageOrder == a.StageOrder && x.AssignedAt <= a.OccurredAt)
                    .MaxBy(x => x.AssignedAt),
                a.OccurredAt,
            })
            .Where(x => x.Assigned is not null)
            .Select(x => (x.SubmissionId, Days: (x.OccurredAt - x.Assigned!.AssignedAt).TotalDays))
            .ToList();

        // Per-course coverage: each course taught × each per-course type with a published list on the program's path.
        // Non-academic departments have no courses, so they expect none.
        var perCourseLists = await db.RequirementSets.AsNoTracking()
            .Where(s => s.Status == RequirementSetStatus.Published && s.SubmissionType.PerCourse && s.SubmissionType.IsActive)
            .Select(s => new { s.SubmissionTypeId, s.ScopeType, s.ScopeId })
            .ToListAsync(ct);
        var perCourseTypes = inScope.ToDictionary(p => p.Id, p => perCourseLists
            .Where(l => (l.ScopeType == RequirementScope.Program && l.ScopeId == p.Id)
                || (l.ScopeType == RequirementScope.Department && l.ScopeId == p.DepartmentId)
                || (l.ScopeType == RequirementScope.College && l.ScopeId == p.CollegeId))
            .Select(l => l.SubmissionTypeId).Distinct().ToList());
        var covered = submissions.Where(s => s.CourseId != null && s.Status != SubmissionStatus.Rejected)
            .Select(s => (s.SubmitterId, s.CourseId!.Value, s.TypeId)).ToHashSet();

        (int Expected, int Done) Coverage(IEnumerable<Unit> units, Guid? person = null)
        {
            var (expected, done) = (0, 0);
            foreach (var p in units)
                foreach (var line in load.Where(l => l.ProgramId == p.Id && (person == null || l.UserId == person)))
                    foreach (var typeId in perCourseTypes[p.Id])
                    {
                        expected++;
                        if (covered.Contains((line.UserId, line.CourseId, typeId))) done++;
                    }
            return (expected, done);
        }

        static StatusCounts Counts(IReadOnlyCollection<Item> items)
        {
            int Count(SubmissionStatus status) => items.Count(s => s.Status == status);
            return new StatusCounts(Count(SubmissionStatus.Submitted), Count(SubmissionStatus.UnderReview), Count(SubmissionStatus.Resubmitted),
                Count(SubmissionStatus.RevisionRequested), Count(SubmissionStatus.Approved), Count(SubmissionStatus.Rejected));
        }

        DashboardRow Row(Guid id, string code, string name, string? group, PersonRef? lead, IReadOnlyCollection<Guid> expected,
            IReadOnlyCollection<Item> own, (int Expected, int Done) courses)
        {
            var ids = own.Select(s => s.Id).ToHashSet();
            var submitted = expected.Count(f => own.Any(s => s.SubmitterId == f));
            var days = decisionDays.Where(d => ids.Contains(d.SubmissionId)).Select(d => d.Days).ToList();
            var approved = own.Where(s => s.Status == SubmissionStatus.Approved).ToList();
            return new DashboardRow(id, code, name, group, lead,
                expected.Count, submitted, expected.Count == 0 ? 0 : (int)Math.Round(100.0 * submitted / expected.Count),
                courses.Expected, courses.Done,
                own.Count,
                own.Count(s => WorkflowRules.IsPending(s.Status)),
                own.Count(IsOverdue),
                days.Count == 0 ? null : Math.Round(days.Average(), 1),
                approved.Count,
                approved.Count(s => s.FinalizedAt is { } f && now - f <= TimeSpan.FromDays(7)),
                Counts(own),
                own.Max(s => s.SubmittedAt));
        }

        List<Item> Of(IEnumerable<Unit> units)
        {
            var ids = units.Select(p => p.Id).ToHashSet();
            return submissions.Where(s => ids.Contains(s.UnitId)).ToList();
        }

        // A program's faculty or a department's staff, plus anyone else who submitted for it (its AS or Head, say), most behind first.
        List<DashboardRow> PeopleRows(Unit unit) => unit.People.Concat(submissions.Select(s => s.SubmitterId)).Distinct()
            .Select(id =>
            {
                var person = Person(id);
                return Row(id, person.Title ?? "", person.Name, null, null,
                    unit.People.Contains(id) ? [id] : [], submissions.Where(s => s.SubmitterId == id).ToList(), Coverage([unit], id));
            })
            .OrderBy(r => r.Submissions > 0).ThenByDescending(r => r.Overdue).ThenBy(r => r.Name)
            .ToList();

        var lead = level switch
        {
            DashboardLevel.Program => Holder(Role.AcademicSupervisor, program!.Id),
            DashboardLevel.Department => Holder(Role.DepartmentHead, office!.Id),
            DashboardLevel.College => Holder(Role.Dean, college!.Id),
            _ => null,
        };
        var totals = Row(scope.Id ?? Guid.Empty, scope.Code, scope.Name, null, lead, everyone, submissions, Coverage(inScope));

        var rows = level switch
        {
            DashboardLevel.University => colleges.Select(c =>
            {
                var units = inScope.Where(p => p.CollegeId == c.Id).ToList();
                return Row(c.Id!.Value, c.Code, c.Name, null, Holder(Role.Dean, c.Id), units.SelectMany(p => p.People).ToHashSet(), Of(units),
                    Coverage(units));
            }).ToList(),
            DashboardLevel.College => inScope
                .Select(p => Row(p.Id, p.Code, p.Name, p.DepartmentName, Holder(Role.AcademicSupervisor, p.Id), p.People, Of([p]), Coverage([p])))
                .ToList(),
            DashboardLevel.NonAcademic => inScope
                .Select(d => Row(d.Id, d.Code, d.Name, null, Holder(Role.DepartmentHead, d.Id), d.People, Of([d]), Coverage([d])))
                .ToList(),
            _ => PeopleRows((program ?? office)!),
        };

        var byType = submissions.GroupBy(s => s.TypeId)
            .Select(g => new DashboardTypeRow(types[g.Key], g.Count(), g.Count(s => WorkflowRules.IsPending(s.Status)), g.Count(IsOverdue),
                Counts(g.ToList())))
            .OrderByDescending(t => t.Submissions).ThenBy(t => t.Type.Name)
            .ToList();

        var unitOf = inScope.ToDictionary(u => u.Id);
        // Routed past the unit's AS or Head because there is none, as opposed to because they submitted it.
        bool UnitHeadVacant(Item s) => Holder(unitOf[s.UnitId].Office ? Role.DepartmentHead : Role.AcademicSupervisor, s.UnitId) is null;
        var overdue = submissions
            .Where(IsOverdue)
            .Select(s =>
            {
                var a = openAssignment[s.Id];
                return new OverdueItem(s.Id, s.Number, s.Title, unitOf[s.UnitId].Code, Person(s.SubmitterId),
                    people.GetValueOrDefault(a.ReviewerUserId), a.ReviewerUserId == me.UserId,
                    a.Reason == AssignmentReason.Fallback && UnitHeadVacant(s), (int)Math.Floor((now - a.AssignedAt).TotalDays));
            })
            .OrderByDescending(o => o.AgeDays)
            .ToList();

        var byId = submissions.ToDictionary(s => s.Id);
        var activity = actions
            .Where(a => a.ActionType != ReviewActionType.Created)
            .Take(10)
            .Select(a => new ActivityItem(a.OccurredAt, a.ActionType, Person(a.ActorUserId),
                a.SubmissionId, byId[a.SubmissionId].Number, byId[a.SubmissionId].Title, unitOf[byId[a.SubmissionId].UnitId].Code))
            .ToList();

        // Up: the levels above this one the caller may open. Across: the caller's other colleges, programs or departments;
        // for the VPAF and admin at the top, the other root.
        var trail = new List<DashboardScopeRef>();
        if (level is DashboardLevel.NonAcademic or DashboardLevel.Department)
        {
            if (wide) trail.Add(NonAcademicRoot);
            if (office is not null) trail.Add(scope);
        }
        else
        {
            if (wide) trail.Add(UniversityRoot);
            if (college is not null && CanSeeCollege(college.Id)) trail.Add(college);
            if (program is not null) trail.Add(scope);
        }
        List<DashboardScopeRef> options = wide
            ? level is DashboardLevel.University or DashboardLevel.NonAcademic ? [UniversityRoot, NonAcademicRoot] : []
            : colleges.Where(c => deanOf.Contains(c.Id!.Value))
                .Concat(programs.Where(p => supervises.Contains(p.Id) && !CanSeeCollege(p.CollegeId)).Select(ProgramRef))
                .Concat(offices.Where(d => heads.Contains(d.Id)).Select(OfficeRef))
                .ToList();

        return new SubmissionDashboard(level, scope, trail, options, term, totals, rows, byType, overdue, activity);
    }
}
