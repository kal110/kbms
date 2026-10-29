using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Application.Org;

/// <summary>
/// Reads a teaching-load file: CSV (comma, semicolon or tab; UTF-8 or Excel's ANSI) or the first sheet
/// of an .xlsx workbook. Headers are matched loosely ("Faculty ID", "employee_no", "Subject Code"…),
/// so the registrar's export works without renaming columns.
/// </summary>
public static class TeachingLoadFile
{
    /// <summary>One data row; <see cref="Line"/> is the 1-based row in the file, as a spreadsheet shows it.</summary>
    public sealed record Row(
        int Line, string? FacultyId, string? Name, string? Email, string? Program, string? CourseCode, string? CourseTitle, string? Section);

    public sealed record Result(IReadOnlyList<Row> Rows, IReadOnlyList<string> Columns);

    public const string Template = "faculty_id,faculty_name,email,program,course_code,course_title,section";

    private enum Column { FacultyId, Name, Email, Program, CourseCode, CourseTitle, Section }

    // Header text with case, spaces and punctuation removed → the column it means.
    private static readonly Dictionary<string, Column> Aliases = new()
    {
        ["facultyid"] = Column.FacultyId, ["employeeid"] = Column.FacultyId, ["employeeno"] = Column.FacultyId,
        ["employeenumber"] = Column.FacultyId, ["facultyno"] = Column.FacultyId, ["facultynumber"] = Column.FacultyId,
        ["signinid"] = Column.FacultyId, ["externalid"] = Column.FacultyId, ["idno"] = Column.FacultyId, ["id"] = Column.FacultyId,
        ["facultyname"] = Column.Name, ["name"] = Column.Name, ["fullname"] = Column.Name, ["faculty"] = Column.Name,
        ["instructor"] = Column.Name, ["instructorname"] = Column.Name, ["teacher"] = Column.Name,
        ["email"] = Column.Email, ["emailaddress"] = Column.Email, ["facultyemail"] = Column.Email,
        ["program"] = Column.Program, ["programcode"] = Column.Program, ["degreeprogram"] = Column.Program,
        ["coursecode"] = Column.CourseCode, ["course"] = Column.CourseCode, ["courseno"] = Column.CourseCode,
        ["subjectcode"] = Column.CourseCode, ["subject"] = Column.CourseCode, ["subjectno"] = Column.CourseCode,
        ["coursetitle"] = Column.CourseTitle, ["title"] = Column.CourseTitle, ["descriptivetitle"] = Column.CourseTitle,
        ["coursename"] = Column.CourseTitle, ["subjecttitle"] = Column.CourseTitle, ["subjectname"] = Column.CourseTitle,
        ["description"] = Column.CourseTitle,
        ["section"] = Column.Section, ["sectioncode"] = Column.Section, ["sec"] = Column.Section, ["block"] = Column.Section,
    };

    /// <summary>Throws a 400 naming the problem when the file can't be read or misses a required column.</summary>
    public static Result Parse(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        IReadOnlyList<string[]> table = extension switch
        {
            ".xlsx" => ReadXlsx(content),
            ".csv" or ".txt" or ".tsv" => ReadCsv(Decode(content)),
            ".xls" => throw new ValidationAppException("file", "Old .xls workbooks aren't supported. Save the file as .xlsx or .csv."),
            _ => throw new ValidationAppException("file", "Use a .csv or .xlsx file."),
        };

        var headerIndex = -1;
        for (var i = 0; i < table.Count && headerIndex < 0; i++)
            if (table[i].Any(c => !string.IsNullOrWhiteSpace(c))) headerIndex = i;
        if (headerIndex < 0) throw new ValidationAppException("file", "The file is empty.");

        var map = new Dictionary<Column, int>();
        var header = table[headerIndex];
        for (var i = 0; i < header.Length; i++)
        {
            if (Aliases.TryGetValue(Normalize(header[i]), out var column)) map.TryAdd(column, i);
        }
        var missing = new List<string>();
        if (!map.ContainsKey(Column.Program)) missing.Add("program");
        if (!map.ContainsKey(Column.CourseCode)) missing.Add("course_code");
        if (!map.ContainsKey(Column.FacultyId) && !map.ContainsKey(Column.Email)) missing.Add("faculty_id or email");
        if (missing.Count > 0)
            throw new ValidationAppException("file",
                $"The first row must name the columns. Missing: {string.Join(", ", missing)}. Expected: {Template}.");

        string? Cell(string[] row, Column column) =>
            map.TryGetValue(column, out var i) && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? Clean(row[i]) : null;

        var rows = new List<Row>();
        for (var i = headerIndex + 1; i < table.Count; i++)
        {
            var r = table[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(new Row(i + 1, Cell(r, Column.FacultyId), Cell(r, Column.Name), Cell(r, Column.Email), Cell(r, Column.Program),
                Cell(r, Column.CourseCode), Cell(r, Column.CourseTitle), Cell(r, Column.Section)));
        }
        return new Result(rows, map.OrderBy(m => m.Value).Select(m => m.Key.ToString()).ToList());
    }

    private static string Normalize(string header) =>
        string.Concat(header.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>Trims and collapses runs of whitespace (Excel cells often carry trailing spaces or line breaks).</summary>
    private static string Clean(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---- CSV

    /// <summary>UTF-8 (with or without BOM); Excel's plain "CSV" is ANSI, so invalid UTF-8 falls back to Windows-1252.</summary>
    private static string Decode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content);
        }
    }

