using KnowledgeBase.Core.Domain.Access;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>
/// Pure inheritance math for FolderEffectiveAccess (frame 14). Grants flow down the tree; a folder
/// that stops inheriting starts from its own grants only. There are no deny rules, so the effective
/// level of a principal is the highest level that reaches the folder.
/// </summary>
public static class EffectiveAccessCalculator
{
    public sealed record Node(Guid Id, Guid? ParentId, string Path, bool InheritPermissions);

    public sealed record Grant(Guid FolderId, string PrincipalKey, AccessLevel Level);

    public sealed record Effective(AccessLevel Level, Guid SourceFolderId);

    /// <param name="rootId">The top of the subtree being recomputed.</param>
    /// <param name="nodes">The root and all its descendants.</param>
    /// <param name="grants">Unexpired folder grants on those nodes.</param>
    /// <param name="inheritedAtRoot">The root's parent's effective access (empty when the root doesn't inherit).</param>
    public static Dictionary<Guid, Dictionary<string, Effective>> Compute(
        Guid rootId,
        IReadOnlyCollection<Node> nodes,
        IEnumerable<Grant> grants,
        IReadOnlyDictionary<string, Effective> inheritedAtRoot)
    {
        var grantsByFolder = grants.GroupBy(g => g.FolderId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<Guid, Dictionary<string, Effective>>(nodes.Count);

        // Parents before children: a shorter materialized path is always higher in the tree.
        foreach (var node in nodes.OrderBy(n => n.Path.Length))
        {
            Dictionary<string, Effective> access;
            if (!node.InheritPermissions)
                access = [];
            else if (node.Id == rootId)
                access = new Dictionary<string, Effective>(inheritedAtRoot);
            else if (node.ParentId is { } parentId && result.TryGetValue(parentId, out var parent))
                access = new Dictionary<string, Effective>(parent);
            else
                access = [];

            // A folder's own grant wins ties, so the access screen shows it as set here.
            foreach (var grant in grantsByFolder.GetValueOrDefault(node.Id) ?? [])
            {
                if (grant.Level == AccessLevel.None) continue;
                if (!access.TryGetValue(grant.PrincipalKey, out var current) || grant.Level >= current.Level)
                    access[grant.PrincipalKey] = new Effective(grant.Level, node.Id);
            }
            result[node.Id] = access;
        }
        return result;
    }
}
