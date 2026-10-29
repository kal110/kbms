using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>
/// Who gets what on the system folders (frames 13, 21–22, 24) — the one place this matrix lives.
/// Grants are on roles of units (dean:, as:, head:) and unit memberships (college:, dept:, prog:),
/// never on named people except the folder's owner, so a new dean or AS needs no folder change.
///
///   Colleges (root) ............ none (shown for navigation only)
///   College .................... college:{c} View · dean:{c} Manage · vpaf View
///   Department (academic) ...... dept:{d} View                      (+ inherits the college)
///   Program .................... prog:{p} View · as:{p} Manage      (+ inherits)
///   Faculty (no inheritance) ... user:{owner} Contribute · as:{p} Download · dean:{c} Download · vpaf Download
///   Non-academic depts (root) .. none
///   Office ..................... dept:{d} View · head:{d} Manage
///   Staff (no inheritance) ..... user:{owner} Contribute · head:{d} Download
///   Year / Term ................ none of their own; inherit the person folder
///
/// The VPAF reads every academic folder, as a Dean does their college's; non-academic folders stay closed.
/// System administrators get nothing here: they can re-run the sync and repair grants, but have
/// no View on content.
/// </summary>
public static class SystemFolderGrants
{
    public sealed record Desired(PrincipalType Type, Guid PrincipalId, AccessLevel Level)
    {
        public string Key => PrincipalKeys.For(Type, PrincipalId);
    }

    /// <summary>Whether folders of this kind inherit their parent's grants.</summary>
    public static bool Inherits(FolderKind kind) => kind is not (FolderKind.Faculty or FolderKind.Staff);

    public static IReadOnlyList<Desired> College(Guid collegeId) =>
    [
        new(PrincipalType.College, collegeId, AccessLevel.View),
        new(PrincipalType.CollegeDean, collegeId, AccessLevel.Manage),
        new(PrincipalType.Vpaf, Guid.Empty, AccessLevel.View),
    ];

    public static IReadOnlyList<Desired> Department(Guid departmentId) =>
    [
        new(PrincipalType.Department, departmentId, AccessLevel.View),
    ];

    public static IReadOnlyList<Desired> Program(Guid programId) =>
    [
        new(PrincipalType.Program, programId, AccessLevel.View),
        new(PrincipalType.ProgramSupervisor, programId, AccessLevel.Manage),
    ];

    public static IReadOnlyList<Desired> Faculty(Guid ownerId, Guid programId, Guid collegeId) =>
    [
        new(PrincipalType.User, ownerId, AccessLevel.Contribute),
        new(PrincipalType.ProgramSupervisor, programId, AccessLevel.Download),
        new(PrincipalType.CollegeDean, collegeId, AccessLevel.Download),
        new(PrincipalType.Vpaf, Guid.Empty, AccessLevel.Download),
    ];

    public static IReadOnlyList<Desired> Office(Guid departmentId) =>
    [
        new(PrincipalType.Department, departmentId, AccessLevel.View),
        new(PrincipalType.DepartmentHead, departmentId, AccessLevel.Manage),
    ];

    public static IReadOnlyList<Desired> Staff(Guid ownerId, Guid departmentId) =>
    [
        new(PrincipalType.User, ownerId, AccessLevel.Contribute),
        new(PrincipalType.DepartmentHead, departmentId, AccessLevel.Download),
    ];
}