    /// <summary>RFC 4180: quoted fields may hold the delimiter, line breaks and doubled quotes.</summary>
    public static IReadOnlyList<string[]> ReadCsv(string text)
    {
        var delimiter = DetectDelimiter(text);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }

    /// <summary>The most frequent of comma, semicolon and tab in the first line, outside quotes.</summary>
    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\r' or '\n') break;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        return counts.MaxBy(kv => kv.Value).Key;
    }

    // ---- XLSX: the first worksheet, read straight from the Open XML package.

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static IReadOnlyList<string[]> ReadXlsx(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var shared = Load(zip.GetEntry("xl/sharedStrings.xml"))?.Root?.Elements(Main + "si").Select(TextOf).ToList() ?? [];
            var sheet = Load(zip.GetEntry(FirstSheetPath(zip)))
                ?? throw new ValidationAppException("file", "The workbook has no worksheet.");

            var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
            var rowIndex = -1;
            foreach (var row in sheet.Descendants(Main + "row"))
            {
                rowIndex = int.TryParse((string?)row.Attribute("r"), out var r) ? r - 1 : rowIndex + 1;
                var cells = new SortedDictionary<int, string>();
                var column = -1;
                foreach (var cell in row.Elements(Main + "c"))
                {
                    column = (string?)cell.Attribute("r") is { } reference ? ColumnIndex(reference) : column + 1;
                    var raw = (string?)cell.Attribute("t") == "inlineStr" ? TextOf(cell.Element(Main + "is")) : cell.Element(Main + "v")?.Value;
                    if (raw is null) continue;
                    cells[column] = (string?)cell.Attribute("t") switch
                    {
                        "s" => int.TryParse(raw, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
                        "b" => raw == "1" ? "TRUE" : "FALSE",
                        _ => raw,
                    };
                }
                if (cells.Count > 0) rows[rowIndex] = cells;
            }

            // Spreadsheet line numbers are kept: missing rows become empty ones.
            var table = new List<string[]>();
            if (rows.Count == 0) return table;
            for (var r = 0; r <= rows.Keys.Max(); r++)
            {
                if (!rows.TryGetValue(r, out var cells)) { table.Add([]); continue; }
                var line = new string[cells.Keys.Max() + 1];
                for (var c = 0; c < line.Length; c++) line[c] = cells.GetValueOrDefault(c) ?? "";
                table.Add(line);
            }
            return table;
        }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            throw new ValidationAppException("file", "This isn't a readable .xlsx workbook. Save it again from Excel, or as .csv.");
        }
    }

    private static XDocument? Load(ZipArchiveEntry? entry)
    {
        if (entry is null) return null;
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    /// <summary>The text of an &lt;si&gt; or &lt;is&gt;: plain, or rich-text runs (phonetic hints left out).</summary>
    private static string TextOf(XElement? element) =>
        element is null ? "" : string.Concat(element.Elements(Main + "t").Concat(element.Elements(Main + "r").Elements(Main + "t")).Select(t => t.Value));

    /// <summary>xl/workbook.xml names the sheets in order; its relationships say which part holds the first one.</summary>
    private static string FirstSheetPath(ZipArchive zip)
    {
        const string fallback = "xl/worksheets/sheet1.xml";
        var relId = (string?)Load(zip.GetEntry("xl/workbook.xml"))?.Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Rel + "id");
        var target = (string?)Load(zip.GetEntry("xl/_rels/workbook.xml.rels"))?.Root?.Elements()
            .FirstOrDefault(e => (string?)e.Attribute("Id") == relId)?.Attribute("Target");
        if (relId is null || target is null) return fallback;
        return target.StartsWith('/') ? target.TrimStart('/') : $"xl/{target}";
    }

    /// <summary>"C12" → 2 (0-based column).</summary>
    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return index - 1;
    }
}
