using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>
/// Folder navigation and manual folder management (frames 1, 21–22). A folder is listed when the
/// caller can view it, or as a path-only step towards one they can view; admins see every folder
/// (to repair access) but only the documents their own grants allow.
/// </summary>
public sealed class FolderService(
    KbDbContext db,
    ICurrentUser currentUser,
    IAccessEvaluator access,
    IAcademicFolderService academic,
    IAuditWriter audit,
    TimeProvider clock)
{
    /// <summary>System folders whose children are generated, so nobody adds folders by hand.</summary>
    private static readonly FolderKind[] GeneratedChildren =
        [FolderKind.AcademicRoot, FolderKind.OfficeRoot, FolderKind.Faculty, FolderKind.Staff, FolderKind.AcademicYear, FolderKind.Term, FolderKind.Course];

    private sealed record FolderInfo(
        Guid Id, Guid? ParentId, string Name, string Path, FolderKind Kind, bool IsSystem, bool IsRestricted,
        bool InheritPermissions, Guid? OrgRefId, Guid? OwnerUserId, Guid? DepartmentId, int SortOrder);

    private static IQueryable<FolderInfo> Infos(IQueryable<Folder> folders) => folders.AsNoTracking().Select(f =>
        new FolderInfo(f.Id, f.ParentId, f.Name, f.Path, f.Kind, f.IsSystem, f.IsRestricted, f.InheritPermissions,
            f.OrgRefId, f.OwnerUserId, f.DepartmentId, f.SortOrder));

    // ---- Reading --------------------------------------------------------------------------

    /// <param name="root">"academic", "offices", "library" (hand-made folders), or null for all.</param>
    public async Task<IReadOnlyList<FolderNode>> GetTreeAsync(string? root, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var scope = await ScopeAsync(root, ct);
        var folders = await Infos(scope).ToListAsync(ct);
        return await BuildNodesAsync(me, folders, folders, ct);
    }

    public async Task<FolderDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var (folder, level) = await RequireNavigableAsync(me, id, ct);
        var node = (await BuildNodesAsync(me, [folder], await ChildrenOfAsync(id, ct), ct)).Single();

        var crumbIds = PathIds(folder.Path);
        var crumbs = await db.Folders.AsNoTracking().Where(f => crumbIds.Contains(f.Id))
            .Select(f => new FolderRef(f.Id, f.Name, f.Kind)).ToListAsync(ct);
        var ordered = crumbIds.Select(cid => crumbs.First(c => c.Id == cid)).ToList();

        return new FolderDetail(node, ordered, await OrgRefAsync(folder, ct), await ContextAsync(folder, ct),
            AllowedActions(folder, level, me.IsAdmin));
    }

    public async Task<FolderContents> GetChildrenAsync(Guid id, DocumentListQuery query, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var (folder, _) = await RequireNavigableAsync(me, id, ct);
        var children = await ChildrenOfAsync(id, ct);
        var nodes = await BuildNodesAsync(me, [folder, .. children], children, ct);

        var docs = access.VisibleDocuments(me).AsNoTracking();
        docs = query.IncludeSubfolders
            ? docs.Where(d => d.Folder.Path.StartsWith(folder.Path))
            : docs.Where(d => d.FolderId == id);
        var page = await ListAsync(docs, query, ct);

        return new FolderContents(nodes[0], nodes.Skip(1).ToList(), page);
    }

    /// <summary>The caller's own faculty and staff folders (one per program or office).</summary>
    public async Task<IReadOnlyList<FolderNode>> GetMineAsync(CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var mine = await Infos(db.Folders.Where(f => f.IsSystem && f.OwnerUserId == me.UserId
            && (f.Kind == FolderKind.Faculty || f.Kind == FolderKind.Staff))).ToListAsync(ct);
        var primary = me.Programs.Where(p => p.IsPrimary).Select(p => p.ProgramId).ToHashSet();
        var parents = mine.Select(f => f.ParentId).OfType<Guid>().ToArray();
        var parentRefs = await db.Folders.AsNoTracking().Where(f => parents.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.OrgRefId, ct);
        var ordered = mine
            .OrderByDescending(f => f.ParentId is { } p && parentRefs.GetValueOrDefault(p) is { } prog && primary.Contains(prog))
            .ThenBy(f => f.SortOrder)
            .ToList();
        return await BuildNodesAsync(me, ordered, [], ct);
    }

    /// <summary>A page of visible documents, filtered and sorted for a folder or search list.</summary>
    public async Task<PagedResult<DocumentListItem>> ListAsync(IQueryable<Document> docs, DocumentListQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{query.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            docs = docs.Where(d => EF.Functions.ILike(d.Title, pattern) || EF.Functions.ILike(d.DocumentNumber ?? "", pattern));
        }
        if (query.TypeId is { } typeId) docs = docs.Where(d => d.DocumentTypeId == typeId);
        if (query.YearId is { } yearId) docs = docs.Where(d => d.AcademicYearId == yearId);
        if (query.TermId is { } termId) docs = docs.Where(d => d.AcademicTermId == termId);
        if (query.State is { } state) docs = docs.WhereState(db, state);

        docs = query.Sort?.ToLowerInvariant() switch
        {
            "title" => docs.OrderBy(d => d.Title).ThenBy(d => d.Id),
            "number" => docs.OrderBy(d => d.DocumentNumber).ThenBy(d => d.Id),
            "type" => docs.OrderBy(d => d.DocumentType.Code).ThenBy(d => d.Title),
            "oldest" => docs.OrderBy(d => d.UpdatedAt).ThenBy(d => d.Id),
            _ => docs.OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id),
        };

        var page = new PageRequest(query.Page, query.PageSize);
        var rows = await docs.ToRows(db).ToPagedAsync(page, ct);
        return new PagedResult<DocumentListItem>(rows.Items.Select(r => r.ToItem()).ToList(), rows.Page, rows.PageSize, rows.Total);
    }

    // ---- Manual folders -------------------------------------------------------------------

    public async Task<FolderDetail> CreateAsync(CreateFolderRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var name = ValidName(request.Name);

        Folder? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await db.Folders.AsNoTracking().SingleOrDefaultAsync(f => f.Id == parentId, ct)
                ?? throw new NotFoundException("Folder", parentId);
            var level = await access.GetFolderAccessAsync(me, parentId, ct);
            if (!me.IsAdmin) level.Require(AccessLevel.Edit, "Folder", parentId);
            if (GeneratedChildren.Contains(parent.Kind))
                throw new ConflictException("system-folder", "Folders here are generated from the org tree and calendar.");
        }
        else if (!me.IsAdmin)
        {
            throw new ForbiddenException("Only a system administrator can add a top-level library folder.");
        }
        await EnsureUniqueNameAsync(request.ParentId, name, exceptId: null, ct);

        var inherit = parent is not null && (request.InheritPermissions ?? true);
        var folder = new Folder
        {
            ParentId = parent?.Id,
            Name = name,
            Kind = FolderKind.Manual,
            InheritPermissions = inherit,
            IsRestricted = request.IsRestricted ?? false,
            DepartmentId = parent?.DepartmentId,
        };
        folder.Path = $"{parent?.Path ?? "/"}{folder.Id}/";

        await DbTransactions.RunAsync(db, async () =>
        {
            db.Folders.Add(folder);
            // Someone who creates a folder that doesn't inherit keeps managing it.
            if (!inherit && !me.IsAdmin)
                db.DocumentPermissions.Add(new DocumentPermission
                {
                    ResourceType = ResourceType.Folder,
                    ResourceId = folder.Id,
                    PrincipalType = PrincipalType.User,
                    PrincipalId = me.UserId,
                    PrincipalKey = PrincipalKeys.User(me.UserId),
                    Level = AccessLevel.Manage,
                    GrantedByUserId = me.UserId,
                    GrantedAt = clock.GetUtcNow(),
                });
            audit.Record("folder.created", "folder", folder.Id, metadata: new { name, parentId = parent?.Id, inherit });
            await db.SaveChangesAsync(ct);
            await access.RecomputeSubtreeAsync(folder.Id, ct);
        }, ct);
        return await GetAsync(folder.Id, ct);
    }

    public async Task<FolderDetail> UpdateAsync(Guid id, UpdateFolderRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var folder = await db.Folders.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Folder", id);
        var level = await access.GetFolderAccessAsync(me, id, ct);
        if (!me.IsAdmin) level.Require(AccessLevel.View, "Folder", id);
        if (folder.IsSystem)
            throw new ConflictException("system-folder", "System folders follow the org tree and can't be renamed or moved.");

        var changes = new Dictionary<string, object?>();
        var moved = false;

        if (request.Name is not null && request.Name.Trim() != folder.Name)
        {
            if (!me.IsAdmin) level.Require(AccessLevel.Edit, "Folder", id);
            var name = ValidName(request.Name);
            await EnsureUniqueNameAsync(folder.ParentId, name, folder.Id, ct);
            changes["name"] = new { from = folder.Name, to = name };
            folder.Name = name;
        }

        if (request.IsRestricted is { } restricted && restricted != folder.IsRestricted)
        {
            if (!me.IsAdmin) level.Require(AccessLevel.Manage, "Folder", id);
            changes["isRestricted"] = restricted;
            folder.IsRestricted = restricted;
        }

        if (request.ParentId is { } parentId && parentId != folder.ParentId)
        {
            if (!me.IsAdmin) level.Require(AccessLevel.Manage, "Folder", id);
            var parent = await db.Folders.SingleOrDefaultAsync(f => f.Id == parentId, ct) ?? throw new NotFoundException("Folder", parentId);
            if (parent.Path.StartsWith(folder.Path, StringComparison.Ordinal))
                throw new BusinessRuleException("folder-cycle", "A folder can't be moved into itself or one of its subfolders.");
            if (GeneratedChildren.Contains(parent.Kind))
                throw new ConflictException("system-folder", "Folders there are generated from the org tree and calendar.");
            if (!me.IsAdmin) (await access.GetFolderAccessAsync(me, parentId, ct)).Require(AccessLevel.Edit, "Folder", parentId);
            await EnsureUniqueNameAsync(parentId, folder.Name, folder.Id, ct);

            var oldPrefix = folder.Path;
            var newPrefix = $"{parent.Path}{folder.Id}/";
            var subtree = await db.Folders.Where(f => f.Path.StartsWith(oldPrefix)).ToListAsync(ct);
            foreach (var f in subtree) f.Path = newPrefix + f.Path[oldPrefix.Length..];
            changes["parentId"] = new { from = folder.ParentId, to = parentId };
            folder.ParentId = parentId;
            folder.DepartmentId ??= parent.DepartmentId;
            moved = true;
        }

        if (changes.Count > 0)
        {
            audit.Record(moved ? "folder.moved" : "folder.updated", "folder", id, metadata: changes);
            await DbTransactions.RunAsync(db, async () =>
            {
                await db.SaveChangesAsync(ct);
                if (moved) await access.RecomputeSubtreeAsync(id, ct);
            }, ct);
        }
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var folder = await db.Folders.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Folder", id);
        var level = await access.GetFolderAccessAsync(me, id, ct);
        if (!me.IsAdmin) level.Require(AccessLevel.Manage, "Folder", id);
        if (folder.IsSystem)
            throw new ConflictException("system-folder", "System folders follow the org tree and can't be deleted.");
        if (await db.Folders.AnyAsync(f => f.ParentId == id, ct))
            throw new ConflictException("folder-not-empty", "Remove or move the subfolders first.");
        if (await db.Documents.AnyAsync(d => d.FolderId == id, ct))
            throw new ConflictException("folder-not-empty", "This folder still holds documents.",
                "Move them out first; deleted documents are kept until their retention ends.");

        var grants = await db.DocumentPermissions.Where(p => p.ResourceType == ResourceType.Folder && p.ResourceId == id).ToListAsync(ct);
        db.DocumentPermissions.RemoveRange(grants);
        await db.FolderEffectiveAccess.Where(a => a.FolderId == id).ExecuteDeleteAsync(ct);
        db.Folders.Remove(folder);
        audit.Record("folder.deleted", "folder", id, metadata: new { folder.Name, folder.ParentId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<OrgFolderSyncResult> SyncOrgFoldersAsync(CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        me.RequireAdmin();
        var result = await academic.SyncAsync(ct);
        audit.Record("org.folders-synced", "folder", null, metadata: result);
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ---- Helpers --------------------------------------------------------------------------

    private async Task<IQueryable<Folder>> ScopeAsync(string? root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Equals("all", StringComparison.OrdinalIgnoreCase)) return db.Folders;

        var roots = await db.Folders.AsNoTracking()
            .Where(f => f.ParentId == null && (f.Kind == FolderKind.AcademicRoot || f.Kind == FolderKind.OfficeRoot))
            .Select(f => new { f.Kind, f.Path })
            .ToListAsync(ct);
        var academicPath = roots.FirstOrDefault(r => r.Kind == FolderKind.AcademicRoot)?.Path ?? "/none/";
        var officePath = roots.FirstOrDefault(r => r.Kind == FolderKind.OfficeRoot)?.Path ?? "/none/";

        return root.ToLowerInvariant() switch
        {
            "academic" => db.Folders.Where(f => f.Path.StartsWith(academicPath)),
            "offices" or "office" or "nonacademic" => db.Folders.Where(f => f.Path.StartsWith(officePath)),
            "library" => db.Folders.Where(f => !f.Path.StartsWith(academicPath) && !f.Path.StartsWith(officePath)),
            _ => throw new ValidationAppException("root", "Use academic, offices, library or all."),
        };
    }

    private Task<List<FolderInfo>> ChildrenOfAsync(Guid id, CancellationToken ct) =>
        Infos(db.Folders.Where(f => f.ParentId == id)).ToListAsync(ct);

    /// <summary>The folder, if the caller may open it: they can view it, are an admin, or can view something below it.</summary>
    private async Task<(FolderInfo Folder, AccessLevel Level)> RequireNavigableAsync(UserContext me, Guid id, CancellationToken ct)
    {
        var folder = await Infos(db.Folders.Where(f => f.Id == id)).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Folder", id);
        var level = await access.GetFolderAccessAsync(me, id, ct);
        if (level >= AccessLevel.View || me.IsAdmin) return (folder, level);
        if (await access.VisibleFolders(me).AnyAsync(f => f.Path.StartsWith(folder.Path), ct)) return (folder, level);
        throw new NotFoundException("Folder", id);
    }

    /// <summary>
    /// Nodes for <paramref name="folders"/> with the caller's level, plus "has children" judged against
    /// <paramref name="candidates"/>. Path-only ancestors are added when listing a whole tree.
    /// </summary>
    private async Task<List<FolderNode>> BuildNodesAsync(UserContext me, IReadOnlyList<FolderInfo> folders,
        IReadOnlyList<FolderInfo> candidates, CancellationToken ct)
    {
        var keys = me.PrincipalKeys.ToArray();
        var levelRows = await db.FolderEffectiveAccess.AsNoTracking()
            .Where(a => keys.Contains(a.PrincipalKey))
            .Select(a => new { a.FolderId, a.Level })
            .ToListAsync(ct);
        var levels = levelRows.GroupBy(r => r.FolderId).ToDictionary(g => g.Key, g => AccessLevels.Max(g.Select(r => r.Level)));
        AccessLevel LevelOf(Guid id) => levels.GetValueOrDefault(id, AccessLevel.None);

        // Navigable = viewable, or an ancestor of something viewable (path only), or anything for an admin.
        var visiblePaths = await access.VisibleFolders(me).AsNoTracking().Select(f => f.Path).ToListAsync(ct);
        var ancestorIds = visiblePaths.SelectMany(PathIds).ToHashSet();
        bool Navigable(FolderInfo f) => me.IsAdmin || LevelOf(f.Id) >= AccessLevel.View || ancestorIds.Contains(f.Id);

        var all = folders.Concat(candidates).DistinctBy(f => f.Id).ToList();
        var navigableIds = all.Where(Navigable).Select(f => f.Id).ToHashSet();
        var parentsWithChildren = all.Where(f => navigableIds.Contains(f.Id) && f.ParentId is not null)
            .Select(f => f.ParentId!.Value).ToHashSet();
        // Children outside the candidate list (detail views) still count.
        var outside = ReferenceEquals(folders, candidates) ? Array.Empty<Guid>()
            : folders.Select(f => f.Id).Where(id => !parentsWithChildren.Contains(id)).ToArray();
        if (outside.Length > 0)
        {
            var childPaths = await db.Folders.AsNoTracking().Where(f => f.ParentId != null && outside.Contains(f.ParentId.Value))
                .Select(f => new { f.Id, ParentId = f.ParentId!.Value }).ToListAsync(ct);
            foreach (var c in childPaths.Where(c => me.IsAdmin || LevelOf(c.Id) >= AccessLevel.View || ancestorIds.Contains(c.Id)))
                parentsWithChildren.Add(c.ParentId);
        }

        var listed = folders.Where(f => folders.Count == 1 || Navigable(f)).ToList();
        var listedIds = listed.Select(f => f.Id).ToArray();
        var counts = await access.VisibleDocuments(me).AsNoTracking()
            .Where(d => listedIds.Contains(d.FolderId))
            .GroupBy(d => d.FolderId)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.FolderId, g => g.Count, ct);

        return listed
            .OrderBy(f => f.Path.Count(c => c == '/')).ThenBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new FolderNode(f.Id, f.ParentId, f.Name, f.Kind, f.IsSystem, f.IsRestricted, f.InheritPermissions,
                f.OrgRefId, f.OwnerUserId, f.DepartmentId, f.SortOrder, LevelOf(f.Id), LevelOf(f.Id) >= AccessLevel.View,
                parentsWithChildren.Contains(f.Id), counts.GetValueOrDefault(f.Id)))
            .ToList();
    }

    private static List<Guid> PathIds(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    private static IReadOnlyList<string> AllowedActions(FolderInfo folder, AccessLevel level, bool isAdmin)
    {
        var actions = new List<string>();
        if (level >= AccessLevel.View) actions.Add("view");
        if (level >= AccessLevel.Contribute && !folder.IsSystem) actions.Add("upload");
        if ((level >= AccessLevel.Edit || isAdmin) && !GeneratedChildren.Contains(folder.Kind)) actions.Add("createFolder");
        if (!folder.IsSystem && (level >= AccessLevel.Edit || isAdmin)) actions.Add("rename");
        if (!folder.IsSystem && (level >= AccessLevel.Manage || isAdmin)) actions.AddRange(["move", "delete"]);
        if (level >= AccessLevel.Manage || isAdmin) actions.Add("manageAccess");
        if (isAdmin && folder.IsSystem) actions.Add("syncOrg");
        return actions;
    }

    private async Task<OrgRef?> OrgRefAsync(FolderInfo folder, CancellationToken ct)
    {
        if (folder.OrgRefId is not { } id) return null;
        return folder.Kind switch
        {
            FolderKind.College => await db.Colleges.AsNoTracking().Where(c => c.Id == id)
                .Select(c => new OrgRef("college", c.Id, c.Code, c.Name)).SingleOrDefaultAsync(ct),
            FolderKind.Department or FolderKind.Office => await db.Departments.AsNoTracking().Where(d => d.Id == id)
                .Select(d => new OrgRef(d.Kind == DepartmentKind.NonAcademic ? "office" : "department", d.Id, d.Code, d.Name)).SingleOrDefaultAsync(ct),
            FolderKind.Program => await db.Programs.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new OrgRef("program", p.Id, p.Code, p.Name)).SingleOrDefaultAsync(ct),
            FolderKind.Faculty or FolderKind.Staff => await db.Users.AsNoTracking().Where(u => u.Id == id)
                .Select(u => new OrgRef("person", u.Id, u.ExternalId, u.DisplayName)).SingleOrDefaultAsync(ct),
            FolderKind.AcademicYear => await db.AcademicYears.AsNoTracking().Where(y => y.Id == id)
                .Select(y => new OrgRef("academicYear", y.Id, y.Code, y.Name)).SingleOrDefaultAsync(ct),
            FolderKind.Term => await db.AcademicTerms.AsNoTracking().Where(t => t.Id == id)
                .Select(t => new OrgRef("academicTerm", t.Id, t.Code, t.Name)).SingleOrDefaultAsync(ct),
            FolderKind.Course => await db.Courses.AsNoTracking().Where(c => c.Id == id)
                .Select(c => new OrgRef("course", c.Id, c.Code, c.Title)).SingleOrDefaultAsync(ct),
            _ => null,
        };
    }

    /// <summary>One line under the folder name, e.g. "Faculty folder · BS Computer Science".</summary>
    private async Task<string?> ContextAsync(FolderInfo folder, CancellationToken ct)
    {
        string? Parent(string? name, string label) => name is null ? label : $"{label} · {name}";
        var parentName = folder.ParentId is { } pid
            ? await db.Folders.AsNoTracking().Where(f => f.Id == pid).Select(f => f.Name).SingleOrDefaultAsync(ct)
            : null;
        return folder.Kind switch
        {
            FolderKind.Faculty => Parent(parentName, "Faculty folder"),
            FolderKind.Staff => Parent(parentName, "Staff folder"),
            FolderKind.AcademicYear or FolderKind.Term => Parent(parentName, "Filed automatically"),
            FolderKind.Course => Parent(parentName, "Course folder"),
            FolderKind.Program => Parent(parentName, "Program"),
            FolderKind.Department => Parent(parentName, "Department"),
            FolderKind.College => "College",
            FolderKind.Office => "Non-academic department",
            _ => folder.IsRestricted ? "Restricted folder" : null,
        };
    }

    private static string ValidName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationAppException("name", "Give the folder a name.");
        if (n.Length > 200) throw new ValidationAppException("name", "Keep the name under 200 characters.");
        if (n.Contains('/')) throw new ValidationAppException("name", "Folder names can't contain \"/\".");
        return n;
    }

    private async Task EnsureUniqueNameAsync(Guid? parentId, string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLower();
        var taken = await db.Folders.AnyAsync(f => f.ParentId == parentId && f.Id != exceptId && f.Name.ToLower() == lower, ct);
        if (taken) throw new ConflictException("folder-name-taken", "A folder with this name already exists here.");
    }
}
