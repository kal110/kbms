using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.Access;

// Grants on folders, collections and documents (frames 13–14). Grants inherit down the folder tree
// unless a folder or document breaks inheritance. Levels are ordered: each implies the ones below.

public enum AccessLevel
{
    None = 0,
    View = 1,
    Download = 2,
    Contribute = 3,
    Edit = 4,
    Manage = 5,
}

public enum ResourceType
{
    Folder,
    Collection,
    Document,
}

public enum PrincipalType
{
    User,
    Group,
    College,
    Department,
    Program,
    CollegeDean,
    ProgramSupervisor,
    DepartmentHead,
    /// <summary>The VP for Academic Affairs, university-wide (no unit id: Guid.Empty).</summary>
    Vpaf,
}

/// <summary>
/// Principal keys are how a grant names who it's for, and how a caller's memberships are matched
/// against grants: user:{id} · group:{id} · college:{id} · dept:{id} · prog:{id} · dean:{collegeId} ·
/// as:{programId} · head:{departmentId} · vpaf (the university-wide VPAF role).
/// </summary>
public static class PrincipalKeys
{
    public static string User(Guid id) => $"user:{id}";
    public static string Group(Guid id) => $"group:{id}";
    public static string College(Guid id) => $"college:{id}";
    public static string Department(Guid id) => $"dept:{id}";
    public static string Program(Guid id) => $"prog:{id}";
    public static string Dean(Guid collegeId) => $"dean:{collegeId}";
    public static string Supervisor(Guid programId) => $"as:{programId}";
    public static string Head(Guid departmentId) => $"head:{departmentId}";
    public const string Vpaf = "vpaf";

    public static string For(PrincipalType type, Guid id) => type switch
    {
        PrincipalType.User => User(id),
        PrincipalType.Group => Group(id),
        PrincipalType.College => College(id),
        PrincipalType.Department => Department(id),
        PrincipalType.Program => Program(id),
        PrincipalType.CollegeDean => Dean(id),
        PrincipalType.ProgramSupervisor => Supervisor(id),
        PrincipalType.DepartmentHead => Head(id),
        PrincipalType.Vpaf => Vpaf,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public sealed class DocumentPermission : Entity
{
    public ResourceType ResourceType { get; set; }

    /// <summary>Folder, collection or document id (polymorphic, validated in the app).</summary>
    public Guid ResourceId { get; set; }
    public PrincipalType PrincipalType { get; set; }
    public Guid PrincipalId { get; set; }

    /// <summary>Denormalized <see cref="PrincipalKeys"/> value for fast matching.</summary>
    public required string PrincipalKey { get; set; }
    public AccessLevel Level { get; set; }

    /// <summary>System grants come from the org tree (academic folders) and can't be edited by hand.</summary>
    public bool IsSystem { get; set; }
    public Guid? GrantedByUserId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// Derived: the effective level each principal key has on each folder after inheritance.
/// Recomputed per subtree whenever grants or inheritance change (frame 14).
/// </summary>
public sealed class FolderEffectiveAccess
{
    public Guid FolderId { get; set; }
    public required string PrincipalKey { get; set; }
    public AccessLevel Level { get; set; }

    /// <summary>The folder whose grant produced this row (itself, or the ancestor it inherits from).</summary>
    public Guid SourceFolderId { get; set; }
}
