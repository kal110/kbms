using System.IO.Compression;
using System.Text;
using KnowledgeBase.Core.Application.Org;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;

namespace KnowledgeBase.Tests;

/// <summary>Reading the registrar's teaching-load file (CSV and .xlsx), without a database.</summary>
public class TeachingLoadTests
{
    /// <summary>UTF-8 with a BOM, as Excel's "CSV UTF-8" saves it.</summary>
    private static TeachingLoadFile.Result Csv(string text) =>
        TeachingLoadFile.Parse("load.csv", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]);

    [Fact]
    public void Csv_reads_quoted_fields_and_keeps_spreadsheet_line_numbers()
    {
        var result = Csv(
            "faculty_id,faculty_name,email,program,course_code,course_title,section\r\n" +
            "u-jamie,\"Santos, Jamie\",jamie@usa.edu.ph,BSCS,CS 301,\"Data Structures \"\"and\"\" Algorithms\",a\r\n" +
            "\r\n" +
            "u-kevin,Kevin   Ong,,BSCS,CS 340,\"Software\nEngineering\",\r\n");

        Assert.Equal(2, result.Rows.Count);
        var jamie = result.Rows[0];
        Assert.Equal(2, jamie.Line);
        Assert.Equal("Santos, Jamie", jamie.Name);
        Assert.Equal("Data Structures \"and\" Algorithms", jamie.CourseTitle);
        Assert.Equal("a", jamie.Section);

        var kevin = result.Rows[1];
        Assert.Equal(4, kevin.Line);
        Assert.Equal("Kevin Ong", kevin.Name);
        Assert.Null(kevin.Email);
        Assert.Equal("Software Engineering", kevin.CourseTitle);
        Assert.Null(kevin.Section);
    }

    [Fact]
    public void Registrar_headers_are_recognized_and_semicolons_detected()
    {
        var result = Csv("Employee No.;Instructor;Degree Program;Subject Code;Descriptive Title;Block\n20231234;Leo Tan;BSCS;CS101;Intro;C\n");

        var row = Assert.Single(result.Rows);
        Assert.Equal("20231234", row.FacultyId);
        Assert.Equal("Leo Tan", row.Name);
        Assert.Equal("BSCS", row.Program);
        Assert.Equal("CS101", row.CourseCode);
        Assert.Equal("Intro", row.CourseTitle);
        Assert.Equal("C", row.Section);
        Assert.Equal(["FacultyId", "Name", "Program", "CourseCode", "CourseTitle", "Section"], result.Columns);
    }

    [Fact]
    public void Ansi_csv_from_excel_falls_back_to_windows_1252()
    {
        var bytes = Encoding.Latin1.GetBytes("email,program,course_code,course_title\nnina@usa.edu.ph,BSIS,IS 230,Peña Systems\n");
        var row = Assert.Single(TeachingLoadFile.Parse("load.csv", bytes).Rows);
        Assert.Equal("Peña Systems", row.CourseTitle);
    }

    [Fact]
    public void Missing_columns_are_named()
    {
        var error = Assert.Throws<ValidationAppException>(() => Csv("name,course_code\nJamie,CS 301\n"));
        var message = Assert.Single(error.Errors["file"]);
        Assert.Contains("Missing: program, faculty_id or email", message);
    }

    [Theory]
    [InlineData("load.xls", "Old .xls workbooks")]
    [InlineData("load.pdf", "Use a .csv or .xlsx file")]
    public void Other_formats_are_refused(string fileName, string expected)
    {
        var error = Assert.Throws<ValidationAppException>(() => TeachingLoadFile.Parse(fileName, [1, 2, 3]));
        Assert.Contains(expected, Assert.Single(error.Errors["file"]));
    }

    [Fact]
    public void Xlsx_reads_the_first_sheet_with_shared_inline_and_numeric_cells()
    {
        // The first sheet lives in sheet2.xml; C2 is empty and row 3 is missing, as Excel writes them.
        var bytes = Workbook(
            sharedStrings: """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>Faculty ID</t></si><si><t>Name</t></si><si><t>Program</t></si><si><t>Course code</t></si>
                  <si><t>Course title</t></si><si><r><t>Data </t></r><r><t>Structures</t></r></si><si><t>BSCS</t></si>
                </sst>
                """,
            sheet: """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c>
                    <c r="D1" t="s"><v>3</v></c><c r="E1" t="s"><v>4</v></c></row>
                  <row r="2"><c r="A2"><v>20231234</v></c><c r="B2" t="inlineStr"><is><t>Jamie Santos</t></is></c>
                    <c r="D2" t="inlineStr"><is><t>CS 301</t></is></c><c r="E2" t="s"><v>5</v></c></row>
                  <row r="4"><c r="A4" t="inlineStr"><is><t>u-maria</t></is></c><c r="C4" t="s"><v>6</v></c>
                    <c r="D4" t="inlineStr"><is><t>CS 150</t></is></c></row>
                </sheetData></worksheet>
                """);

        var result = TeachingLoadFile.Parse("Load 2026-1.xlsx", bytes);

        Assert.Collection(result.Rows,
            jamie =>
            {
                Assert.Equal(2, jamie.Line);
                Assert.Equal("20231234", jamie.FacultyId);
                Assert.Equal("Jamie Santos", jamie.Name);
                Assert.Null(jamie.Program);
                Assert.Equal("CS 301", jamie.CourseCode);
                Assert.Equal("Data Structures", jamie.CourseTitle);
            },
            maria =>
            {
                Assert.Equal(4, maria.Line);
                Assert.Equal("u-maria", maria.FacultyId);
                Assert.Equal("BSCS", maria.Program);
                Assert.Null(maria.CourseTitle);
            });
    }

    [Fact]
    public void A_broken_workbook_is_a_validation_error()
    {
        var error = Assert.Throws<ValidationAppException>(() => TeachingLoadFile.Parse("load.xlsx", Encoding.UTF8.GetBytes("not a zip")));
        Assert.Contains("readable .xlsx", Assert.Single(error.Errors["file"]));
    }

    [Theory]
    [InlineData("CS 301", "cs-301")]
    [InlineData("IT214", "it 214")]
    public void Course_codes_match_ignoring_case_spaces_and_dashes(string a, string b) =>
        Assert.Equal(Course.KeyOf(a), Course.KeyOf(b));

    private static byte[] Workbook(string sharedStrings, string sheet)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Add("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Load" sheetId="1" r:id="rId7"/><sheet name="Notes" sheetId="2" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId7" Type="worksheet" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Add("xl/sharedStrings.xml", sharedStrings);
            Add("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>""");
            Add("xl/worksheets/sheet2.xml", sheet);
        }
        return stream.ToArray();
    }
}
