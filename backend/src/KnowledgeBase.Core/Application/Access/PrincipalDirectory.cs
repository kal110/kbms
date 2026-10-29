using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>Turns principal keys into names, checks that a principal exists, and searches principals.</summary>
public sealed class PrincipalDirectory(KbDbContext db)
{
    private static readonly Dictionary<string, PrincipalType> Prefixes = new()
    {
        ["user"] = PrincipalType.User,
        ["group"] = PrincipalType.Group,
        ["college"] = PrincipalType.College,
        ["dept"] = PrincipalType.Department,
        ["prog"] = PrincipalType.Program,
        ["dean"] = PrincipalType.CollegeDean,
        ["as"] = PrincipalType.ProgramSupervisor,
        ["head"] = PrincipalType.DepartmentHead,
    };

    public static bool TryParse(string key, out PrincipalType type, out Guid id)
    {
        type = default;
        id = default;
        if (key == PrincipalKeys.Vpaf)
        {
            type = PrincipalType.Vpaf;
            return true;
        }
        var colon = key.IndexOf(':');
        return colon > 0
            && Prefixes.TryGetValue(key[..colon], out type)
            && Guid.TryParse(key[(colon + 1)..], out id);
    }

    public async Task<IReadOnlyDictionary<string, PrincipalDto>> DescribeAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var parsed = keys.Distinct()
            .Select(k => TryParse(k, out var t, out var id) ? (Key: k, Type: t, Id: id) : (Key: k, Type: (PrincipalType?)null, Id: Guid.Empty))
            .ToList();
        Guid[] Ids(params PrincipalType[] types) => parsed.Where(p => p.Type is { } t && types.Contains(t)).Select(p => p.Id).Distinct().ToArray();

