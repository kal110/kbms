using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>
/// Level helpers. Levels are stored as text, so SQL can't compare them with &lt; or &gt;: queries
/// filter with <see cref="AtLeast"/> (an IN list) and take maxima in memory.
/// </summary>
public static class AccessLevels
{
    private static readonly AccessLevel[] Granted =
        [AccessLevel.View, AccessLevel.Download, AccessLevel.Contribute, AccessLevel.Edit, AccessLevel.Manage];

    /// <summary>The grantable levels at or above <paramref name="min"/> (None counts as View).</summary>
    public static AccessLevel[] AtLeast(AccessLevel min) => Granted.Where(l => l >= min).ToArray();

    public static AccessLevel Max(AccessLevel a, AccessLevel b) => a >= b ? a : b;

    public static AccessLevel Max(IEnumerable<AccessLevel> levels) => levels.DefaultIfEmpty(AccessLevel.None).Max();

    /// <summary>
    /// 404 when the caller can't see the resource at all (so ids can't be probed), 403 when they can
    /// see it but the action needs more (frame 12).
    /// </summary>
    public static void Require(this AccessLevel actual, AccessLevel required, string resource, Guid id)
    {
        if (actual < AccessLevel.View) throw new NotFoundException(resource, id);
        if (actual < required)
            throw new ForbiddenException($"This needs \"{Describe(required)}\" access; you have \"{Describe(actual)}\".");
    }

    public static string Describe(AccessLevel level) => level switch
    {
        AccessLevel.View => "Can view",
        AccessLevel.Download => "Can download",
        AccessLevel.Contribute => "Can contribute",
        AccessLevel.Edit => "Can edit",
        AccessLevel.Manage => "Can manage",
        _ => "No access",
    };
}
