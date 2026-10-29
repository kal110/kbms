using System.Text.RegularExpressions;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>Rules every document follows, whether uploaded in the library or attached to a submission.</summary>
public static class DocumentRules
{
    /// <summary>
    /// The type's academic tag decides the period (frame 17): Term → a term (and its year), Year → a
    /// year and no term, None → neither.
    /// </summary>
    public static async Task<(Guid? YearId, Guid? TermId)> ResolvePeriodAsync(
        KbDbContext db, AcademicTag tag, Guid? yearId, Guid? termId, CancellationToken ct)
    {
        switch (tag)
        {
            case AcademicTag.Term:
                if (termId is null)
                    throw new ValidationAppException("academicTermId", "This document type is tagged by term; pick an academic term.");
                var termYear = await db.AcademicTerms.AsNoTracking()
                    .Where(t => t.Id == termId)
                    .Select(t => (Guid?)t.AcademicYearId)
                    .SingleOrDefaultAsync(ct)
                    ?? throw new ValidationAppException("academicTermId", "That academic term doesn't exist.");
                if (yearId is not null && yearId != termYear)
                    throw new ValidationAppException("academicYearId", "The term belongs to a different academic year.");
                return (termYear, termId);

            case AcademicTag.Year:
                if (yearId is null)
                    throw new ValidationAppException("academicYearId", "This document type is tagged by academic year; pick one.");
                if (termId is not null)
                    throw new ValidationAppException("academicTermId", "This document type is tagged by year, not by term.");
                if (!await db.AcademicYears.AnyAsync(y => y.Id == yearId, ct))
                    throw new ValidationAppException("academicYearId", "That academic year doesn't exist.");
                return (yearId, null);

            default:
                if (yearId is not null || termId is not null)
                    throw new ValidationAppException(yearId is not null ? "academicYearId" : "academicTermId",
                        "This document type isn't tagged by year or term.");
                return (null, null);
        }
    }

    /// <summary>Academic-only types can't be filed in office folders and vice versa.</summary>
    public static async Task CheckScopeAsync(KbDbContext db, DocumentType type, Folder folder, CancellationToken ct)
    {
        if (type.Scope == TypeScope.All) return;

        DepartmentKind? unit = folder.Kind switch
        {
            FolderKind.AcademicRoot or FolderKind.College => DepartmentKind.Academic,
            FolderKind.OfficeRoot => DepartmentKind.NonAcademic,
            _ when folder.DepartmentId is { } departmentId => await db.Departments.AsNoTracking()
                .Where(d => d.Id == departmentId).Select(d => (DepartmentKind?)d.Kind).SingleOrDefaultAsync(ct),
            _ => null,
        };
        if (unit is null) return; // a library folder outside the org tree takes any type

        var fits = type.Scope == TypeScope.Academic ? unit == DepartmentKind.Academic : unit == DepartmentKind.NonAcademic;
        if (!fits)
            throw new BusinessRuleException("type-scope", "This document type doesn't fit this folder.",
                $"{type.Name} is for {(type.Scope == TypeScope.Academic ? "academic" : "non-academic")} units only.");
    }

    public static string ValidTitle(string? title)
    {
        var t = title?.Trim();
        if (string.IsNullOrEmpty(t)) throw new ValidationAppException("title", "Give the document a title.");
        if (t.Length > 300) throw new ValidationAppException("title", "Keep the title under 300 characters.");
        return t;
    }

    public static string? ValidDescription(string? description)
    {
        var d = description?.Trim();
        if (string.IsNullOrEmpty(d)) return null;
        return d.Length <= 2000 ? d : throw new ValidationAppException("description", "Keep the description under 2,000 characters.");
    }

