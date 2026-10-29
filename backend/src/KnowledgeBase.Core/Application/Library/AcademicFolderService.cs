using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>
/// The system folder trees generated from the org tree (frames 21–22, 24):
/// Colleges / College / Department / Program / Faculty / AY / Term and
/// Non-academic departments / Office / Staff / AY / Term. Grants follow <see cref="SystemFolderGrants"/>.
/// </summary>
public sealed class AcademicFolderService(KbDbContext db, IAccessEvaluator access, ILogger<AcademicFolderService> log)
    : IAcademicFolderService
{
    public const string AcademicRootName = "Colleges";
    public const string OfficeRootName = "Non-academic departments";

    // ---- Sync -----------------------------------------------------------------------------

    public Task<OrgFolderSyncResult> SyncAsync(CancellationToken ct = default) =>
        DbTransactions.RunAsync(db, async () =>
        {
            var run = new SyncRun(db, await db.Folders.Where(f => f.IsSystem).ToListAsync(ct));

            var colleges = await db.Colleges.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(ct);
            var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Code).ToListAsync(ct);
            var programs = await db.Programs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
            // A program's academic supervisor and a department's head can submit for their unit too
            // (SubmissionService.ResolveUnit), so they get a person folder beside its faculty or staff.
            var faculty = Members(
                await db.FacultyPrograms.AsNoTracking()
                    .Where(fp => fp.User.IsActive)
                    .Select(fp => new UnitMember(fp.ProgramId, fp.UserId, fp.User.DisplayName))
                    .ToListAsync(ct),
                await HoldersAsync(Role.AcademicSupervisor, RoleScope.Program));
            var staff = Members(
                await db.DepartmentStaff.AsNoTracking()
                    .Where(s => s.User.IsActive)
                    .Select(s => new UnitMember(s.DepartmentId, s.UserId, s.User.DisplayName))
                    .ToListAsync(ct),
                await HoldersAsync(Role.DepartmentHead, RoleScope.Department));

            Task<List<UnitMember>> HoldersAsync(Role role, RoleScope scope) => db.RoleAssignments.AsNoTracking()
                .Where(r => r.Role == role && r.ScopeType == scope && r.IsActive && r.ScopeId != null && r.User.IsActive)
                .Select(r => new UnitMember(r.ScopeId!.Value, r.UserId, r.User.DisplayName))
                .ToListAsync(ct);
            static List<UnitMember> Members(List<UnitMember> members, List<UnitMember> holders) =>
                members.Concat(holders).DistinctBy(m => (m.UnitId, m.UserId)).OrderBy(m => m.DisplayName).ToList();

            var academicRoot = await run.EnsureAsync(null, FolderKind.AcademicRoot, null, AcademicRootName, 0, ct: ct);
            var officeRoot = await run.EnsureAsync(null, FolderKind.OfficeRoot, null, OfficeRootName, 1, ct: ct);

            var order = 0;
            foreach (var college in colleges)
            {
                var cf = await run.EnsureAsync(academicRoot, FolderKind.College, college.Id, college.Name, order++, ct: ct);
                run.Want(cf, SystemFolderGrants.College(college.Id));

                foreach (var dept in departments.Where(d => d.Kind == DepartmentKind.Academic && d.CollegeId == college.Id))
                {
                    var df = await run.EnsureAsync(cf, FolderKind.Department, dept.Id, dept.ShortName ?? dept.Name, order++, dept.Id, ct: ct);
                    run.Want(df, SystemFolderGrants.Department(dept.Id));

                    foreach (var program in programs.Where(p => p.DepartmentId == dept.Id))
                    {
                        var pf = await run.EnsureAsync(df, FolderKind.Program, program.Id, program.Name, order++, dept.Id, ct: ct);
                        run.Want(pf, SystemFolderGrants.Program(program.Id));

                        foreach (var member in faculty.Where(f => f.UnitId == program.Id))
                        {
                            var ff = await run.EnsureAsync(pf, FolderKind.Faculty, member.UserId, member.DisplayName, order++, dept.Id, member.UserId, ct);
                            run.Want(ff, SystemFolderGrants.Faculty(member.UserId, program.Id, college.Id));
                        }
                    }
                }
            }

            foreach (var dept in departments.Where(d => d.Kind == DepartmentKind.NonAcademic))
            {
                var of = await run.EnsureAsync(officeRoot, FolderKind.Office, dept.Id, dept.Name, order++, dept.Id, ct: ct);
                run.Want(of, SystemFolderGrants.Office(dept.Id));

                foreach (var member in staff.Where(s => s.UnitId == dept.Id))
                {
                    var sf = await run.EnsureAsync(of, FolderKind.Staff, member.UserId, member.DisplayName, order++, dept.Id, member.UserId, ct);
                    run.Want(sf, SystemFolderGrants.Staff(member.UserId, dept.Id));
                }
            }

            await db.SaveChangesAsync(ct);
            var grantsWritten = await run.WriteGrantsAsync(ct);
            await db.SaveChangesAsync(ct);

            await access.RecomputeSubtreeAsync(academicRoot.Id, ct);
            await access.RecomputeSubtreeAsync(officeRoot.Id, ct);

            log.LogInformation("Org folder sync: {Created} created, {Updated} updated, {Grants} grants written",
                run.Created, run.Updated, grantsWritten);
            return new OrgFolderSyncResult(run.Created, run.Updated, grantsWritten);
        }, ct);

    /// <summary>Someone with a person folder in a program or a non-academic department.</summary>
    private sealed record UnitMember(Guid UnitId, Guid UserId, string DisplayName);

    /// <summary>One sync pass: finds or creates each system folder and collects its desired grants.</summary>
    private sealed class SyncRun(KbDbContext db, List<Folder> systemFolders)
    {
        private readonly Dictionary<Guid, IReadOnlyList<SystemFolderGrants.Desired>> _wanted = [];
        public int Created { get; private set; }
        public int Updated { get; private set; }

        public void Want(Folder folder, IReadOnlyList<SystemFolderGrants.Desired> grants) => _wanted[folder.Id] = grants;

        public async Task<Folder> EnsureAsync(Folder? parent, FolderKind kind, Guid? orgRefId, string name, int sortOrder,
            Guid? departmentId = null, Guid? ownerUserId = null, CancellationToken ct = default)
        {
            // Unit folders are unique per unit; person folders per (parent, person); roots per kind.
            var folder = kind switch
            {
                FolderKind.AcademicRoot or FolderKind.OfficeRoot => systemFolders.FirstOrDefault(f => f.Kind == kind && f.ParentId == null),
                FolderKind.Faculty or FolderKind.Staff => systemFolders.FirstOrDefault(f => f.Kind == kind && f.OrgRefId == orgRefId && f.ParentId == parent?.Id),
                _ => systemFolders.FirstOrDefault(f => f.Kind == kind && f.OrgRefId == orgRefId),
            };

            if (folder is null)
            {
                folder = new Folder
                {
                    ParentId = parent?.Id,
                    Name = name,
                    Kind = kind,
                    OrgRefId = orgRefId,
                    IsSystem = true,
                    InheritPermissions = SystemFolderGrants.Inherits(kind),
                    DepartmentId = departmentId,
                    OwnerUserId = ownerUserId,
                    SortOrder = sortOrder,
                };
                folder.Path = $"{parent?.Path ?? "/"}{folder.Id}/";
                db.Folders.Add(folder);
                systemFolders.Add(folder);
                Created++;
                return folder;
            }

            var changed = false;
            if (folder.ParentId != parent?.Id && parent is not null)
            {
                await MoveAsync(folder, parent, ct);
                changed = true;
            }
            if (folder.Name != name) { folder.Name = name; changed = true; }
            if (folder.SortOrder != sortOrder) { folder.SortOrder = sortOrder; changed = true; }
            if (folder.DepartmentId != departmentId) { folder.DepartmentId = departmentId; changed = true; }
            if (folder.OwnerUserId != ownerUserId) { folder.OwnerUserId = ownerUserId; changed = true; }
            if (folder.InheritPermissions != SystemFolderGrants.Inherits(kind)) { folder.InheritPermissions = SystemFolderGrants.Inherits(kind); changed = true; }
            if (changed) Updated++;
            return folder;
        }

        /// <summary>Re-parents a folder (its unit moved) and rewrites the paths below it.</summary>
        private async Task MoveAsync(Folder folder, Folder parent, CancellationToken ct)
        {
            var oldPrefix = folder.Path;
            var newPrefix = $"{parent.Path}{folder.Id}/";
            var subtree = await db.Folders.Where(f => f.Path.StartsWith(oldPrefix)).ToListAsync(ct);
            foreach (var f in subtree.Append(folder).Distinct())
                f.Path = newPrefix + f.Path[oldPrefix.Length..];
            folder.ParentId = parent.Id;
        }

        /// <summary>Adds missing system grants, fixes changed ones and drops ones the org no longer implies.</summary>
        public async Task<int> WriteGrantsAsync(CancellationToken ct)
        {
            var folderIds = _wanted.Keys.ToArray();
            var existing = await db.DocumentPermissions
                .Where(p => p.ResourceType == ResourceType.Folder && folderIds.Contains(p.ResourceId))
                .ToListAsync(ct);
            var written = 0;

            foreach (var (folderId, grants) in _wanted)
            {
                var onFolder = existing.Where(p => p.ResourceId == folderId).ToList();
                foreach (var want in grants)
                {
                    var grant = onFolder.FirstOrDefault(p => p.PrincipalKey == want.Key);
                    if (grant is null)
                    {
                        db.DocumentPermissions.Add(new DocumentPermission
                        {
                            ResourceType = ResourceType.Folder,
                            ResourceId = folderId,
                            PrincipalType = want.Type,
                            PrincipalId = want.PrincipalId,
                            PrincipalKey = want.Key,
                            Level = want.Level,
                            IsSystem = true,
                        });
                        written++;
                    }
                    else if (!grant.IsSystem || grant.Level != want.Level || grant.ExpiresAt is not null)
                    {
                        // A hand-made grant for the same principal becomes the system one.
                        grant.IsSystem = true;
                        grant.Level = want.Level;
                        grant.ExpiresAt = null;
                        grant.PrincipalType = want.Type;
                        grant.PrincipalId = want.PrincipalId;
                        written++;
                    }
                }
                var wantedKeys = grants.Select(g => g.Key).ToHashSet();
                db.DocumentPermissions.RemoveRange(onFolder.Where(p => p.IsSystem && !wantedKeys.Contains(p.PrincipalKey)));
            }
            return written;
        }
    }

    // ---- Auto-filing ----------------------------------------------------------------------

    public async Task<Guid> ResolveSubmissionFolderAsync(SubmissionFolderRequest request, CancellationToken ct = default)
    {
        var tag = await db.DocumentTypes.AsNoTracking()
            .Where(t => t.Id == request.DocumentTypeId)
            .Select(t => (AcademicTag?)t.AcademicTag)
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Document type", request.DocumentTypeId);

        var term = await db.AcademicTerms.AsNoTracking()
            .Where(t => t.Id == request.AcademicTermId)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.SortOrder,
                t.AcademicYearId,
                YearName = t.AcademicYear.Name,
                YearStarts = t.AcademicYear.StartsOn,
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Academic term", request.AcademicTermId);

        var person = await FindPersonFolderAsync(request, ct);
        if (person is null)
        {
            // The org changed since the last sync (e.g. a new faculty member): build the folders now.
            await SyncAsync(ct);
            person = await FindPersonFolderAsync(request, ct)
                ?? throw new BusinessRuleException("no-personal-folder", "The submitter has no faculty or staff folder.",
                    "Add them to a program or a non-academic department first.");
        }

        if (tag == AcademicTag.None) return person.Id;
        var year = await EnsureChildAsync(person, FolderKind.AcademicYear, term.AcademicYearId, term.YearName, term.YearStarts.Year, ct);
        if (tag == AcademicTag.Year) return year.Id;
        var termFolder = await EnsureChildAsync(year, FolderKind.Term, term.Id, term.Name, term.SortOrder, ct);
        if (request.CourseId is not { } courseId) return termFolder.Id;

        var course = await db.Courses.AsNoTracking().Where(c => c.Id == courseId)
            .Select(c => new { c.Code, c.Title })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Course", courseId);
        return (await EnsureChildAsync(termFolder, FolderKind.Course, courseId, CourseFolderName(course.Code, course.Title), 0, ct)).Id;
    }

    /// <summary>"CS 301 · Data Structures", cut to fit a folder name.</summary>
    public static string CourseFolderName(string code, string title)
    {
        var name = $"{code} · {title}";
        return name.Length <= 200 ? name : name[..199] + "…";
    }

    private async Task<Folder?> FindPersonFolderAsync(SubmissionFolderRequest request, CancellationToken ct)
    {
        var userId = request.SubmitterUserId;
        if (request.ProgramId is { } programId)
        {
            var program = await FindSystemAsync(f => f.Kind == FolderKind.Program && f.OrgRefId == programId, ct);
            return program is null ? null
                : await FindSystemAsync(f => f.ParentId == program.Id && f.Kind == FolderKind.Faculty && f.OrgRefId == userId, ct);
        }

        if (request.DepartmentId is { } departmentId)
        {
            var kind = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId)
                .Select(d => (DepartmentKind?)d.Kind).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Department", departmentId);

            if (kind == DepartmentKind.NonAcademic)
            {
                var office = await FindSystemAsync(f => f.Kind == FolderKind.Office && f.OrgRefId == departmentId, ct);
                return office is null ? null
                    : await FindSystemAsync(f => f.ParentId == office.Id && f.Kind == FolderKind.Staff && f.OrgRefId == userId, ct);
            }

            // A department-level requirement for faculty: their folder in that department, primary program first.
            var primary = await db.FacultyPrograms.AsNoTracking()
                .Where(fp => fp.UserId == userId && fp.Program.DepartmentId == departmentId)
                .OrderByDescending(fp => fp.IsPrimary)
                .Select(fp => (Guid?)fp.ProgramId)
                .FirstOrDefaultAsync(ct);
            return primary is null ? null : await FindPersonFolderAsync(request with { ProgramId = primary, DepartmentId = null }, ct);
        }

        return await FindSystemAsync(f => (f.Kind == FolderKind.Faculty || f.Kind == FolderKind.Staff) && f.OrgRefId == userId, ct);
    }

    /// <summary>Looks in the unit of work first, so folders created earlier in the same request are reused.</summary>
    private async Task<Folder?> FindSystemAsync(System.Linq.Expressions.Expression<Func<Folder, bool>> match, CancellationToken ct)
    {
        var compiled = match.Compile();
        return db.Folders.Local.FirstOrDefault(f => f.IsSystem && compiled(f))
            ?? await db.Folders.Where(f => f.IsSystem).Where(match).OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Finds or creates a year/term folder under a person folder. A new folder inherits its parent,
    /// so its effective access is a copy of the parent's; both are added to the unit of work unsaved.
    /// </summary>
    private async Task<Folder> EnsureChildAsync(Folder parent, FolderKind kind, Guid orgRefId, string name, int sortOrder, CancellationToken ct)
    {
        var existing = await FindSystemAsync(f => f.ParentId == parent.Id && f.Kind == kind && f.OrgRefId == orgRefId, ct);
        if (existing is not null) return existing;

        var folder = new Folder
        {
            ParentId = parent.Id,
            Name = name,
            Kind = kind,
            OrgRefId = orgRefId,
            IsSystem = true,
            InheritPermissions = true,
            DepartmentId = parent.DepartmentId,
            OwnerUserId = parent.OwnerUserId,
            SortOrder = sortOrder,
        };
        folder.Path = $"{parent.Path}{folder.Id}/";
        db.Folders.Add(folder);

        var parentRows = db.FolderEffectiveAccess.Local.Where(a => a.FolderId == parent.Id).ToList();
        if (parentRows.Count == 0)
            parentRows = await db.FolderEffectiveAccess.AsNoTracking().Where(a => a.FolderId == parent.Id).ToListAsync(ct);
        db.FolderEffectiveAccess.AddRange(parentRows.Select(a => new FolderEffectiveAccess
        {
            FolderId = folder.Id,
            PrincipalKey = a.PrincipalKey,
            Level = a.Level,
            SourceFolderId = a.SourceFolderId,
        }));
        return folder;
    }
}
