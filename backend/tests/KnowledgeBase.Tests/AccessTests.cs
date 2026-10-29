using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Application.Library;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using Microsoft.EntityFrameworkCore;
using static KnowledgeBase.Core.Application.Access.EffectiveAccessCalculator;

namespace KnowledgeBase.Tests;

/// <summary>Folder inheritance (frame 14) and the system folder grant matrix, without a database.</summary>
public class AccessTests
{
    private static readonly Guid College = Guid.NewGuid(), Department = Guid.NewGuid(), Program = Guid.NewGuid(),
        Faculty = Guid.NewGuid(), Term = Guid.NewGuid(), Jamie = Guid.NewGuid();

    /// <summary>College › Department › Program › Faculty (no inherit) › Term, with the system grants.</summary>
    private static Dictionary<Guid, Dictionary<string, Effective>> AcademicTree()
    {
        var nodes = new[]
        {
            new Node(College, null, $"/{College}/", true),
            new Node(Department, College, $"/{College}/{Department}/", true),
            new Node(Program, Department, $"/{College}/{Department}/{Program}/", true),
            new Node(Faculty, Program, $"/{College}/{Department}/{Program}/{Faculty}/", SystemFolderGrants.Inherits(FolderKind.Faculty)),
            new Node(Term, Faculty, $"/{College}/{Department}/{Program}/{Faculty}/{Term}/", true),
        };
        var grants = SystemFolderGrants.College(College).Select(g => new Grant(College, g.Key, g.Level))
            .Concat(SystemFolderGrants.Department(Department).Select(g => new Grant(Department, g.Key, g.Level)))
            .Concat(SystemFolderGrants.Program(Program).Select(g => new Grant(Program, g.Key, g.Level)))
            .Concat(SystemFolderGrants.Faculty(Jamie, Program, College).Select(g => new Grant(Faculty, g.Key, g.Level)));
        return Compute(College, nodes, grants, new Dictionary<string, Effective>());
    }

    [Fact]
    public void Program_folder_gives_program_members_view_and_the_supervisor_manage()
    {
        var program = AcademicTree()[Program];

        Assert.Equal(AccessLevel.View, program[PrincipalKeys.For(PrincipalType.Program, Program)].Level);
        Assert.Equal(AccessLevel.Manage, program[PrincipalKeys.For(PrincipalType.ProgramSupervisor, Program)].Level);
        Assert.Equal(AccessLevel.Manage, program[PrincipalKeys.For(PrincipalType.CollegeDean, College)].Level);
        Assert.Equal(College, program[PrincipalKeys.For(PrincipalType.CollegeDean, College)].SourceFolderId);
    }

    [Fact]
    public void Faculty_folder_is_private_to_the_owner_supervisor_dean_and_vpaf()
    {
        var faculty = AcademicTree()[Faculty];

        Assert.Equal(4, faculty.Count);
        Assert.Equal(AccessLevel.Download, faculty[PrincipalKeys.Vpaf].Level);
        Assert.Equal(AccessLevel.Contribute, faculty[PrincipalKeys.User(Jamie)].Level);
        Assert.Equal(AccessLevel.Download, faculty[PrincipalKeys.For(PrincipalType.ProgramSupervisor, Program)].Level);
        Assert.Equal(AccessLevel.Download, faculty[PrincipalKeys.For(PrincipalType.CollegeDean, College)].Level);
        Assert.False(faculty.ContainsKey(PrincipalKeys.For(PrincipalType.Program, Program)));
        Assert.False(faculty.ContainsKey(PrincipalKeys.For(PrincipalType.College, College)));
    }

    [Fact]
    public void Term_folder_inherits_the_faculty_folder()
    {
        var tree = AcademicTree();

        Assert.Equal(tree[Faculty].Keys.Order(), tree[Term].Keys.Order());
        Assert.All(tree[Term].Values, e => Assert.Equal(Faculty, e.SourceFolderId));
    }

    [Fact]
    public void Own_grant_wins_ties_but_never_lowers_an_inherited_level()
    {
        Guid parent = Guid.NewGuid(), child = Guid.NewGuid();
        var nodes = new[] { new Node(parent, null, $"/{parent}/", true), new Node(child, parent, $"/{parent}/{child}/", true) };
        var grants = new[]
        {
            new Grant(parent, "user:a", AccessLevel.View), new Grant(child, "user:a", AccessLevel.View),
            new Grant(parent, "user:b", AccessLevel.Manage), new Grant(child, "user:b", AccessLevel.View),
            new Grant(child, "user:c", AccessLevel.None),
        };

        var result = Compute(parent, nodes, grants, new Dictionary<string, Effective>())[child];

        Assert.Equal(new Effective(AccessLevel.View, child), result["user:a"]);
        Assert.Equal(new Effective(AccessLevel.Manage, parent), result["user:b"]);
        Assert.False(result.ContainsKey("user:c"));
    }

