using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;

namespace KnowledgeBase.Core.Domain.Org;

// The teaching load: which courses of which programs each faculty member teaches in a term.
// It arrives every semester from the registrar's file (CSV or Excel) and replaces that term's load.

/// <summary>A course in a program's catalog, e.g. BSCS · CS 301 Data Structures. Unique per (program, code).</summary>
public sealed class Course : Entity
{
    public Guid ProgramId { get; set; }
    public AcademicProgram Program { get; set; } = null!;

    /// <summary>As the registrar writes it, e.g. "CS 301". Matching ignores case, spaces and dashes.</summary>
    public required string Code { get; set; }
    public required string Title { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>"CS 301" and "cs-301" are the same course.</summary>
    public static string KeyOf(string code) =>
        string.Concat(code.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
}

/// <summary>One line of a term's teaching load: a faculty member teaches a course (one of its sections).</summary>
public sealed class TeachingAssignment : Entity
{
    public Guid AcademicTermId { get; set; }
    public AcademicTerm AcademicTerm { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;

    /// <summary>"" when the load doesn't split the course into sections.</summary>
    public string Section { get; set; } = "";
}

/// <summary>One teaching-load import: which file replaced which term's load, by whom, and what changed.</summary>
public sealed class TeachingLoadImport : Entity
{
    public Guid AcademicTermId { get; set; }
    public required string FileName { get; set; }
    public Guid ImportedByUserId { get; set; }
    public User ImportedBy { get; set; } = null!;
    public DateTimeOffset ImportedAt { get; set; }
    public int Rows { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int PeopleCreated { get; set; }
    public int CoursesCreated { get; set; }
}