        var userIds = Ids(PrincipalType.User);
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.DisplayName, u.Title), ct);

        var groupIds = Ids(PrincipalType.Group);
        // Count in SQL: ToDictionaryAsync's selector runs in memory, where Members isn't loaded (always 0).
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name, Members = g.Members.Count })
            .ToDictionaryAsync(g => g.Id, g => (g.Name, g.Members), ct);

        var collegeIds = Ids(PrincipalType.College, PrincipalType.CollegeDean);
        var colleges = await db.Colleges.AsNoTracking().Where(c => collegeIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => (c.Code, c.Name), ct);

        var deptIds = Ids(PrincipalType.Department, PrincipalType.DepartmentHead);
        var departments = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => (d.Name, Short: d.ShortName ?? d.Name, d.Kind), ct);

        var programIds = Ids(PrincipalType.Program, PrincipalType.ProgramSupervisor);
        var programs = await db.Programs.AsNoTracking().Where(p => programIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => (p.Code, p.Name), ct);

        // Who currently holds each scoped role, to show "Dean · Dr. Elena Navarro".
        var scopeIds = collegeIds.Concat(deptIds).Concat(programIds).ToArray();
        var holders = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.IsActive && r.ScopeId != null && scopeIds.Contains(r.ScopeId.Value) && r.Role != Role.SystemAdmin)
            .Select(r => new { r.Role, ScopeId = r.ScopeId!.Value, r.Title, r.User.DisplayName })
            .ToListAsync(ct);
        string? Holder(Role role, Guid scope) => holders.FirstOrDefault(h => h.Role == role && h.ScopeId == scope)?.DisplayName;
        string? HolderTitle(Role role, Guid scope) => holders.FirstOrDefault(h => h.Role == role && h.ScopeId == scope)?.Title;

        var result = new Dictionary<string, PrincipalDto>();
        foreach (var (key, type, id) in parsed)
        {
            if (type is not { } t)
            {
                result[key] = new PrincipalDto(PrincipalType.User, Guid.Empty, key, key, "Unknown principal");
                continue;
            }
            var (name, detail) = t switch
            {
                PrincipalType.User when users.TryGetValue(id, out var u) => (u.DisplayName, $"Person · {u.Title ?? "Member"}"),
                PrincipalType.Group when groups.TryGetValue(id, out var g) => (g.Name, $"Group · {g.Members} {(g.Members == 1 ? "person" : "people")}"),
                PrincipalType.College when colleges.TryGetValue(id, out var c) => (c.Name, "College · everyone in it"),
                PrincipalType.Department when departments.TryGetValue(id, out var d) =>
                    (d.Name, d.Kind == DepartmentKind.NonAcademic ? "Non-academic department · everyone in it" : "Department · everyone in it"),
                PrincipalType.Program when programs.TryGetValue(id, out var p) => (p.Name, "Program · its faculty"),
                PrincipalType.CollegeDean when colleges.TryGetValue(id, out var c) =>
                    ($"Dean, {c.Code}", Holder(Role.Dean, id) is { } who ? $"College Dean · {who}" : "College Dean · vacant"),
                PrincipalType.ProgramSupervisor when programs.TryGetValue(id, out var p) =>
                    ($"Academic Supervisor, {p.Code}", Holder(Role.AcademicSupervisor, id) is { } who ? $"Academic Supervisor · {who}" : "Academic Supervisor · vacant"),
                PrincipalType.DepartmentHead when departments.TryGetValue(id, out var d) =>
                    ($"{HolderTitle(Role.DepartmentHead, id) ?? "Head"}, {d.Short}",
                        Holder(Role.DepartmentHead, id) is { } who ? $"Department head · {who}" : "Department head · vacant"),
                PrincipalType.Vpaf => ("VP for Academic Affairs", "University-wide · whoever holds the VPAF role"),
                _ => (key, "Removed or unknown"),
            };
            result[key] = new PrincipalDto(t, id, key, name, detail);
        }
        return result;
    }

    public async Task<PrincipalDto> DescribeAsync(string key, CancellationToken ct = default) =>
        (await DescribeAsync([key], ct))[key];

    /// <summary>Validates that the principal exists (400 otherwise) and returns it described.</summary>
    public async Task<PrincipalDto> RequireAsync(PrincipalType type, Guid id, CancellationToken ct = default)
    {
        var exists = type switch
        {
            PrincipalType.User => await db.Users.AnyAsync(u => u.Id == id && u.IsActive, ct),
            PrincipalType.Group => await db.Groups.AnyAsync(g => g.Id == id, ct),
            PrincipalType.College or PrincipalType.CollegeDean => await db.Colleges.AnyAsync(c => c.Id == id, ct),
            PrincipalType.Department => await db.Departments.AnyAsync(d => d.Id == id, ct),
            PrincipalType.DepartmentHead => await db.Departments.AnyAsync(d => d.Id == id && d.Kind == DepartmentKind.NonAcademic, ct),
            PrincipalType.Program or PrincipalType.ProgramSupervisor => await db.Programs.AnyAsync(p => p.Id == id, ct),
            PrincipalType.Vpaf => id == Guid.Empty,
            _ => false,
        };
        if (!exists) throw new ValidationAppException("principalId", "That person, group or unit doesn't exist.");
        return await DescribeAsync(PrincipalKeys.For(type, id), ct);
    }

    /// <summary>People, groups, units and unit roles matching a search, for the grant picker.</summary>
    public async Task<IReadOnlyList<PrincipalDto>> SearchAsync(string? q, int limit = 20, CancellationToken ct = default)
    {
        var term = (q ?? "").Trim();
        var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        var keys = new List<string>();

        keys.AddRange((await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (term == "" || EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.ExternalId, pattern)))
            .OrderBy(u => u.DisplayName).Take(limit).Select(u => u.Id).ToListAsync(ct)).Select(PrincipalKeys.User));
        keys.AddRange((await db.Groups.AsNoTracking()
            .Where(g => term == "" || EF.Functions.ILike(g.Name, pattern))
            .OrderBy(g => g.Name).Take(limit).Select(g => g.Id).ToListAsync(ct)).Select(PrincipalKeys.Group));

        var colleges = await db.Colleges.AsNoTracking()
            .Where(c => c.IsActive && (term == "" || EF.Functions.ILike(c.Name, pattern) || EF.Functions.ILike(c.Code, pattern)))
            .OrderBy(c => c.Code).Take(limit).Select(c => c.Id).ToListAsync(ct);
        keys.AddRange(colleges.Select(PrincipalKeys.College));
        keys.AddRange(colleges.Select(PrincipalKeys.Dean));

        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.IsActive && (term == "" || EF.Functions.ILike(d.Name, pattern) || EF.Functions.ILike(d.Code, pattern)))
            .OrderBy(d => d.Code).Take(limit).Select(d => new { d.Id, d.Kind }).ToListAsync(ct);
        keys.AddRange(departments.Select(d => PrincipalKeys.Department(d.Id)));
        keys.AddRange(departments.Where(d => d.Kind == DepartmentKind.NonAcademic).Select(d => PrincipalKeys.Head(d.Id)));

        var programs = await db.Programs.AsNoTracking()
            .Where(p => p.IsActive && (term == "" || EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Code, pattern)))
            .OrderBy(p => p.Code).Take(limit).Select(p => p.Id).ToListAsync(ct);
        keys.AddRange(programs.Select(PrincipalKeys.Program));
        keys.AddRange(programs.Select(PrincipalKeys.Supervisor));
        if (term == "" || "VPAF · VP for Academic Affairs".Contains(term, StringComparison.OrdinalIgnoreCase)) keys.Add(PrincipalKeys.Vpaf);

        var described = await DescribeAsync(keys, ct);
        return keys.Select(k => described[k]).Take(limit * 2).ToList();
    }
}