    /// <summary>Replaces a document's tags, creating tag names that don't exist yet.</summary>
    public static async Task SetTagsAsync(KbDbContext db, Document doc, IReadOnlyList<string> names, CancellationToken ct)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Any(n => n.Length > 80)) throw new ValidationAppException("tags", "Keep each tag under 80 characters.");
        if (wanted.Count > 20) throw new ValidationAppException("tags", "Use at most 20 tags.");

        var lower = wanted.Select(n => n.ToLowerInvariant()).ToArray();
        var existing = await db.Tags.Where(t => lower.Contains(t.Name.ToLower())).ToListAsync(ct);
        var tags = wanted.Select(n => existing.FirstOrDefault(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))
            ?? db.Tags.Local.FirstOrDefault(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))
            ?? db.Tags.Add(new Tag { Name = n }).Entity).ToList();

        var current = await db.DocumentTags.Where(dt => dt.DocumentId == doc.Id).ToListAsync(ct);
        db.DocumentTags.RemoveRange(current.Where(c => tags.All(t => t.Id != c.TagId)));
        foreach (var tag in tags.Where(t => current.All(c => c.TagId != t.Id)))
            db.DocumentTags.Add(new DocumentTag { DocumentId = doc.Id, TagId = tag.Id });
    }
}

/// <summary>
/// Document numbers from the type's pattern, e.g. "{code}-{yyyy}-{seq:0000}" → "SYL-2026-0413".
/// Tokens: {code}, {yyyy}, {yy}, {seq}, and {seq:4} or {seq:0000} (zero-padded to four digits).
/// </summary>
public sealed partial class DocumentNumbering(KbDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Takes the next number atomically (an upsert on document_number_sequences). It commits with
    /// the caller's transaction if one is open; otherwise right away, so a failed upload leaves a gap.
    /// </summary>
    public async Task<string> NextAsync(DocumentType type, CancellationToken ct = default)
    {
        var year = clock.GetUtcNow().Year;
        var values = await db.Database.SqlQuery<int>($"""
            INSERT INTO document_number_sequences (document_type_id, year, next_value)
            VALUES ({type.Id}, {year}, 2)
            ON CONFLICT (document_type_id, year)
            DO UPDATE SET next_value = document_number_sequences.next_value + 1
            RETURNING next_value - 1 AS "Value"
            """).ToListAsync(ct);
        return Format(type.NumberPattern ?? "{code}-{yyyy}-{seq:0000}", type.Code, year, values.Single());
    }

    public static string Format(string pattern, string code, int year, int seq) =>
        Token().Replace(pattern, m => m.Groups["name"].Value.ToLowerInvariant() switch
        {
            "code" => code,
            "yyyy" => year.ToString("0000"),
            "yy" => (year % 100).ToString("00"),
            "seq" => m.Groups["width"].Success ? seq.ToString(new string('0', Width(m.Groups["width"].Value))) : seq.ToString(),
            _ => m.Value,
        });

    /// <summary>Reads back the year and sequence of a number made by <paramref name="pattern"/>, if it matches.</summary>
    public static (int Year, int Seq)? Parse(string pattern, string code, string number)
    {
        var regex = "^" + Token().Replace(Regex.Escape(pattern).Replace(@"\{", "{"), m => m.Groups["name"].Value.ToLowerInvariant() switch
        {
            "code" => Regex.Escape(code),
            "yyyy" => "(?<year>\\d{4})",
            "yy" => "(?<yy>\\d{2})",
            "seq" => "(?<seq>\\d+)",
            _ => Regex.Escape(m.Value),
        }) + "$";
        var match = Regex.Match(number, regex);
        if (!match.Success || !match.Groups["seq"].Success) return null;
        var y = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value)
            : match.Groups["yy"].Success ? 2000 + int.Parse(match.Groups["yy"].Value)
            : 0;
        return (y, int.Parse(match.Groups["seq"].Value));
    }

    /// <summary>"{seq:4}" and "{seq:0000}" both mean four digits.</summary>
    private static int Width(string spec) => spec.All(c => c == '0') ? spec.Length : int.Parse(spec);

    [GeneratedRegex(@"\{(?<name>code|yyyy|yy|seq)(:(?<width>\d+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Token();
}
