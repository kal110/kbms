using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Org;

/// <summary>
/// Settings → Colleges &amp; departments: adds, renames, moves and deletes colleges, departments
/// (academic and non-academic) and programs. Every change runs the folder sync, which creates,
/// renames and moves the unit folders and rewrites their grants. A unit can be deleted only while it
/// holds nothing: no units, people, courses, submissions or documents. Its folders, required-documents
/// lists, grants naming it and its holder's role go with it.
/// </summary>
public sealed partial class OrgStructureService(
    KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, OrgService org, IAcademicFolderService folders, IAccessEvaluator access)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 .&-]*$")]
    private static partial Regex CodeFormat();

    public async Task<OrgStructureDto> GetAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        return await StructureAsync(ct);
    }

    // ---- colleges

    public async Task<OrgUnitAdminDto> CreateCollegeAsync(CreateCollegeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var code = Code(request.Code);
        var name = Name(request.Name);
        await UniqueCodeAsync(db.Colleges.Select(c => new Coded { Id = c.Id, Code = c.Code, Name = c.Name }), code, null, ct);

        var college = new College { Code = code, Name = name };
        db.Colleges.Add(college);
        audit.Record("college.created", "college", college.Id, metadata: new { Code = code, Name = name });
        await db.SaveChangesAsync(ct);
        return await SyncedAsync(college.Id, ct);
    }

    public async Task<OrgUnitAdminDto> UpdateCollegeAsync(Guid id, UpdateCollegeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var college = await db.Colleges.SingleOrDefaultAsync(c => c.Id == id && c.IsActive, ct) ?? throw new NotFoundException("College", id);
        var before = new { college.Code, college.Name };
        if (request.Code is not null)
        {
            college.Code = Code(request.Code);
            await UniqueCodeAsync(db.Colleges.Select(c => new Coded { Id = c.Id, Code = c.Code, Name = c.Name }), college.Code, id, ct);
        }
        if (request.Name is not null) college.Name = Name(request.Name);

        audit.Record("college.updated", "college", id, metadata: new { Before = before, After = new { college.Code, college.Name } });
        await db.SaveChangesAsync(ct);
        return await SyncedAsync(id, ct);
    }

    public async Task DeleteCollegeAsync(Guid id, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var college = await db.Colleges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.IsActive, ct)
            ?? throw new NotFoundException("College", id);
        await DeleteUnitAsync(new UnitKey(id, OrgUnitKind.College, college.Code), () => db.Colleges.Where(c => c.Id == id).ExecuteDeleteAsync(ct), ct);
    }

    // ---- departments

    public async Task<OrgUnitAdminDto> CreateDepartmentAsync(CreateDepartmentRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var code = Code(request.Code);
        var name = Name(request.Name);
        var shortName = ShortName(request.ShortName);
        var collegeId = await CollegeForAsync(request.Kind, request.CollegeId, ct);
        await UniqueCodeAsync(db.Departments.Select(d => new Coded { Id = d.Id, Code = d.Code, Name = d.Name }), code, null, ct);

        var dept = new Department { Code = code, Name = name, ShortName = shortName, Kind = request.Kind, CollegeId = collegeId };
        db.Departments.Add(dept);
        audit.Record("department.created", "department", dept.Id, metadata: new { Code = code, Name = name, request.Kind, CollegeId = collegeId });
        await db.SaveChangesAsync(ct);
        return await SyncedAsync(dept.Id, ct);
    }

    public async Task<OrgUnitAdminDto> UpdateDepartmentAsync(Guid id, UpdateDepartmentRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var dept = await db.Departments.SingleOrDefaultAsync(d => d.Id == id && d.IsActive, ct) ?? throw new NotFoundException("Department", id);
        var before = new { dept.Code, dept.Name, dept.ShortName, dept.CollegeId };
        if (request.Code is not null)
        {
            dept.Code = Code(request.Code);
            await UniqueCodeAsync(db.Departments.Select(d => new Coded { Id = d.Id, Code = d.Code, Name = d.Name }), dept.Code, id, ct);
        }
        if (request.Name is not null) dept.Name = Name(request.Name);
        if (request.ShortName is not null) dept.ShortName = ShortName(request.ShortName);
        if (request.CollegeId is { } collegeId && collegeId != dept.CollegeId)
            dept.CollegeId = await CollegeForAsync(dept.Kind, collegeId, ct);

        audit.Record("department.updated", "department", id, metadata: new
        {
            Before = before, After = new { dept.Code, dept.Name, dept.ShortName, dept.CollegeId },
        });
        await db.SaveChangesAsync(ct);
        return await SyncedAsync(id, ct);
    }

    public async Task DeleteDepartmentAsync(Guid id, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var dept = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.IsActive, ct)
            ?? throw new NotFoundException("Department", id);
        var kind = dept.Kind == DepartmentKind.Academic ? OrgUnitKind.Department : OrgUnitKind.Office;
        await DeleteUnitAsync(new UnitKey(id, kind, dept.Code), async () =>
        {
            // Library folders outside its own tree may name it as their department; they stay, unowned.
            await db.Folders.Where(f => f.DepartmentId == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.DepartmentId, (Guid?)null), ct);
            return await db.Departments.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        }, ct);
    }

    // ---- programs

    public async Task<OrgUnitAdminDto> CreateProgramAsync(CreateProgramRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var code = Code(request.Code);
        var name = Name(request.Name);
        await RequireAcademicDepartmentAsync(request.DepartmentId, ct);
        await UniqueCodeAsync(db.Programs.Select(p => new Coded { Id = p.Id, Code = p.Code, Name = p.Name }), code, null, ct);

        var program = new AcademicProgram { Code = code, Name = name, DepartmentId = request.DepartmentId };
        db.Programs.Add(program);
        audit.Record("program.created", "program", program.Id, metadata: new { Code = code, Name = name, request.DepartmentId });
        await db.SaveChangesAsync(ct);
        return await SyncedAsync(program.Id, ct);
    }

    public async Task<OrgUnitAdminDto> UpdateProgramAsync(Guid id, UpdateProgramRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == id && p.IsActive, ct) ?? throw new NotFoundException("Program", id);
        var before = new { program.Code, program.Name, program.DepartmentId };
        if (request.Code is not null)
        {
            program.Code = Code(request.Code);
            await UniqueCodeAsync(db.Programs.Select(p => new Coded { Id = p.Id, Code = p.Code, Name = p.Name }), program.Code, id, ct);
        }
        if (request.Name is not null) program.Name = Name(request.Name);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.DepartmentId is { } departmentId && departmentId != program.DepartmentId)
        {
            await RequireAcademicDepartmentAsync(departmentId, ct);
            program.DepartmentId = departmentId;
            // The sync moves the program and faculty folders; the year, term and course folders below
            // them, and the documents filed there, carry the department too.
            var folder = await db.Folders.AsNoTracking()
                .SingleOrDefaultAsync(f => f.IsSystem && f.Kind == FolderKind.Program && f.OrgRefId == id, ct);
            if (folder is not null)
            {
                await db.Folders.Where(f => f.Path.StartsWith(folder.Path))
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.DepartmentId, (Guid?)departmentId), ct);
                await db.Documents.Where(d => d.Folder.Path.StartsWith(folder.Path))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.DepartmentId, (Guid?)departmentId), ct);
            }
        }

        audit.Record("program.updated", "program", id, metadata: new
        {
            Before = before, After = new { program.Code, program.Name, program.DepartmentId },
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await SyncedAsync(id, ct);
    }

    public async Task DeleteProgramAsync(Guid id, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.IsActive, ct)
            ?? throw new NotFoundException("Program", id);
        await DeleteUnitAsync(new UnitKey(id, OrgUnitKind.Program, program.Code), () => db.Programs.Where(p => p.Id == id).ExecuteDeleteAsync(ct), ct);
    }

    // ---- deleting

    private sealed record UnitKey(Guid Id, OrgUnitKind Kind, string Code);

    private async Task DeleteUnitAsync(UnitKey unit, Func<Task<int>> deleteRow, CancellationToken ct)
    {
        var usage = (await UsageAsync(ct)).GetValueOrDefault(unit.Id) ?? Unused;
        if (usage.Blockers.Count > 0)
            throw new ConflictException("unit-in-use", $"{unit.Code} still has {Join(usage.Blockers)}.",
                "Move or remove them first. Units with submissions or documents stay, so their records keep their place.")
                .With("blockers", usage.Blockers);

        var keys = PrincipalKeysOf(unit);
        var unitFolder = await db.Folders.AsNoTracking()
            .SingleOrDefaultAsync(f => f.IsSystem && f.Kind == FolderKindOf(unit.Kind) && f.OrgRefId == unit.Id, ct);
        var subtree = unitFolder is null
            ? []
            : await db.Folders.AsNoTracking().Where(f => f.Path.StartsWith(unitFolder.Path)).Select(f => new { f.Id, f.Path }).ToListAsync(ct);
        var removedFolders = subtree.Select(f => f.Id).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Grants naming the unit (or its dean, supervisor or head) on folders that stay: their access is recomputed afterwards.
        var regranted = await db.DocumentPermissions.AsNoTracking()
            .Where(p => keys.Contains(p.PrincipalKey) && p.ResourceType == ResourceType.Folder && !removedFolders.Contains(p.ResourceId))
            .Select(p => p.ResourceId).Distinct()
            .ToListAsync(ct);
        await db.DocumentPermissions
            .Where(p => keys.Contains(p.PrincipalKey) || (p.ResourceType == ResourceType.Folder && removedFolders.Contains(p.ResourceId)))
            .ExecuteDeleteAsync(ct);
        await db.FolderEffectiveAccess.Where(a => removedFolders.Contains(a.FolderId) || keys.Contains(a.PrincipalKey)).ExecuteDeleteAsync(ct);

        // Its folders hold no documents (or it couldn't be deleted): deepest first, since a parent can't go before its children.
        foreach (var level in subtree.GroupBy(f => f.Path.Count(c => c == '/')).OrderByDescending(g => g.Key))
        {
            var ids = level.Select(f => f.Id).ToList();
            await db.Folders.Where(f => ids.Contains(f.Id)).ExecuteDeleteAsync(ct);
        }

        // Its Dean, Academic Supervisor or Head stops holding the role; its required-documents lists
        // (no submission was checked against them) go with it.
        var ended = await db.RoleAssignments.Where(r => r.ScopeId == unit.Id && r.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, false), ct);
        var lists = await db.RequirementSets.Where(s => s.ScopeId == unit.Id).ExecuteDeleteAsync(ct);
        await deleteRow();

        var resource = unit.Kind switch
        {
            OrgUnitKind.College => "college",
            OrgUnitKind.Program => "program",
            _ => "department",
        };
        audit.Record($"{resource}.deleted", resource, unit.Id, metadata: new
        {
            unit.Code, unit.Kind, Folders = removedFolders.Count, RequirementLists = lists, RolesEnded = ended, GrantsOn = regranted.Count,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await folders.SyncAsync(ct);
        foreach (var folderId in regranted) await access.RecomputeSubtreeAsync(folderId, ct);
    }

    private static IReadOnlyList<string> PrincipalKeysOf(UnitKey unit) => unit.Kind switch
    {
        OrgUnitKind.College => [PrincipalKeys.College(unit.Id), PrincipalKeys.Dean(unit.Id)],
        OrgUnitKind.Program => [PrincipalKeys.Program(unit.Id), PrincipalKeys.Supervisor(unit.Id)],
        _ => [PrincipalKeys.Department(unit.Id), PrincipalKeys.Head(unit.Id)],
    };

    private static FolderKind FolderKindOf(OrgUnitKind kind) => kind switch
    {
        OrgUnitKind.College => FolderKind.College,
        OrgUnitKind.Department => FolderKind.Department,
        OrgUnitKind.Office => FolderKind.Office,
        _ => FolderKind.Program,
    };

    // ---- the structure, with what each unit holds

    private static readonly OrgUnitUsageDto Unused = new(0, 0, 0, 0, 0, 0, []);

    private async Task<OrgUnitAdminDto> SyncedAsync(Guid id, CancellationToken ct)
    {
        await folders.SyncAsync(ct);
        var structure = await StructureAsync(ct);
        return structure.Colleges.Concat(structure.Offices)
            .SelectMany(u => new[] { u }.Concat(u.Children).Concat(u.Children.SelectMany(d => d.Children)))
            .Single(u => u.Id == id);
    }

    private async Task<OrgStructureDto> StructureAsync(CancellationToken ct)
    {
        var colleges = await db.Colleges.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Code).ToListAsync(ct);
        var programs = await db.Programs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
        var holders = await org.LoadHoldersAsync(ct);
        var usage = await UsageAsync(ct);

        OrgUnitAdminDto Node(Guid id, OrgUnitKind kind, string code, string name, string? shortName, Guid? parentId,
            (Role Role, Guid Scope)? holderKey, IReadOnlyList<OrgUnitAdminDto> children)
        {
            var holder = holderKey is { } key ? holders.GetValueOrDefault(key) : default;
            return new OrgUnitAdminDto(id, kind, code, name, shortName, parentId, holder.Person, holder.Title,
                usage.GetValueOrDefault(id) ?? Unused, children);
        }

        OrgUnitAdminDto Program(AcademicProgram p) =>
            Node(p.Id, OrgUnitKind.Program, p.Code, p.Name, null, p.DepartmentId, (Role.AcademicSupervisor, p.Id), []);

        OrgUnitAdminDto Department(Department d) => d.Kind == DepartmentKind.Academic
            ? Node(d.Id, OrgUnitKind.Department, d.Code, d.Name, d.ShortName, d.CollegeId, null,
                programs.Where(p => p.DepartmentId == d.Id).Select(Program).ToList())
            : Node(d.Id, OrgUnitKind.Office, d.Code, d.Name, d.ShortName, null, (Role.DepartmentHead, d.Id), []);

        return new OrgStructureDto(
            colleges.Select(c => Node(c.Id, OrgUnitKind.College, c.Code, c.Name, null, null, (Role.Dean, c.Id),
                departments.Where(d => d.Kind == DepartmentKind.Academic && d.CollegeId == c.Id).Select(Department).ToList())).ToList(),
            departments.Where(d => d.Kind == DepartmentKind.NonAcademic).Select(Department).ToList());
    }

    /// <summary>What every unit holds, and what keeps it from being deleted. Units holding nothing are left out.</summary>
    private async Task<Dictionary<Guid, OrgUnitUsageDto>> UsageAsync(CancellationToken ct)
    {
        var departmentsOf = await CountAsync(db.Departments.Where(d => d.CollegeId != null).Select(d => d.CollegeId!.Value), ct);
        var programsOf = await CountAsync(db.Programs.Select(p => p.DepartmentId), ct);
        var facultyOf = await CountAsync(db.FacultyPrograms.Select(f => f.ProgramId), ct);
        var staffOf = await CountAsync(db.DepartmentStaff.Select(s => s.DepartmentId), ct);
        var coursesOf = await CountAsync(db.Courses.Select(c => c.ProgramId), ct);
        var programSubmissions = await CountAsync(db.Submissions.Where(s => s.ProgramId != null).Select(s => s.ProgramId!.Value), ct);
        var unitSubmissions = await CountAsync(db.Submissions.Where(s => s.DepartmentId != null).Select(s => s.DepartmentId!.Value), ct);
        var listsOf = await CountAsync(db.RequirementSets.Select(s => s.ScopeId), ct);
        var checkedAgainst = await CountAsync(db.SubmissionRequirementSets.Select(s => s.RequirementSet.ScopeId), ct);
        var ownedDocuments = await CountAsync(db.Documents.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value), ct);

        // Documents filed anywhere below a unit's folder.
        var unitKinds = new[] { FolderKind.College, FolderKind.Department, FolderKind.Office, FolderKind.Program };
        var unitFolders = await db.Folders.AsNoTracking()
            .Where(f => f.IsSystem && unitKinds.Contains(f.Kind) && f.OrgRefId != null)
            .Select(f => new { Id = f.OrgRefId!.Value, f.Path })
            .ToListAsync(ct);
        var perPath = await db.Documents.AsNoTracking()
            .GroupBy(d => d.Folder.Path).Select(g => new { Path = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var filedIn = unitFolders.GroupBy(f => f.Id).ToDictionary(g => g.Key,
            g => perPath.Where(p => g.Any(f => p.Path.StartsWith(f.Path, StringComparison.Ordinal))).Sum(p => p.Count));

        var units = (await db.Colleges.AsNoTracking().Select(c => c.Id).ToListAsync(ct)).Select(id => (Id: id, Kind: OrgUnitKind.College))
            .Concat((await db.Departments.AsNoTracking().Select(d => new { d.Id, d.Kind }).ToListAsync(ct))
                .Select(d => (d.Id, Kind: d.Kind == DepartmentKind.Academic ? OrgUnitKind.Department : OrgUnitKind.Office)))
            .Concat((await db.Programs.AsNoTracking().Select(p => p.Id).ToListAsync(ct)).Select(id => (Id: id, Kind: OrgUnitKind.Program)));

        var result = new Dictionary<Guid, OrgUnitUsageDto>();
        foreach (var unit in units)
        {
            int Of(Dictionary<Guid, int> counts) => counts.GetValueOrDefault(unit.Id);
            var (children, people, courses, submissions) = unit.Kind switch
            {
                OrgUnitKind.College => (Of(departmentsOf), 0, 0, 0),
                OrgUnitKind.Department => (Of(programsOf), 0, 0, Of(unitSubmissions)),
                OrgUnitKind.Office => (0, Of(staffOf), 0, Of(unitSubmissions)),
                _ => (0, Of(facultyOf), Of(coursesOf), Of(programSubmissions)),
            };
            // Documents in its folders, or owned by the department from elsewhere in the library.
            var documents = Math.Max(Of(filedIn), unit.Kind is OrgUnitKind.Department or OrgUnitKind.Office ? Of(ownedDocuments) : 0);

            var blockers = new List<string>();
            if (children > 0) blockers.Add(unit.Kind == OrgUnitKind.College ? Plural(children, "department") : Plural(children, "program"));
            if (people > 0) blockers.Add(unit.Kind == OrgUnitKind.Office ? Plural(people, "staff member") : Plural(people, "faculty member"));
            if (courses > 0) blockers.Add(Plural(courses, "course"));
            if (submissions > 0) blockers.Add(Plural(submissions, "submission"));
            if (documents > 0) blockers.Add(Plural(documents, "document"));
            if (Of(checkedAgainst) > 0) blockers.Add($"a required-documents list used by {Plural(Of(checkedAgainst), "submission")}");

            if (children + people + courses + submissions + documents + Of(listsOf) > 0 || blockers.Count > 0)
                result[unit.Id] = new OrgUnitUsageDto(children, people, courses, submissions, documents, Of(listsOf), blockers);
        }
        return result;
    }

    private static async Task<Dictionary<Guid, int>> CountAsync(IQueryable<Guid> ids, CancellationToken ct) =>
        await ids.GroupBy(id => id).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    private static string Plural(int n, string one) => $"{n:N0} {(n == 1 ? one : one + "s")}";

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";

    // ---- validation

    /// <summary>Init properties, not a positional record: EF can filter on a member-init projection.</summary>
    private sealed record Coded
    {
        public Guid Id { get; init; }
        public required string Code { get; init; }
        public required string Name { get; init; }
    }

    /// <summary>Codes are unique per kind of unit, ignoring case.</summary>
    private static async Task UniqueCodeAsync(IQueryable<Coded> units, string code, Guid? except, CancellationToken ct)
    {
        var lower = code.ToLowerInvariant();
        var taken = await units.Where(u => u.Code.ToLower() == lower && u.Id != except).FirstOrDefaultAsync(ct);
        if (taken is not null) throw new ValidationAppException("code", $"{taken.Code} is already used by {taken.Name}.");
    }

    private async Task<Guid?> CollegeForAsync(DepartmentKind kind, Guid? collegeId, CancellationToken ct)
    {
        if (kind == DepartmentKind.NonAcademic)
            return collegeId is null ? null : throw new ValidationAppException("collegeId", "Non-academic departments don't belong to a college.");
        if (collegeId is not { } id) throw new ValidationAppException("collegeId", "Pick the college the department belongs to.");
        if (!await db.Colleges.AnyAsync(c => c.Id == id && c.IsActive, ct)) throw new ValidationAppException("collegeId", "Pick a college.");
        return id;
    }

    private async Task RequireAcademicDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var kind = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId && d.IsActive).Select(d => (DepartmentKind?)d.Kind)
            .SingleOrDefaultAsync(ct);
        if (kind is null) throw new ValidationAppException("departmentId", "Pick a department.");
        if (kind == DepartmentKind.NonAcademic)
            throw new ValidationAppException("departmentId", "Programs belong to academic departments; non-academic ones have staff instead.");
    }

    private static string Code(string? value)
    {
        var code = value?.Trim() ?? "";
        if (code.Length == 0) throw new ValidationAppException("code", "Give it a code, e.g. CCS.");
        if (code.Length > 20) throw new ValidationAppException("code", "Codes are at most 20 characters.");
        if (!CodeFormat().IsMatch(code)) throw new ValidationAppException("code", "Use letters and digits (spaces, dots, dashes and & are fine too).");
        return code;
    }

    private static string Name(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0) throw new ValidationAppException("name", "Give it a name.");
        return name.Length <= 200 ? name : throw new ValidationAppException("name", "Names are at most 200 characters.");
    }

    private static string? ShortName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0) return null;
        return name.Length <= 120 ? name : throw new ValidationAppException("shortName", "Short names are at most 120 characters.");
    }
}
