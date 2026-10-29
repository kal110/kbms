using System.Diagnostics;
using System.Text;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Ai;
using KnowledgeBase.Core.Infrastructure.Ocr;
using KnowledgeBase.Core.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static KnowledgeBase.Tests.ProcessingTests;

namespace KnowledgeBase.Tests;

/// <summary>OCR: Tesseract's TSV as blocks, the OCR stage of processing, the catch-up, and (where installed) Tesseract itself.</summary>
public class OcrTests
{
    private const string Engine = "tesseract 5.3.4 (eng+fil)";

    // ---- Tesseract TSV → blocks -----------------------------------------------------------------

    private sealed record TsvLine(int Block, int Paragraph, double Top, double Height, string Text, double Confidence = 95);

    private static string Tsv(params TsvLine[] lines)
    {
        var sb = new StringBuilder("level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n");
        sb.Append("1\t1\t0\t0\t0\t0\t0\t0\t2480\t3508\t-1\t\n");
        var lineNumbers = new Dictionary<(int, int), int>();
        foreach (var l in lines)
        {
            var n = lineNumbers[(l.Block, l.Paragraph)] = lineNumbers.GetValueOrDefault((l.Block, l.Paragraph)) + 1;
            sb.Append($"4\t1\t{l.Block}\t{l.Paragraph}\t{n}\t0\t200\t{l.Top}\t1500\t{l.Height}\t-1\t\n");
            var words = l.Text.Split(' ');
            for (var w = 0; w < words.Length; w++)
                sb.Append($"5\t1\t{l.Block}\t{l.Paragraph}\t{n}\t{w + 1}\t{200 + w * 100}\t{l.Top}\t90\t{l.Height}\t{l.Confidence}\t{words[w]}\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Tsv_becomes_headings_paragraphs_and_list_items()
    {
        var tsv = Tsv(
            new TsvLine(1, 1, 197, 54, "COURSE SYLLABUS"),
            new TsvLine(2, 1, 389, 40, "The course introduces data structures and"),
            new TsvLine(2, 1, 449, 40, "algorithms through student-"),
            new TsvLine(2, 1, 509, 40, "led projects."),
            new TsvLine(3, 1, 609, 40, "1. Arrays and linked lists"),
            new TsvLine(3, 1, 669, 40, "2. Stacks and queues"),
            new TsvLine(4, 1, 829, 40, "Ang mga mag-aaral ay inaasahang dumalo."),
            new TsvLine(4, 1, 889, 40, "~;' ,.", Confidence: 12),
            new TsvLine(5, 1, 3400, 30, "Page 2 of 5"));

        var page = TesseractTsv.ToPage(2, tsv, minConfidence: 30);

        Assert.Equal(2, page.Number);
        Assert.Equal(
            [
                new PageBlock(BlockKind.Heading, "COURSE SYLLABUS", 2),
                new PageBlock(BlockKind.Paragraph, "The course introduces data structures and algorithms through student-led projects."),
                new PageBlock(BlockKind.ListItem, "1. Arrays and linked lists"),
                new PageBlock(BlockKind.ListItem, "2. Stacks and queues"),
                new PageBlock(BlockKind.Paragraph, "Ang mga mag-aaral ay inaasahang dumalo."),
            ],
            page.Blocks);
        Assert.Equal(95, page.Confidence);
        Assert.Equal(28, page.Words);
    }

    [Fact]
    public void Short_capitals_at_body_size_are_a_minor_heading()
    {
        var page = TesseractTsv.ToPage(1, Tsv(
            new TsvLine(1, 1, 300, 40, "LEARNING OUTCOMES"),
            new TsvLine(2, 1, 360, 40, "Students write programs that use each structure.")), minConfidence: 30);

        Assert.Equal(new PageBlock(BlockKind.Heading, "LEARNING OUTCOMES", 3), page.Blocks[0]);
        Assert.Equal(BlockKind.Paragraph, page.Blocks[1].Kind);
    }

    [Fact]
    public void Blank_or_noise_only_page_has_no_blocks()
    {
        Assert.Empty(TesseractTsv.ToPage(1, Tsv(), 30).Blocks);
        Assert.Empty(TesseractTsv.ToPage(1, Tsv(new TsvLine(1, 1, 500, 40, "%& ~~ ;", Confidence: 8)), 30).Blocks);
        Assert.Empty(TesseractTsv.ToPage(1, "", 30).Blocks);
    }

    // ---- The OCR stage --------------------------------------------------------------------------

    private sealed class FakeOcr(OcrStatus status, Func<int, OcrPage?>? read = null) : IOcrEngine
    {
        public List<(string ContentType, int[] Pages)> Calls { get; } = [];

        public Task<OcrStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(status);

        public Task<OcrResult> ReadAsync(byte[] file, string contentType, IReadOnlyList<int> pages, CancellationToken ct = default)
        {
            Calls.Add((contentType, pages.ToArray()));
            var done = new List<OcrPage>();
            var failed = new SortedDictionary<int, string>();
            foreach (var number in pages)
            {
                if ((read ?? Scanned)(number) is { } page) done.Add(page);
                else failed[number] = "Tesseract timed out";
            }
            return Task.FromResult(new OcrResult(Engine, done, failed, []));
        }
    }

    private static OcrPage Scanned(int number) => new(number,
        [new PageBlock(BlockKind.Heading, "Grading System", 2), new PageBlock(BlockKind.Paragraph, $"Quizzes count for thirty percent of the grade on page {number}.")],
        Words: 12, Confidence: 91);

    /// <summary>Page 1 has a text layer; page 2 is blank, as a scanned page looks to the extractor.</summary>
    private static byte[] TextThenScanPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("The syllabus lists the topics, the readings and the grading of the course.", 11, new PdfPoint(50, 760), font);
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    private static DocumentProcessor Processor(MemoryStorage storage, IOcrEngine? ocr, bool ai = true) =>
        new(storage, Options.Create(new AiOptions { Enabled = ai }), Options.Create(new ProcessingOptions()), ocr: ocr);

    [Fact]
    public async Task Scanned_pages_are_read_and_replace_the_empty_extract()
    {
        var pdf = TextThenScanPdf();
        var storage = new MemoryStorage { ["docs/a.pdf"] = pdf };
        var ocr = new FakeOcr(new OcrStatus(Engine, null));
        var version = Version("docs/a.pdf", "application/pdf", pdf.Length);

        var result = await Processor(storage, ocr).RunAsync(version, _ => { }, CancellationToken.None);

        var call = Assert.Single(ocr.Calls);
        Assert.Equal("application/pdf", call.ContentType);
        Assert.Equal([2], call.Pages);
        Assert.Equal(DocumentProcessor.OcrKey(version.Id, 1), result.TextObjectKey);
        Assert.Contains("syllabus lists the topics", result.Pages![0].Text);
        Assert.Equal("Grading System Quizzes count for thirty percent of the grade on page 2.", result.Pages[1].Text);

        var saved = ExtractedText.FromJsonLines(Encoding.UTF8.GetString(storage[DocumentProcessor.OcrKey(version.Id, 1)]));
        Assert.Equal("pdfpig+tesseract", saved.Extractor);
        Assert.Empty(saved.LowTextPages);

        var notes = Assert.IsType<Dictionary<string, object?>>(result.Metrics["ocr"]);
        Assert.Equal(Engine, notes["engine"]);
        Assert.Equal("2", notes["pages"]);
        Assert.Equal(91d, notes["confidence"]);
        Assert.False(notes.ContainsKey("failed"));

        // The chunks cut from the OCR text cite page 2 and are kept under their own key.
        Assert.True(storage.ContainsKey(DocumentProcessor.ChunksKey(version.Id, 1, ocr: true)));
        Assert.False(storage.ContainsKey(DocumentProcessor.ChunksKey(version.Id, 1)));
    }

    [Fact]
    public async Task A_retry_reuses_what_ocr_read()
    {
        var pdf = TextThenScanPdf();
        var storage = new MemoryStorage { ["docs/a.pdf"] = pdf };
        var ocr = new FakeOcr(new OcrStatus(Engine, null));
        var version = Version("docs/a.pdf", "application/pdf", pdf.Length);
        await Processor(storage, ocr).RunAsync(version, _ => { }, CancellationToken.None);

        var again = await Processor(storage, ocr).RunAsync(version, _ => { }, CancellationToken.None);

        Assert.Single(ocr.Calls);
        var notes = Assert.IsType<Dictionary<string, object?>>(again.Metrics["ocr"]);
        Assert.Equal(true, notes["checkpoint"]);
        Assert.Equal("Grading System Quizzes count for thirty percent of the grade on page 2.", again.Pages![1].Text);
    }

    [Fact]
    public async Task Chunks_cut_before_ocr_are_not_reused_once_it_reads_the_scan()
    {
        var pdf = TextThenScanPdf();
        var storage = new MemoryStorage { ["docs/a.pdf"] = pdf };
        var version = Version("docs/a.pdf", "application/pdf", pdf.Length);
        await Processor(storage, ocr: null).RunAsync(version, _ => { }, CancellationToken.None);
        var before = Chunker.FromJsonLines(Encoding.UTF8.GetString(storage[DocumentProcessor.ChunksKey(version.Id, 1)]));
        Assert.DoesNotContain(before, c => c.Text.Contains("Quizzes"));

        await Processor(storage, new FakeOcr(new OcrStatus(Engine, null))).RunAsync(version, _ => { }, CancellationToken.None);
        var chunks = await Processor(storage, ocr: null).PrepareChunksAsync(version, CancellationToken.None);

        Assert.Contains(chunks, c => c.Text.Contains("Quizzes count for thirty percent") && c.PageEnd == 2);
    }

    [Fact]
    public async Task Without_an_engine_scans_are_flagged_and_the_text_stays_as_extracted()
    {
        var pdf = TextThenScanPdf();
        var storage = new MemoryStorage { ["docs/a.pdf"] = pdf };
        var version = Version("docs/a.pdf", "application/pdf", pdf.Length);

        var result = await Processor(storage, new FakeOcr(new OcrStatus(null, "Tesseract isn't installed")), ai: false)
            .RunAsync(version, _ => { }, CancellationToken.None);

        var notes = Assert.IsType<Dictionary<string, object?>>(result.Metrics["ocr"]);
        Assert.Equal(DocumentProcessor.SkippedNoOcr, notes["status"]);
        Assert.Equal("2", notes["pages"]);
        Assert.Equal("Tesseract isn't installed", notes["reason"]);
        Assert.Equal(DocumentProcessor.PagesKey(version.Id, 1), result.TextObjectKey);
        Assert.Equal("", result.Pages![1].Text);
        Assert.False(storage.ContainsKey(DocumentProcessor.OcrKey(version.Id, 1)));
    }

    [Fact]
    public async Task A_photo_of_a_document_is_read_as_one_page()
    {
        var storage = new MemoryStorage { ["docs/memo.jpg"] = [0xFF, 0xD8, 0xFF] };
        var ocr = new FakeOcr(new OcrStatus(Engine, null));
        var version = Version("docs/memo.jpg", "image/jpeg", 3);

        var result = await Processor(storage, ocr, ai: false).RunAsync(version, _ => { }, CancellationToken.None);

        var call = Assert.Single(ocr.Calls);
        Assert.Equal("image/jpeg", call.ContentType);
        Assert.Equal([1], call.Pages);
        Assert.Equal(DocumentProcessor.OcrKey(version.Id, 1), result.TextObjectKey);
        Assert.Equal("Grading System Quizzes count for thirty percent of the grade on page 1.", Assert.Single(result.Pages!).Text);
    }

    [Fact]
    public async Task Text_files_need_no_ocr()
    {
        var storage = new MemoryStorage { ["docs/a.md"] = Encoding.UTF8.GetBytes("# Policy\n\nEvery student attends the orientation.") };
        var ocr = new FakeOcr(new OcrStatus(Engine, null));

        var result = await Processor(storage, ocr, ai: false).RunAsync(Version("docs/a.md", "text/markdown", 10), _ => { }, CancellationToken.None);

        Assert.Equal("not needed", result.Metrics["ocr"]);
        Assert.Empty(ocr.Calls);
    }

    [Fact]
    public async Task Pages_ocr_cannot_read_are_noted_and_none_at_all_fails_the_stage()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        builder.AddPage(PageSize.A4);
        var pdf = builder.Build();
        var version = Version("docs/scan.pdf", "application/pdf", pdf.Length);

        var some = await Processor(new MemoryStorage { ["docs/scan.pdf"] = pdf }, new FakeOcr(new OcrStatus(Engine, null), n => n == 1 ? Scanned(1) : null), ai: false)
            .RunAsync(version, _ => { }, CancellationToken.None);
        var notes = Assert.IsType<Dictionary<string, object?>>(some.Metrics["ocr"]);
        Assert.Equal("1", notes["pages"]);
        Assert.Equal("2 (Tesseract timed out)", notes["failed"]);

        var error = await Assert.ThrowsAsync<StageFailedException>(() =>
            Processor(new MemoryStorage { ["docs/scan.pdf"] = pdf }, new FakeOcr(new OcrStatus(Engine, null), _ => null), ai: false)
                .RunAsync(version, _ => { }, CancellationToken.None));
        Assert.Equal(ProcessingStage.Ocr, error.Stage);
        Assert.Equal("OCR couldn't read any page (Tesseract timed out).", error.Message);
        Assert.False(error.Permanent);
    }

    [Fact]
    public void Catch_up_finds_jobs_that_skipped_ocr_in_the_job_notes()
    {
        using var db = ModelTests.NewContext();

        var sql = OcrCatchUpWorker.Skipped(db, 1).ToQueryString();

        Assert.Contains("'Succeeded'", sql);
        Assert.Contains("ocr", sql);
        Assert.Contains("extract", sql);
    }

    // ---- Tesseract itself (in the API's Docker image; skipped where it isn't installed) ---------

    private sealed class TesseractFactAttribute : FactAttribute
    {
        private static readonly Lazy<bool> Installed = new(() => Runs("tesseract", "--version") && Runs("pdftoppm", "-v"));

        public TesseractFactAttribute()
        {
            if (!Installed.Value) Skip = "Tesseract and pdftoppm aren't installed here (the API's Docker image has them).";
        }

        private static bool Runs(string program, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true });
                p!.WaitForExit(10_000);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A scan: a text page rendered to an image by pdftoppm, placed alone on a PDF page.</summary>
    private static async Task<(byte[] Pdf, byte[] Png)> ScanAsync()
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("COURSE SYLLABUS", 24, new PdfPoint(60, 760), bold);
        page.AddText("The course introduces data structures and algorithms.", 12, new PdfPoint(60, 710), regular);
        page.AddText("Quizzes count for thirty percent of the final grade.", 12, new PdfPoint(60, 690), regular);
        page.AddText("Ang mga mag-aaral ay inaasahang dumalo sa bawat klase.", 12, new PdfPoint(60, 650), regular);

        var dir = Directory.CreateTempSubdirectory("kbms-ocr-test-");
        try
        {
            var source = Path.Combine(dir.FullName, "text.pdf");
            await File.WriteAllBytesAsync(source, builder.Build());
            using (var render = Process.Start("pdftoppm", ["-r", "200", "-gray", "-png", "-singlefile", source, Path.Combine(dir.FullName, "scan")]))
                await render.WaitForExitAsync();
            var png = await File.ReadAllBytesAsync(Path.Combine(dir.FullName, "scan.png"));

            var scan = new PdfDocumentBuilder();
            scan.AddPage(PageSize.A4).AddPng(png, new PdfRectangle(0, 0, 595, 842));
            return (scan.Build(), png);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static TesseractOcrEngine RealEngine() => new(Options.Create(new OcrOptions()));

    [TesseractFact]
    public async Task Tesseract_reads_a_scanned_pdf_page()
    {
        var (pdf, _) = await ScanAsync();
        Assert.Empty(PdfTextExtractor.Extract(pdf, 40).Pages[0].Blocks);

        var engine = RealEngine();
        var status = await engine.StatusAsync();
        var result = await engine.ReadAsync(pdf, "application/pdf", [1]);

        Assert.StartsWith("tesseract ", status.Engine);
        var page = Assert.Single(result.Pages);
        Assert.Empty(result.Failed);
        var text = string.Join("\n", page.Blocks.Select(b => b.Text));
        Assert.Contains("COURSE SYLLABUS", text);
        Assert.Contains("data structures and algorithms", text);
        Assert.Contains("inaasahang dumalo", text);
        Assert.Equal(BlockKind.Heading, page.Blocks[0].Kind);
        Assert.True(page.Confidence > 80, $"confidence {page.Confidence}");
    }

    [TesseractFact]
    public async Task Tesseract_reads_a_photo_and_the_whole_pipeline_indexes_the_scan()
    {
        var (pdf, png) = await ScanAsync();
        var engine = RealEngine();

        var photo = await engine.ReadAsync(png, "image/png", [1]);
        Assert.Contains("thirty percent", string.Join(" ", Assert.Single(photo.Pages).Blocks.Select(b => b.Text)));

        var storage = new MemoryStorage { ["docs/scan.pdf"] = pdf };
        var version = Version("docs/scan.pdf", "application/pdf", pdf.Length);
        var result = await Processor(storage, engine).RunAsync(version, _ => { }, CancellationToken.None);

        Assert.Contains("thirty percent of the final grade", result.Pages![0].Text);
        var chunks = Chunker.FromJsonLines(Encoding.UTF8.GetString(storage[DocumentProcessor.ChunksKey(version.Id, 1, ocr: true)]));
        Assert.Contains(chunks, c => c.Text.Contains("data structures and algorithms"));
    }
}
