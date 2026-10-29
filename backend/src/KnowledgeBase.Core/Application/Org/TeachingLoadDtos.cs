using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Workflow;

namespace KnowledgeBase.Core.Application.Org;

// The per-term teaching load (Settings → Teaching loads) and the faculty's own courses.

public sealed record TeachingTermDto(Guid Id, string Code, string Name, string YearName, TermStatus Status);

public sealed record CourseRefDto(Guid Id, string Code, string Title, UnitRefDto Program);

/// <summary>One line of the load. Section is null when the course isn't split into sections.</summary>
public sealed record TeachingAssignmentDto(Guid Id, PersonDto Person, CourseRefDto Course, string? Section);

public sealed record TeachingImportSummaryDto(
    string FileName, PersonDto ImportedBy, DateTimeOffset ImportedAt, int Rows, int Added, int Removed, int PeopleCreated, int CoursesCreated);

public sealed record TeachingLoadDto(
    TeachingTermDto Term,
    IReadOnlyList<TeachingAssignmentDto> Assignments,
    int FacultyCount,
    int CourseCount,
    TeachingImportSummaryDto? LastImport);

/// <summary>
/// The file goes up as base64 (at most 5 MB). Apply false only checks it and returns what would
/// change; Apply true replaces the term's load, and is refused while the file has errors.
/// </summary>
public sealed record ImportTeachingLoadRequest(Guid AcademicTermId, string FileName, string ContentBase64, bool Apply);

/// <summary>Line is the row number in the file (the header is row 1).</summary>
public sealed record ImportIssueDto(int Line, string Message);

public sealed record ImportPersonDto(string? FacultyId, string Name, string? Email);

public sealed record ImportCourseDto(string Program, string Code, string Title, string? PreviousTitle);

public sealed record ImportMembershipDto(string Person, string Program);

public sealed record ImportLoadLineDto(string Person, string? FacultyId, string Program, string CourseCode, string CourseTitle, string? Section);

public sealed record TeachingImportResultDto(
    TeachingTermDto Term,
    string FileName,
    bool Applied,
    int Rows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ImportIssueDto> Errors,
    IReadOnlyList<ImportIssueDto> Warnings,
    IReadOnlyList<ImportPersonDto> NewPeople,
    IReadOnlyList<ImportCourseDto> NewCourses,
    IReadOnlyList<ImportCourseDto> RenamedCourses,
    IReadOnlyList<ImportMembershipDto> NewMemberships,
    IReadOnlyList<ImportLoadLineDto> Added,
    IReadOnlyList<ImportLoadLineDto> Removed,
    int Unchanged);

/// <summary>
/// Encodes one faculty member's course in a term by hand: adds it to their load, or sets its sections.
/// CourseId picks a course from the program's catalog; otherwise CourseCode (and CourseTitle for a
/// code the catalog doesn't have yet) names it. ReplacesCourseId moves the lines of another course to
/// this one, for a course encoded wrongly. No sections = one line without a section.
/// </summary>
public sealed record SetTeachingCourseRequest(
    Guid AcademicTermId,
    Guid UserId,
    Guid ProgramId,
    Guid? CourseId,
    string? CourseCode,
    string? CourseTitle,
    IReadOnlyList<string>? Sections,
    Guid? ReplacesCourseId);

/// <summary>
/// What an encoded change did. Lines are the person's lines for the course afterwards (none after a
/// removal). Note names a live per-course submission whose course left the person's load; it stays.
/// </summary>
public sealed record TeachingLoadChangeDto(
    IReadOnlyList<TeachingAssignmentDto> Lines,
    int Added,
    int Removed,
    bool CourseCreated,
    bool MembershipAdded,
    string? Note);

/// <summary>
/// A course the caller teaches in a term, with its sections. When asked for a per-course submission
/// type, Submission is the caller's open (or approved) submission of that type for the course.
/// </summary>
public sealed record MyCourseDto(CourseRefDto Course, IReadOnlyList<string> Sections, MyCourseSubmissionDto? Submission);

public sealed record MyCourseSubmissionDto(Guid Id, string Number, SubmissionStatus Status);
