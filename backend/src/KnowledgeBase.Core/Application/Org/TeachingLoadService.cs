using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Org;

/// <summary>
/// The per-term teaching load: who teaches which course of which program. The registrar's file
/// replaces a term's load (lines missing from it are removed for that term only), registers new
/// people and courses, and adds the program memberships the load implies. Documents, folders and
/// other terms are never touched.
/// </summary>
public sealed partial class TeachingLoadService(
    KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, IAcademicFolderService folders, TimeProvider clock)
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxRows = 20_000;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailFormat();

    private static readonly SubmissionStatus[] Dropped = [SubmissionStatus.Withdrawn, SubmissionStatus.Rejected];

    /// <summary>A term's load (the active term when none is given), for administrators and the VPAF.</summary>
    public async Task<TeachingLoadDto> GetAsync(Guid? termId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        if (!me.IsAdmin && !me.IsVpaf) throw new ForbiddenException("Only an administrator or the VPAF can see every teaching load.");
        var term = await TermAsync(termId, ct);

        var lines = await db.TeachingAssignments.AsNoTracking()
            .Where(a => a.AcademicTermId == term.Id)
            .Select(a => new
            {
                a.Id, a.User, a.Section, a.CourseId, a.Course.Code, a.Course.Title,
                ProgramId = a.Course.Program.Id, ProgramCode = a.Course.Program.Code, ProgramName = a.Course.Program.Name,
            })
            .ToListAsync(ct);
        var assignments = lines
            .OrderBy(l => l.ProgramCode).ThenBy(l => l.User.DisplayName).ThenBy(l => l.Code).ThenBy(l => l.Section)
            .Select(l => new TeachingAssignmentDto(l.Id, PersonDto.From(l.User),
                new CourseRefDto(l.CourseId, l.Code, l.Title, new UnitRefDto(l.ProgramId, l.ProgramCode, l.ProgramName)),
                l.Section.Length == 0 ? null : l.Section))
            .ToList();

        var last = await db.TeachingLoadImports.AsNoTracking().Include(i => i.ImportedBy)
            .Where(i => i.AcademicTermId == term.Id)
            .OrderByDescending(i => i.ImportedAt)
            .FirstOrDefaultAsync(ct);

        return new TeachingLoadDto(ToDto(term), assignments,
            lines.Select(l => l.User.Id).Distinct().Count(), lines.Select(l => l.CourseId).Distinct().Count(),
            last is null ? null : new TeachingImportSummaryDto(last.FileName, PersonDto.From(last.ImportedBy), last.ImportedAt,
                last.Rows, last.Added, last.Removed, last.PeopleCreated, last.CoursesCreated));
    }

    /// <summary>The caller's courses in a term; with a submission type, each course's live submission of that type.</summary>
    public async Task<IReadOnlyList<MyCourseDto>> GetMyCoursesAsync(Guid? termId, Guid? submissionTypeId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var term = await TermAsync(termId, ct);
        var lines = await db.TeachingAssignments.AsNoTracking()
            .Where(a => a.UserId == me.UserId && a.AcademicTermId == term.Id)
            .Select(a => new
            {
                a.CourseId, a.Course.Code, a.Course.Title, a.Section,
                ProgramId = a.Course.Program.Id, ProgramCode = a.Course.Program.Code, ProgramName = a.Course.Program.Name,
            })
            .ToListAsync(ct);

        var submissions = submissionTypeId is { } typeId
            ? await db.Submissions.AsNoTracking()
                .Where(s => s.SubmittedByUserId == me.UserId && s.AcademicTermId == term.Id && s.SubmissionTypeId == typeId
                    && s.CourseId != null && !Dropped.Contains(s.Status))
                .Select(s => new { CourseId = s.CourseId!.Value, s.Id, s.SubmissionNumber, s.Status })
                .ToListAsync(ct)
            : [];

        return lines
            .GroupBy(l => l.CourseId)
            .Select(g =>
            {
                var l = g.First();
                var live = submissions.FirstOrDefault(s => s.CourseId == g.Key);
                return new MyCourseDto(
                    new CourseRefDto(l.CourseId, l.Code, l.Title, new UnitRefDto(l.ProgramId, l.ProgramCode, l.ProgramName)),
                    g.Select(x => x.Section).Where(s => s.Length > 0).Order().ToList(),
                    live is null ? null : new MyCourseSubmissionDto(live.Id, live.SubmissionNumber, live.Status));
            })
            .OrderBy(c => c.Course.Program.Code).ThenBy(c => c.Course.Code)
            .ToList();
    }

    /// <summary>Checks the file and returns what would change; with Apply, replaces the term's load.</summary>
    public async Task<TeachingImportResultDto> ImportAsync(ImportTeachingLoadRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        me.RequireAdmin();
        var term = await OpenTermAsync(request.AcademicTermId, ct);

        var fileName = Path.GetFileName(request.FileName?.Trim() ?? "");
        if (fileName.Length is 0 or > 260) throw new ValidationAppException("fileName", "Name the file (at most 260 characters).");
        byte[] content;
        try
        {
            content = Convert.FromBase64String(request.ContentBase64 ?? "");
        }
        catch (FormatException)
        {
            throw new ValidationAppException("file", "The file didn't arrive intact. Try again.");
        }
        if (content.Length == 0) throw new ValidationAppException("file", "The file is empty.");
        if (content.Length > MaxFileBytes) throw new ValidationAppException("file", "Use a file of at most 5 MB.");

        var parsed = TeachingLoadFile.Parse(fileName, content);
        if (parsed.Rows.Count == 0) throw new ValidationAppException("file", "The file has a header row but no teaching lines.");
        if (parsed.Rows.Count > MaxRows) throw new ValidationAppException("file", $"Use at most {MaxRows:N0} lines per file.");

        var plan = await PlanAsync(term.Id, parsed.Rows, ct);
        if (request.Apply)
        {
            if (plan.Errors.Count > 0)
                throw new BusinessRuleException("import-has-errors", "Fix the file first.",
                    $"{plan.Errors.Count} line(s) have errors, so nothing was changed.");
            await ApplyAsync(term, fileName, parsed.Rows.Count, plan, me.UserId, ct);
        }

        string Person(Guid userId) => plan.People[userId].DisplayName;
        string? FacultyId(Guid userId) => plan.People[userId].ExternalId;
        ImportLoadLineDto Line(Guid userId, Course course, string section) =>
            new(Person(userId), FacultyId(userId), plan.Programs[course.ProgramId].Code, course.Code, course.Title,
                section.Length == 0 ? null : section);

        return new TeachingImportResultDto(
            ToDto(term), fileName, request.Apply, parsed.Rows.Count, parsed.Columns,
            plan.Errors, plan.Warnings,
            plan.NewPeople.Select(u => new ImportPersonDto(u.ExternalId, u.DisplayName, u.Email)).ToList(),
            plan.NewCourses.Select(c => new ImportCourseDto(plan.Programs[c.ProgramId].Code, c.Code, c.Title, null)).ToList(),
            plan.Renamed.Select(r => new ImportCourseDto(plan.Programs[r.Course.ProgramId].Code, r.Course.Code, r.Title, r.Previous)).ToList(),
            plan.NewMemberships.Select(m => new ImportMembershipDto(Person(m.UserId), plan.Programs[m.ProgramId].Code)).ToList(),
            plan.Added.Select(a => Line(a.UserId, plan.Courses[a.CourseId], a.Section)).ToList(),
            plan.Removed.Select(a => Line(a.UserId, plan.Courses[a.CourseId], a.Section)).ToList(),
            plan.Unchanged);
    }

    // ---- encoding by hand

    /// <summary>A program's course catalog, for picking a course when encoding a load.</summary>
    public async Task<IReadOnlyList<CourseRefDto>> GetCoursesAsync(Guid programId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        if (!me.IsAdmin && !me.IsVpaf) throw new ForbiddenException("Only an administrator or the VPAF can see course catalogs.");
        var program = await db.Programs.AsNoTracking().Where(p => p.Id == programId && p.IsActive)
            .Select(p => new UnitRefDto(p.Id, p.Code, p.Name))
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Program", programId);
        var courses = await db.Courses.AsNoTracking().Where(c => c.ProgramId == programId && c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Code, c.Title })
            .ToListAsync(ct);
        return courses.Select(c => new CourseRefDto(c.Id, c.Code, c.Title, program)).ToList();
    }

    /// <summary>
    /// Sets a faculty member's sections of one course in a term, like one group of lines in the
    /// registrar's file: missing courses join the catalog and missing program memberships are added.
    /// </summary>
    public async Task<TeachingLoadChangeDto> SetCourseAsync(SetTeachingCourseRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        me.RequireAdmin();
        var term = await OpenTermAsync(request.AcademicTermId, ct);
        var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new ValidationAppException("userId", "Pick a faculty member.");
        if (!person.IsActive)
            throw new ValidationAppException("userId", $"{person.DisplayName}'s account is deactivated. Reactivate it in Users & roles first.");
        var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.ProgramId && p.IsActive, ct)
            ?? throw new ValidationAppException("programId", "Pick a program.");
        var sections = Sections(request.Sections);
        var (course, courseCreated) = await ResolveCourseAsync(program, request, ct);

        var replaces = request.ReplacesCourseId is { } r && r != course.Id ? r : (Guid?)null;
        var current = await db.TeachingAssignments
            .Where(a => a.AcademicTermId == term.Id && a.UserId == person.Id && (a.CourseId == course.Id || a.CourseId == replaces))
            .ToListAsync(ct);
        var kept = current.Where(a => a.CourseId == course.Id && sections.Contains(a.Section)).ToList();
        var removed = current.Except(kept).ToList();
        var added = sections.Where(s => kept.All(a => a.Section != s))
            .Select(s => new TeachingAssignment { AcademicTermId = term.Id, UserId = person.Id, CourseId = course.Id, Section = s })
            .ToList();

        // Teaching in a program makes the person one of its faculty, as an import does.
        var memberships = await db.FacultyPrograms.AsNoTracking().Where(f => f.UserId == person.Id).ToListAsync(ct);
        var membershipAdded = memberships.All(f => f.ProgramId != program.Id);
        if (membershipAdded)
            db.FacultyPrograms.Add(new FacultyProgram { UserId = person.Id, ProgramId = program.Id, IsPrimary = !memberships.Any(f => f.IsPrimary) });

        db.TeachingAssignments.RemoveRange(removed);
        db.TeachingAssignments.AddRange(added);
        if (added.Count + removed.Count > 0 || courseCreated || membershipAdded)
        {
            audit.Record("teaching-load.course-set", "academic-term", term.Id, metadata: new
            {
                request.UserId, CourseId = course.Id, course.Code, Sections = sections, Added = added.Count, Removed = removed.Count,
                CourseCreated = courseCreated, MembershipAdded = membershipAdded, ReplacesCourseId = replaces,
            });
            await db.SaveChangesAsync(ct);
        }
        if (membershipAdded) await folders.SyncAsync(ct); // their faculty folder in the program
        var note = replaces is { } old && await db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == old, ct) is { } oldCourse
            ? await LeftCourseNoteAsync(term.Id, person, oldCourse, ct)
            : null;

        var courseRef = new CourseRefDto(course.Id, course.Code, course.Title, new UnitRefDto(program.Id, program.Code, program.Name));
        var lines = kept.Concat(added).OrderBy(a => a.Section)
            .Select(a => new TeachingAssignmentDto(a.Id, PersonDto.From(person), courseRef, a.Section.Length == 0 ? null : a.Section))
            .ToList();
        return new TeachingLoadChangeDto(lines, added.Count, removed.Count, courseCreated, membershipAdded, note);
    }

    /// <summary>Takes a course (every section of it) out of a faculty member's load for a term. They stay in the program's faculty.</summary>
    public async Task<TeachingLoadChangeDto> RemoveCourseAsync(Guid termId, Guid userId, Guid courseId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        me.RequireAdmin();
        var term = await OpenTermAsync(termId, ct);
        var lines = await db.TeachingAssignments.Include(a => a.User).Include(a => a.Course)
            .Where(a => a.AcademicTermId == term.Id && a.UserId == userId && a.CourseId == courseId)
            .ToListAsync(ct);
        if (lines.Count == 0) throw new NotFoundException("Teaching load line", courseId);

        var (person, course) = (lines[0].User, lines[0].Course);
        db.TeachingAssignments.RemoveRange(lines);
        audit.Record("teaching-load.course-removed", "academic-term", term.Id, metadata: new
        {
            UserId = userId, CourseId = courseId, course.Code, Removed = lines.Count,
        });
        await db.SaveChangesAsync(ct);
        return new TeachingLoadChangeDto([], 0, lines.Count, false, false, await LeftCourseNoteAsync(term.Id, person, course, ct));
    }

    /// <summary>The course from the catalog, by id or by code (ignoring case, spaces and dashes); a new code joins the catalog.</summary>
    private async Task<(Course Course, bool Created)> ResolveCourseAsync(AcademicProgram program, SetTeachingCourseRequest request, CancellationToken ct)
    {
        if (request.CourseId is { } courseId)
        {
            var picked = await db.Courses.SingleOrDefaultAsync(c => c.Id == courseId && c.ProgramId == program.Id, ct)
                ?? throw new ValidationAppException("courseId", $"Pick one of {program.Code}'s courses.");
            return (picked, false);
        }

        var code = request.CourseCode?.Trim() ?? "";
        if (code.Length == 0) throw new ValidationAppException("courseCode", "Give the course code, e.g. CS 301.");
        if (code.Length > 40) throw new ValidationAppException("courseCode", "Course codes are at most 40 characters.");
        var key = Course.KeyOf(code);
        var catalog = await db.Courses.Where(c => c.ProgramId == program.Id).ToListAsync(ct);
        if (catalog.FirstOrDefault(c => Course.KeyOf(c.Code) == key) is { } known) return (known, false);

        var title = request.CourseTitle?.Trim() ?? "";
        if (title.Length == 0)
            throw new ValidationAppException("courseTitle", $"{code.ToUpperInvariant()} is a new {program.Code} course, so give its title.");
        if (title.Length > 200) throw new ValidationAppException("courseTitle", "Course titles are at most 200 characters.");
        var course = new Course { ProgramId = program.Id, Code = code.ToUpperInvariant(), Title = title };
        db.Courses.Add(course);
        return (course, true);
    }

    /// <summary>Sections as the import stores them: trimmed, upper case, each once; none = one line without a section.</summary>
    private static List<string> Sections(IReadOnlyList<string>? sections)
    {
        var list = (sections ?? []).Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().Order().ToList();
        if (list.Any(s => s.Length > 40)) throw new ValidationAppException("sections", "Sections are at most 40 characters.");
        if (list.Count > 50) throw new ValidationAppException("sections", "Use at most 50 sections per course.");
        return list.Count == 0 ? [""] : list;
    }

    /// <summary>Removing a course never touches submissions; this says which one lost its place in the load.</summary>
    private async Task<string?> LeftCourseNoteAsync(Guid termId, User person, Course course, CancellationToken ct)
    {
        var stillTeaches = await db.TeachingAssignments.AnyAsync(a => a.AcademicTermId == termId && a.UserId == person.Id && a.CourseId == course.Id, ct);
        if (stillTeaches) return null;
        var number = await db.Submissions.AsNoTracking()
            .Where(s => s.AcademicTermId == termId && s.SubmittedByUserId == person.Id && s.CourseId == course.Id && !Dropped.Contains(s.Status))
            .Select(s => s.SubmissionNumber)
            .FirstOrDefaultAsync(ct);
        return number is null ? null : $"{person.DisplayName} no longer teaches {course.Code}; {number} stays as it is.";
    }

    // ---- planning

    private sealed record ProgramRef(Guid Id, string Code, string Name);

    private sealed record LoadLine(Guid UserId, Guid CourseId, string Section);

    private sealed class Plan
    {
        public List<ImportIssueDto> Errors { get; } = [];
        public List<ImportIssueDto> Warnings { get; } = [];
        public Dictionary<Guid, ProgramRef> Programs { get; init; } = [];
        /// <summary>Everyone the file names, existing or new, by id.</summary>
        public Dictionary<Guid, User> People { get; } = [];
        public Dictionary<Guid, Course> Courses { get; } = [];
        public List<User> NewPeople { get; } = [];
        public List<Course> NewCourses { get; } = [];
        public List<(Course Course, string Title, string Previous)> Renamed { get; } = [];
        public List<(Guid UserId, Guid ProgramId)> NewMemberships { get; } = [];
        public List<LoadLine> Added { get; } = [];
        public List<TeachingAssignment> Removed { get; } = [];
        public int Unchanged { get; set; }
    }

    private async Task<Plan> PlanAsync(Guid termId, IReadOnlyList<TeachingLoadFile.Row> rows, CancellationToken ct)
    {
        var programs = await db.Programs.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new ProgramRef(p.Id, p.Code, p.Name))
            .ToListAsync(ct);
        var plan = new Plan { Programs = programs.ToDictionary(p => p.Id) };
        var programByCode = programs.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

        var ids = rows.Select(r => r.FacultyId?.ToLowerInvariant()).OfType<string>().Distinct().ToList();
        var emails = rows.Select(r => r.Email?.ToLowerInvariant()).OfType<string>().Distinct().ToList();
        var known = await db.Users
            .Where(u => ids.Contains(u.ExternalId.ToLower()) || (u.Email != null && emails.Contains(u.Email.ToLower())))
            .ToListAsync(ct);
        var byId = known.GroupBy(u => u.ExternalId.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var byEmail = known.Where(u => u.Email is not null).GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());

        // Existing lines are loaded tracked so Apply can remove them; their people and courses join the lookups.
        var existing = await db.TeachingAssignments.Include(a => a.User).Include(a => a.Course)
            .Where(a => a.AcademicTermId == termId)
            .ToListAsync(ct);
        foreach (var a in existing)
        {
            plan.People.TryAdd(a.UserId, a.User);
            plan.Courses.TryAdd(a.CourseId, a.Course);
        }

        var codes = rows.Select(r => r.Program).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var programIds = programs.Where(p => codes.Contains(p.Code, StringComparer.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
        var courses = await db.Courses.Where(c => programIds.Contains(c.ProgramId)).ToListAsync(ct);
        foreach (var c in courses) plan.Courses.TryAdd(c.Id, c);
        var courseByKey = plan.Courses.Values.GroupBy(c => (c.ProgramId, Course.KeyOf(c.Code))).ToDictionary(g => g.Key, g => g.First());

        var knownIds = known.Select(u => u.Id).ToList();
        var memberships = (await db.FacultyPrograms.AsNoTracking().Where(f => knownIds.Contains(f.UserId))
                .Select(f => new { f.UserId, f.ProgramId }).ToListAsync(ct))
            .Select(f => (f.UserId, f.ProgramId)).ToHashSet();

        var newPeopleByKey = new Dictionary<string, User>();
        var titleSeen = new Dictionary<Guid, (string Title, int Line)>();
        var lineSeen = new Dictionary<LoadLine, int>();
        var wanted = new List<LoadLine>();
        var programList = string.Join(", ", programs.Select(p => p.Code).Order().Take(12));

        foreach (var row in rows)
        {
            void Error(string message) => plan.Errors.Add(new ImportIssueDto(row.Line, message));
            void Warn(string message) => plan.Warnings.Add(new ImportIssueDto(row.Line, message));

            if (row.Program is null) { Error("No program."); continue; }
            if (!programByCode.TryGetValue(row.Program, out var program))
            {
                Error($"Unknown program “{row.Program}”. Use one of: {programList}.");
                continue;
            }
            if (row.CourseCode is null) { Error("No course code."); continue; }
            if (row.CourseCode.Length > 40) { Error("Course codes are at most 40 characters."); continue; }
            if (row.FacultyId is null && row.Email is null) { Error("No faculty id or email."); continue; }
            if (row.Email is not null && !EmailFormat().IsMatch(row.Email)) { Error($"“{row.Email}” isn't an email address."); continue; }
            var section = (row.Section ?? "").ToUpperInvariant();
            if (section.Length > 40) { Error("Sections are at most 40 characters."); continue; }

            // The person: by sign-in id first, then by email; otherwise someone new.
            var idKey = row.FacultyId?.ToLowerInvariant();
            var emailKey = row.Email?.ToLowerInvariant();
            var person = (idKey is not null ? byId.GetValueOrDefault(idKey) : null) ?? (emailKey is not null ? byEmail.GetValueOrDefault(emailKey) : null);
            if (person is not null)
            {
                if (!person.IsActive)
                {
                    Error($"{person.DisplayName}'s account is deactivated. Reactivate it in Users & roles, or remove the line.");
                    continue;
                }
                if (idKey is not null && emailKey is not null && byEmail.GetValueOrDefault(emailKey) is { } other && other.Id != person.Id)
                    Warn($"{row.Email} belongs to {other.DisplayName}; the line was matched to {person.DisplayName} by faculty id.");
            }
            else
            {
                var key = idKey ?? emailKey!;
                if (!newPeopleByKey.TryGetValue(key, out person))
                {
                    if (row.Name is null) { Error($"{row.FacultyId ?? row.Email} isn't registered yet, so the line needs a faculty name."); continue; }
                    if (row.Name.Length > 200 || (row.FacultyId?.Length ?? 0) > 200) { Error("Names and faculty ids are at most 200 characters."); continue; }
                    person = new User { ExternalId = row.FacultyId ?? row.Email!, DisplayName = row.Name, Email = row.Email };
                    newPeopleByKey[key] = person;
                    if (emailKey is not null) newPeopleByKey.TryAdd(emailKey, person);
                    plan.NewPeople.Add(person);
                }
                else if (row.Name is not null && !string.Equals(row.Name, person.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    Warn($"Named “{row.Name}” here but “{person.DisplayName}” earlier; the first name is used.");
                }
            }
            plan.People.TryAdd(person.Id, person);

            // The course: by program and code, ignoring case, spaces and dashes.
            var courseKey = (program.Id, Course.KeyOf(row.CourseCode));
            if (!courseByKey.TryGetValue(courseKey, out var course))
            {
                if (row.CourseTitle is null) { Error($"{row.CourseCode} is a new {program.Code} course, so the line needs a course title."); continue; }
                if (row.CourseTitle.Length > 200) { Error("Course titles are at most 200 characters."); continue; }
                course = new Course { ProgramId = program.Id, Code = row.CourseCode.ToUpperInvariant(), Title = row.CourseTitle };
                courseByKey[courseKey] = course;
                plan.Courses[course.Id] = course;
                plan.NewCourses.Add(course);
                titleSeen[course.Id] = (course.Title, row.Line);
            }
            else if (row.CourseTitle is not null && row.CourseTitle.Length <= 200)
            {
                if (titleSeen.TryGetValue(course.Id, out var seen))
                {
                    if (!SameText(seen.Title, row.CourseTitle))
                        Warn($"{course.Code} is titled “{seen.Title}” on line {seen.Line}; that title is used.");
                }
                else
                {
                    titleSeen[course.Id] = (row.CourseTitle, row.Line);
                    if (!plan.NewCourses.Contains(course) && !SameText(course.Title, row.CourseTitle)) plan.Renamed.Add((course, row.CourseTitle, course.Title));
                }
            }

            var line = new LoadLine(person.Id, course.Id, section);
            if (lineSeen.TryGetValue(line, out var first))
            {
                Warn($"Same as line {first}; counted once.");
                continue;
            }
            lineSeen[line] = row.Line;
            wanted.Add(line);

            if (!memberships.Contains((person.Id, program.Id)))
            {
                memberships.Add((person.Id, program.Id));
                plan.NewMemberships.Add((person.Id, program.Id));
            }
        }

        var current = existing.ToDictionary(a => new LoadLine(a.UserId, a.CourseId, a.Section));
        var wantedSet = wanted.ToHashSet();
        plan.Added.AddRange(wanted.Where(l => !current.ContainsKey(l)));
        plan.Removed.AddRange(existing.Where(a => !wantedSet.Contains(new LoadLine(a.UserId, a.CourseId, a.Section))));
        plan.Unchanged = wanted.Count - plan.Added.Count;

        // Removing a line never touches submissions, but say which ones lose their place in the load.
        if (plan.Removed.Count > 0)
        {
            var removedUsers = plan.Removed.Select(a => a.UserId).Distinct().ToList();
            var removedCourses = plan.Removed.Select(a => a.CourseId).Distinct().ToList();
            var keep = wanted.Select(l => (l.UserId, l.CourseId)).ToHashSet();
            var affected = await db.Submissions.AsNoTracking()
                .Where(s => s.AcademicTermId == termId && s.CourseId != null && removedUsers.Contains(s.SubmittedByUserId)
                    && removedCourses.Contains(s.CourseId!.Value) && !Dropped.Contains(s.Status))
                .Select(s => new { s.SubmissionNumber, s.SubmittedByUserId, CourseId = s.CourseId!.Value })
                .ToListAsync(ct);
            foreach (var s in affected.Where(s => !keep.Contains((s.SubmittedByUserId, s.CourseId))))
                plan.Warnings.Add(new ImportIssueDto(0,
                    $"{plan.People[s.SubmittedByUserId].DisplayName} no longer teaches {plan.Courses[s.CourseId].Code}; {s.SubmissionNumber} stays as it is."));
        }
        return plan;
    }

    private static bool SameText(string a, string b) =>
        string.Equals(string.Join(' ', a.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            string.Join(' ', b.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    private async Task ApplyAsync(AcademicTerm term, string fileName, int rows, Plan plan, Guid importedBy, CancellationToken ct)
    {
        db.Users.AddRange(plan.NewPeople);
        db.Courses.AddRange(plan.NewCourses);
        foreach (var (course, title, _) in plan.Renamed) course.Title = title;

        // A person's first program is their primary one.
        var userIds = plan.NewMemberships.Select(m => m.UserId).Distinct().ToList();
        var hasPrimary = (await db.FacultyPrograms.AsNoTracking().Where(f => userIds.Contains(f.UserId) && f.IsPrimary)
            .Select(f => f.UserId).ToListAsync(ct)).ToHashSet();
        foreach (var (userId, programId) in plan.NewMemberships)
            db.FacultyPrograms.Add(new FacultyProgram { UserId = userId, ProgramId = programId, IsPrimary = hasPrimary.Add(userId) });

        db.TeachingAssignments.RemoveRange(plan.Removed);
        db.TeachingAssignments.AddRange(plan.Added.Select(l => new TeachingAssignment
        {
            AcademicTermId = term.Id, UserId = l.UserId, CourseId = l.CourseId, Section = l.Section,
        }));

        var record = new TeachingLoadImport
        {
            AcademicTermId = term.Id,
            FileName = fileName,
            ImportedByUserId = importedBy,
            ImportedAt = clock.GetUtcNow(),
            Rows = rows,
            Added = plan.Added.Count,
            Removed = plan.Removed.Count,
            PeopleCreated = plan.NewPeople.Count,
            CoursesCreated = plan.NewCourses.Count,
        };
        db.TeachingLoadImports.Add(record);
        audit.Record("teaching-load.imported", "academic-term", term.Id, metadata: new
        {
            FileName = fileName, Rows = rows, record.Added, record.Removed, record.PeopleCreated, record.CoursesCreated,
            Memberships = plan.NewMemberships.Count, Renamed = plan.Renamed.Count,
        });
        await db.SaveChangesAsync(ct);

        // New people and memberships get their faculty folders straight away.
        if (plan.NewMemberships.Count > 0) await folders.SyncAsync(ct);
    }

    // ---- terms

    /// <summary>A term whose load can still change: a closed term's load stays as it was.</summary>
    private async Task<AcademicTerm> OpenTermAsync(Guid termId, CancellationToken ct)
    {
        var term = await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Id == termId, ct)
            ?? throw new NotFoundException("Academic term", termId);
        if (term.Status == TermStatus.Closed)
            throw new BusinessRuleException("term-closed", "That term is closed.",
                $"{term.Name} {term.AcademicYear.Name} is closed, so its teaching load stays as it was.");
        return term;
    }

    private async Task<AcademicTerm> TermAsync(Guid? termId, CancellationToken ct) =>
        termId is { } id
            ? await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException("Academic term", id)
            : await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Status == TermStatus.Active, ct)
                ?? throw new NotFoundException("Active academic term");

    private static TeachingTermDto ToDto(AcademicTerm t) => new(t.Id, t.Code, t.Name, t.AcademicYear.Name, t.Status);
}