    [Fact]
    public void Recomputing_a_subtree_starts_from_the_parents_access()
    {
        Guid root = Guid.NewGuid(), above = Guid.NewGuid();
        var inherited = new Dictionary<string, Effective> { ["group:x"] = new(AccessLevel.Download, above) };

        var inheriting = Compute(root, [new Node(root, above, $"/{above}/{root}/", true)], [], inherited);
        var cutOff = Compute(root, [new Node(root, above, $"/{above}/{root}/", false)], [], inherited);

        Assert.Equal(new Effective(AccessLevel.Download, above), inheriting[root]["group:x"]);
        Assert.Empty(cutOff[root]);
    }

    [Fact]
    public void Require_hides_invisible_resources_and_forbids_insufficient_levels()
    {
        var id = Guid.NewGuid();

        Assert.Throws<NotFoundException>(() => AccessLevel.None.Require(AccessLevel.View, "Document", id));
        Assert.Throws<ForbiddenException>(() => AccessLevel.View.Require(AccessLevel.Download, "Document", id));
        AccessLevel.Manage.Require(AccessLevel.Edit, "Document", id);
    }

    [Fact]
    public void AtLeast_lists_the_levels_for_sql_in_filters()
    {
        Assert.Equal([AccessLevel.Edit, AccessLevel.Manage], AccessLevels.AtLeast(AccessLevel.Edit));
        Assert.Equal(5, AccessLevels.AtLeast(AccessLevel.None).Length);
    }
}

/// <summary>Document numbering, search query building and upload file names.</summary>
public class LibraryRulesTests
{
    [Theory]
    [InlineData("SYL-{yyyy}-{seq:4}", "SYL", 2026, 7, "SYL-2026-0007")]
    [InlineData("{code}-{yyyy}-{seq:0000}", "SYL", 2026, 413, "SYL-2026-0413")]
    [InlineData("MEMO-{yyyy}-{seq:2}", "MEMO", 2026, 14, "MEMO-2026-14")]
    [InlineData("{code}/{yy}/{seq}", "BR", 2026, 1234, "BR/26/1234")]
    public void Numbers_follow_the_type_pattern(string pattern, string code, int year, int seq, string expected)
    {
        Assert.Equal(expected, DocumentNumbering.Format(pattern, code, year, seq));
        Assert.Equal((year, seq), DocumentNumbering.Parse(pattern, code, expected));
    }

    [Theory]
    [InlineData("AP-{yyyy}-{seq:3}", "AP", "RO-2025-002")]
    [InlineData("AP-{yyyy}-{seq:3}", "AP", "AP-2025")]
    public void Numbers_from_another_pattern_dont_parse(string pattern, string code, string number) =>
        Assert.Null(DocumentNumbering.Parse(pattern, code, number));

    [Theory]
    [InlineData("CS 220: Data Structures!", "cs:* & 220:* & data:* & structures:*")]
    [InlineData("syllabus syllabus", "syllabus:*")]
    [InlineData("  &|!():* ", null)]
    public void Search_words_become_a_prefix_tsquery(string q, string? expected) =>
        Assert.Equal(expected, SearchService.ToPrefixQuery(q));

    [Theory]
    [InlineData(@"..\..\etc/My Report (final).PDF", "My-Report-final.pdf")]
    [InlineData("syllabus--v2..docx", "syllabus-v2.docx")]
    [InlineData("..", "file")]
    public void Upload_file_names_are_made_safe(string name, string expected) =>
        Assert.Equal(expected, UploadService.SafeFileName(name));
}

/// <summary>The access filters and search translate to PostgreSQL (ToQueryString needs no connection).</summary>
public class AccessQueryTests
{
    private static readonly UserContext Jamie = new()
    {
        UserId = Guid.NewGuid(),
        DisplayName = "Jamie Santos",
        Roles = [],
        Programs = [],
        StaffDepartmentIds = [],
        GroupIds = [],
        PrincipalKeys = new HashSet<string> { $"user:{Guid.NewGuid()}", $"prog:{Guid.NewGuid()}" },
    };

    [Fact]
    public void Visible_documents_with_state_and_rows_translate()
    {
        using var db = ModelTests.NewContext();
        var access = new AccessEvaluator(db, TimeProvider.System);

        var sql = access.VisibleDocuments(Jamie, AccessLevel.Download).WhereState(db, DocumentState.InReview).ToRows(db).ToQueryString();

        Assert.Contains("folder_effective_access", sql);
        Assert.Contains("document_permissions", sql);
        // An expiry check outside its grant's conjunction would let any unexpired grant reveal every document.
        Assert.Matches(@"\(\w+\.expires_at IS NULL OR \w+\.expires_at > @\w+\)", sql);
    }

    [Fact]
    public void Visible_folders_translate()
    {
        using var db = ModelTests.NewContext();
        var sql = new AccessEvaluator(db, TimeProvider.System).VisibleFolders(Jamie, AccessLevel.Contribute).ToQueryString();

        Assert.Contains("folder_effective_access", sql);
    }

    [Fact]
    public void Keyword_search_translates()
    {
        using var db = ModelTests.NewContext();
        var docs = new AccessEvaluator(db, TimeProvider.System).VisibleDocuments(Jamie);

        var sql = SearchService.Rank(SearchService.Matching(docs, "cs 220 syllabus"), "cs 220 syllabus").ToQueryString();

        Assert.Contains("to_tsquery", sql);
        Assert.Contains("ts_rank", sql);
        Assert.Contains("similarity", sql);
    }
}
