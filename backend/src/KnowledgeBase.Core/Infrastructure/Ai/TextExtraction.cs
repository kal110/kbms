using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PdfTextBlock = UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock;

namespace KnowledgeBase.Core.Infrastructure.Ai;

// Extract (frame 25, steps 1–3): the original becomes pages of typed blocks, kept page by page so
// every chunk can cite its pages. Headings, list items and tables are recognized here because the
// chunker splits on them first.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Table,
}

/// <param name="Level">Heading level, 1 = largest. 0 for other blocks.</param>
public sealed record PageBlock(BlockKind Kind, string Text, int Level = 0);

/// <param name="NeedsOcr">Little or no text but images: a scan, flagged for OCR (frame 8, step 7).</param>
public sealed record ExtractedPage(int Number, IReadOnlyList<PageBlock> Blocks, int Chars, bool HasImages, bool NeedsOcr);

public sealed record ExtractedText(string Extractor, IReadOnlyList<ExtractedPage> Pages)
{
    public int Chars => Pages.Sum(p => p.Chars);
    public int[] LowTextPages => Pages.Where(p => p.NeedsOcr).Select(p => p.Number).ToArray();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>pages-p{n}.jsonl: a header line with the extractor, then one JSON object per page.</summary>
    public byte[] ToJsonLines()
    {
        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(new { extractor = Extractor, pages = Pages.Count }, Json)).Append('\n');
        foreach (var page in Pages) sb.Append(JsonSerializer.Serialize(page, Json)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static ExtractedText FromJsonLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = JsonDocument.Parse(lines[0]).RootElement;
        var pages = lines.Skip(1).Select(l => JsonSerializer.Deserialize<ExtractedPage>(l, Json)!).ToList();
        return new ExtractedText(header.GetProperty("extractor").GetString() ?? "unknown", pages);
    }
}

/// <summary>Picks the extractor for a content type; null when there is none (images need OCR).</summary>
public static class TextExtractors
{
    public static bool CanExtract(string contentType) => contentType is
        "application/pdf" or "text/plain" or "text/markdown" or "text/csv" or DocxTextExtractor.ContentType;

    public static ExtractedText Extract(string contentType, byte[] bytes, int lowTextChars) => contentType switch
    {
        "application/pdf" => PdfTextExtractor.Extract(bytes, lowTextChars),
        DocxTextExtractor.ContentType => DocxTextExtractor.Extract(bytes),
        "text/csv" => PlainTextExtractor.ExtractCsv(Encoding.UTF8.GetString(bytes)),
        _ => PlainTextExtractor.Extract(Encoding.UTF8.GetString(bytes)),
    };
}

/// <summary>
/// PDF text with layout: PdfPig words → Docstrum blocks → reading order (handles two columns),
/// then running headers/footers and page numbers are dropped, hyphenated line breaks are joined,
/// and blocks are typed by font size, weight and shape.
/// </summary>
public static partial class PdfTextExtractor
{
    public const string Name = "pdfpig";

    private sealed record RawLine(string Text, double Size, double BoldShare, double Top, double Bottom, IReadOnlyList<string> Cells);

    private sealed record RawBlock(IReadOnlyList<RawLine> Lines)
    {
        public double Size => Lines.Count == 0 ? 0 : Lines.Average(l => l.Size);
        public double BoldShare => Lines.Count == 0 ? 0 : Lines.Average(l => l.BoldShare);
        public int Chars => Lines.Sum(l => l.Text.Length);
    }

    private sealed record RawPage(int Number, double Height, List<RawBlock> Blocks, bool HasImages);

    /// <exception cref="UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException">Needs a password to open.</exception>
    public static ExtractedText Extract(byte[] pdf, int lowTextChars)
    {
        var raw = new List<RawPage>();
        using (var document = PdfDocument.Open(pdf))
        {
            foreach (var page in document.GetPages())
                raw.Add(ReadPage(page));
        }

        RemoveRunningLines(raw);

        // The body size is the most common letter size; headings are noticeably larger or bold.
        var body = raw.SelectMany(p => p.Blocks).SelectMany(b => b.Lines)
            .GroupBy(l => Math.Round(l.Size * 2) / 2)
            .OrderByDescending(g => g.Sum(l => l.Text.Length))
            .Select(g => g.Key)
            .FirstOrDefault(10);
        var headingSizes = raw.SelectMany(p => p.Blocks)
            .Where(b => IsHeading(b, body) && b.Size >= body * 1.15)
            .Select(b => Math.Round(b.Size))
            .Distinct()
            .OrderByDescending(s => s)
            .ToList();

        var pages = raw.Select(p =>
        {
            var blocks = p.Blocks.SelectMany(b => Classify(b, body, headingSizes)).Where(b => b.Text.Length > 0).ToList();
            var chars = blocks.Sum(b => b.Text.Length);
            return new ExtractedPage(p.Number, blocks, chars, p.HasImages, NeedsOcr: chars < lowTextChars && (p.HasImages || chars == 0));
        }).ToList();
        return new ExtractedText(Name, pages);
    }

    private static RawPage ReadPage(Page page)
    {
        var hasImages = false;
        try { hasImages = page.GetImages().Any(); }
        catch (Exception) { /* a broken image stream doesn't stop the text */ }

        var letters = page.Letters;
        if (letters.Count == 0) return new RawPage(page.Number, page.Height, [], hasImages);

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
        var tables = TakeTables(words);

        var result = new List<RawBlock>();
        if (words.Count > 0)
        {
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            IEnumerable<PdfTextBlock> ordered;
            try { ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks); }
            catch (Exception) { ordered = blocks; }

            foreach (var block in ordered)
            {
                var lines = block.TextLines
                    .Select(line => ToRawLine(line.Words, Cells(line.Words).Select(c => c.Text).ToList()))
                    .Where(l => l.Text.Length > 0);
                result.AddRange(SplitAtStyleChanges(lines).Select(part => new RawBlock(part)));
            }
        }

        // Each table goes before the first block that starts below it.
        foreach (var table in tables)
        {
            var at = result.FindIndex(b => b.Lines[0].Top < table.Lines[0].Top);
            result.Insert(at < 0 ? result.Count : at, table);
        }
        return new RawPage(page.Number, page.Height, result, hasImages);
    }

    private static RawLine ToRawLine(IReadOnlyList<Word> words, IReadOnlyList<string> cells)
    {
        var letters = words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var size = letters.Count == 0 ? 0 : Median(letters.Select(l => l.PointSize));
        var bold = letters.Count == 0 ? 0 : letters.Count(l => IsBoldFont(l.FontName)) / (double)letters.Count;
        var text = Normalize(string.Join(" ", words.Select(w => w.Text)));
        var top = words.Count == 0 ? 0 : words.Max(w => w.BoundingBox.Top);
        var bottom = words.Count == 0 ? 0 : words.Min(w => w.BoundingBox.Bottom);
        return new RawLine(text, size, bold, top, bottom, cells);
    }

    /// <summary>
    /// Docstrum can glue a heading to the paragraph under it on a sparse page. A line whose letter
    /// size differs by more than 15%, or a regular line after all-bold ones, starts a new block.
    /// </summary>
    private static IEnumerable<List<RawLine>> SplitAtStyleChanges(IEnumerable<RawLine> lines)
    {
        var part = new List<RawLine>();
        foreach (var line in lines)
        {
            if (part.Count > 0)
            {
                var previous = part[^1];
                var resized = Math.Abs(line.Size - previous.Size) > Math.Min(line.Size, previous.Size) * 0.15;
                var unbolded = line.BoldShare < 0.2 && part.All(l => l.BoldShare >= 0.8);
                if (resized || unbolded)
                {
                    yield return part;
                    part = [];
                }
            }
            part.Add(line);
        }
        if (part.Count > 0) yield return part;
    }

    private sealed record Cell(string Text, double Left, double Right);

    /// <summary>Wide gaps between words mark table cells: more than three average letter widths.</summary>
    private static List<Cell> Cells(IReadOnlyList<Word> words)
    {
        var letters = words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var width = letters.Count == 0 ? 0 : letters.Average(l => l.BoundingBox.Width);
        var cells = new List<Cell>();
        var start = 0;
        for (var i = 1; i <= words.Count; i++)
        {
            if (i < words.Count && !(width > 0 && words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right > Math.Max(width * 3, 12))) continue;
            var text = Normalize(string.Join(" ", words.Skip(start).Take(i - start).Select(w => w.Text)));
            if (text.Length > 0) cells.Add(new Cell(text, words[start].BoundingBox.Left, words[i - 1].BoundingBox.Right));
            start = i;
        }
        return cells;
    }

    private sealed record VisualLine(List<Word> Words, List<Cell> Cells, double Top, double Bottom)
    {
        public double Height => Top - Bottom;
    }

    /// <summary>
    /// Docstrum groups each column of a table into its own block, which reads the table column by
    /// column. So tables are taken out of <paramref name="words"/> first: words on one baseline form
    /// a line, and two or more nearby lines with three or more cells in the same columns form a
    /// table, row by row. A tight line whose cells all sit inside the columns continues the row above
    /// (a wrapped cell). Prose set in three columns has long cells and is left alone.
    /// </summary>
    private static List<RawBlock> TakeTables(List<Word> words)
    {
        var lines = VisualLines(words);
        var tables = new List<RawBlock>();
        var taken = new HashSet<Word>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Cells.Count < 3) continue;
            var columns = lines[i].Cells.Select(c => (c.Left, c.Right)).ToList();
            var rows = new List<(List<VisualLine> Lines, string[] Cells)> { ([lines[i]], lines[i].Cells.Select(c => c.Text).ToArray()) };
            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                var line = lines[j];
                var above = rows[^1].Lines[^1];
                var gap = above.Bottom - line.Top;
                var map = ToColumns(line.Cells, columns);
                if (map is null) break;

                string[] cells;
                if (line.Cells.Count >= 3 && gap <= Math.Max(above.Height, line.Height) * 2)
                {
                    cells = new string[columns.Count];
                    rows.Add(([line], cells));
                }
                else if (gap <= Math.Max(above.Height, line.Height) * 0.8)
                {
                    cells = rows[^1].Cells;
                    rows[^1].Lines.Add(line);
                }
                else break;

                for (var c = 0; c < line.Cells.Count; c++)
                {
                    var k = map[c];
                    cells[k] = cells[k] is { Length: > 0 } before ? before + " " + line.Cells[c].Text : line.Cells[c].Text;
                    columns[k] = (Math.Min(columns[k].Left, line.Cells[c].Left), Math.Max(columns[k].Right, line.Cells[c].Right));
                }
            }

            var used = rows.SelectMany(r => r.Lines).ToList();
            if (rows.Count < 2 || used.SelectMany(l => l.Cells).Average(c => c.Text.Length) > 32) continue;

            tables.Add(new RawBlock(rows.Select(r =>
            {
                var rowWords = r.Lines.SelectMany(l => l.Words).ToList();
                var cells = r.Cells.Select(c => c ?? "").ToList();
                var line = ToRawLine(rowWords, cells);
                return line with { Text = string.Join(" ", cells.Where(c => c.Length > 0)) };
            }).ToList()));
            foreach (var word in used.SelectMany(l => l.Words)) taken.Add(word);
            i = j - 1;
        }
        words.RemoveAll(taken.Contains);
        return tables;
    }

    /// <summary>Lines top to bottom: horizontal words whose baselines agree, left to right.</summary>
    private static List<VisualLine> VisualLines(IEnumerable<Word> words)
    {
        static double Baseline(Word w) => w.Letters[0].StartBaseLine.Y;

        var groups = new List<List<Word>>();
        foreach (var word in words
            .Where(w => w.TextOrientation == TextOrientation.Horizontal && w.Letters.Count > 0)
            .OrderByDescending(Baseline))
        {
            var line = groups.Count == 0 ? null : groups[^1];
            if (line is not null && Math.Abs(Baseline(line[0]) - Baseline(word)) <= Math.Max(word.BoundingBox.Height, 4) * 0.3) line.Add(word);
            else groups.Add([word]);
        }
        return groups.Select(g =>
        {
            var sorted = g.OrderBy(w => w.BoundingBox.Left).ToList();
            return new VisualLine(sorted, Cells(sorted), sorted.Max(w => w.BoundingBox.Top), sorted.Min(w => w.BoundingBox.Bottom));
        }).ToList();
    }

    /// <summary>The column of each cell, or null when a cell spans no column, several, or one already used.</summary>
    private static int[]? ToColumns(List<Cell> cells, List<(double Left, double Right)> columns)
    {
        var map = new int[cells.Count];
        var seen = new HashSet<int>();
        for (var c = 0; c < cells.Count; c++)
        {
            var hits = Enumerable.Range(0, columns.Count)
                .Where(k => cells[c].Left < columns[k].Right + 2 && cells[c].Right > columns[k].Left - 2)
                .ToList();
            if (hits.Count != 1 || !seen.Add(hits[0])) return null;
            map[c] = hits[0];
        }
        return map;
    }

    /// <summary>
    /// Running headers and footers repeat in the top or bottom 8% of most pages; page numbers
    /// ("12", "Page 3 of 10", "- 4 -") sit there too. Digits are masked when comparing.
    /// </summary>
    private static void RemoveRunningLines(List<RawPage> pages)
    {
        bool InMargin(RawPage p, RawLine l) => l.Bottom >= p.Height * 0.92 || l.Top <= p.Height * 0.08;

        var counts = new Dictionary<string, int>();
        if (pages.Count >= 3)
        {
            foreach (var page in pages)
            {
                foreach (var key in page.Blocks.SelectMany(b => b.Lines).Where(l => InMargin(page, l)).Select(l => MaskDigits(l.Text)).Distinct())
                    counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        var threshold = Math.Max(3, (int)Math.Ceiling(pages.Count * 0.5));

        foreach (var page in pages)
        {
            for (var i = page.Blocks.Count - 1; i >= 0; i--)
            {
                var kept = page.Blocks[i].Lines
                    .Where(l => !(InMargin(page, l) && (PageNumber().IsMatch(l.Text) || counts.GetValueOrDefault(MaskDigits(l.Text)) >= threshold)))
                    .ToList();
                if (kept.Count == 0) page.Blocks.RemoveAt(i);
                else if (kept.Count != page.Blocks[i].Lines.Count) page.Blocks[i] = new RawBlock(kept);
            }
        }
    }

    private static bool IsHeading(RawBlock block, double body)
    {
        var chars = block.Chars;
        if (block.Lines.Count > 3 || chars > 160 || chars < 2) return false;
        var text = string.Join(" ", block.Lines.Select(l => l.Text));
        if (block.Size >= body * 1.15) return true;
        if (block.BoldShare >= 0.8 && chars <= 100 && !text.EndsWith('.')) return true;
        return chars <= 80 && IsMostlyUpper(text) && !text.EndsWith('.');
    }

    private static IEnumerable<PageBlock> Classify(RawBlock block, double body, List<double> headingSizes)
    {
        // Two or more consecutive lines with several cells read as a table, row by row.
        var tabular = block.Lines.Count(l => l.Cells.Count >= 3);
        if (block.Lines.Count >= 2 && tabular * 2 >= block.Lines.Count)
        {
            var rows = block.Lines.Select(l => string.Join(" | ", l.Cells));
            yield return new PageBlock(BlockKind.Table, string.Join("\n", rows));
            yield break;
        }

        if (IsHeading(block, body))
        {
            var index = headingSizes.IndexOf(Math.Round(block.Size));
            // Larger sizes rank first; bold or capitals at body size sit one level below them.
            var level = index >= 0 ? Math.Min(index + 1, 3) : Math.Min(headingSizes.Count + 1, 3);
            yield return new PageBlock(BlockKind.Heading, JoinLines(block.Lines.Select(l => l.Text)), level);
            yield break;
        }

        // Docstrum often groups a whole list into one block; each marker starts an item.
        var current = new List<string>();
        var currentIsItem = false;
        foreach (var line in block.Lines)
        {
            if (ListMarker().IsMatch(line.Text) && current.Count > 0)
            {
                yield return new PageBlock(currentIsItem ? BlockKind.ListItem : BlockKind.Paragraph, JoinLines(current));
                current.Clear();
            }
            if (current.Count == 0) currentIsItem = ListMarker().IsMatch(line.Text);
            current.Add(line.Text);
        }
        if (current.Count > 0)
            yield return new PageBlock(currentIsItem ? BlockKind.ListItem : BlockKind.Paragraph, JoinLines(current));
    }

    /// <summary>
    /// Joins wrapped lines with spaces. A line ending in a hyphen joins the next without one and
    /// keeps the hyphen: Word, which writes most of these documents, doesn't hyphenate on its own,
    /// so "student-" + "led" is a compound word far more often than a split one.
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0)
            {
                if (!(sb[^1] == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))) sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    public static string Normalize(string text) => Whitespace().Replace(text.Replace(' ', ' ').Replace("­", ""), " ").Trim();

    private static bool IsBoldFont(string? font) =>
        font is not null && (font.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Semibold", StringComparison.OrdinalIgnoreCase));

    private static bool IsMostlyUpper(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.Count(char.IsUpper) >= letters.Count * 0.8;
    }

    private static string MaskDigits(string text) => Digits().Replace(text.ToLowerInvariant(), "#");

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^(page\s*)?[-–—]?\s*\d{1,4}\s*([-–—]|(of|/)\s*\d{1,4})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"^([•◦▪▫●○■□‣∙·*–-]|\(?([0-9]{1,2}|[a-zA-Z]|[ivxIVX]{1,4})[.)])\s+")]
    internal static partial Regex ListMarker();
}

/// <summary>
/// .docx without a library: paragraphs, heading styles (Title, Heading1–3), numbered/bulleted
/// paragraphs and tables from word/document.xml. Pages follow Word's saved page breaks.
/// </summary>
public static class DocxTextExtractor
{
    public const string Name = "docx";
    public const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static ExtractedText Extract(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
        var xml = new XmlDocument { XmlResolver = null };
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            xml.Load(reader);

        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("w", W);
        var body = xml.SelectSingleNode("/w:document/w:body", ns) ?? throw new InvalidDataException("Not a Word document.");

        var pages = new List<List<PageBlock>> { new() };
        // Content controls (w:sdt: tables of contents, template fields) wrap paragraphs and tables too.
        foreach (XmlNode node in body.SelectNodes("w:p|w:tbl|w:sdt/w:sdtContent/w:p|w:sdt/w:sdtContent/w:tbl", ns)!)
        {
            if (node.LocalName == "p")
            {
                if (node.SelectSingleNode(".//w:lastRenderedPageBreak|.//w:br[@w:type='page']", ns) is not null && pages[^1].Count > 0)
                    pages.Add([]);
                var text = PdfTextExtractor.Normalize(string.Concat(node.SelectNodes(".//w:t|.//w:tab", ns)!.Cast<XmlNode>()
                    .Select(t => t.LocalName == "tab" ? " " : t.InnerText)));
                if (text.Length == 0) continue;

                var style = node.SelectSingleNode("w:pPr/w:pStyle/@w:val", ns)?.Value ?? "";
                var level = style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1
                    : style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var h) ? Math.Clamp(h, 1, 3)
                    : 0;
                var kind = level > 0 ? BlockKind.Heading
                    : node.SelectSingleNode("w:pPr/w:numPr", ns) is not null ? BlockKind.ListItem
                    : BlockKind.Paragraph;
                pages[^1].Add(new PageBlock(kind, text, level));
            }
            else if (node.LocalName == "tbl")
            {
                var rows = node.SelectNodes("w:tr", ns)!.Cast<XmlNode>()
                    .Select(tr => string.Join(" | ", tr.SelectNodes("w:tc", ns)!.Cast<XmlNode>()
                        .Select(tc => PdfTextExtractor.Normalize(string.Join(" ", tc.SelectNodes(".//w:t", ns)!.Cast<XmlNode>().Select(t => t.InnerText))))))
                    .Where(r => r.Replace("|", "").Trim().Length > 0)
                    .ToList();
                if (rows.Count > 0) pages[^1].Add(new PageBlock(BlockKind.Table, string.Join("\n", rows)));
            }
        }

        return new ExtractedText(Name, pages.Select((blocks, i) =>
            new ExtractedPage(i + 1, blocks, blocks.Sum(b => b.Text.Length), HasImages: false, NeedsOcr: false)).ToList());
    }
}

/// <summary>Plain text and Markdown: blank lines separate paragraphs, "#" lines are headings.</summary>
public static partial class PlainTextExtractor
{
    public const string Name = "text";

    public static ExtractedText Extract(string text)
    {
        var blocks = new List<PageBlock>();
        foreach (var part in BlankLines().Split(text.Replace("\r\n", "\n")))
        {
            var lines = part.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) continue;

            var heading = MarkdownHeading().Match(lines[0]);
            if (heading.Success)
            {
                blocks.Add(new PageBlock(BlockKind.Heading, PdfTextExtractor.Normalize(heading.Groups[2].Value), Math.Min(heading.Groups[1].Length, 3)));
                lines.RemoveAt(0);
                if (lines.Count == 0) continue;
            }

            if (lines.All(l => l.TrimStart().StartsWith('|')))
            {
                blocks.Add(new PageBlock(BlockKind.Table, string.Join("\n", lines.Where(l => !TableRule().IsMatch(l))
                    .Select(l => string.Join(" | ", l.Trim().Trim('|').Split('|').Select(c => c.Trim()))))));
                continue;
            }

            var items = new List<string>();
            foreach (var line in lines)
            {
                if (PdfTextExtractor.ListMarker().IsMatch(line.TrimStart()) && items.Count > 0)
                {
                    blocks.Add(Block(items));
                    items.Clear();
                }
                items.Add(line.Trim());
            }
            blocks.Add(Block(items));
        }
        return Single(blocks);

        static PageBlock Block(List<string> lines) =>
            new(PdfTextExtractor.ListMarker().IsMatch(lines[0]) ? BlockKind.ListItem : BlockKind.Paragraph, PdfTextExtractor.JoinLines(lines));
    }

    /// <summary>CSV: the header row, then every row as a table line.</summary>
    public static ExtractedText ExtractCsv(string csv)
    {
        var rows = csv.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Select(l => string.Join(" | ", l.Split(',').Select(c => c.Trim().Trim('"'))))
            .ToList();
        return Single(rows.Count == 0 ? [] : [new PageBlock(BlockKind.Table, string.Join("\n", rows))]);
    }

    private static ExtractedText Single(List<PageBlock> blocks) =>
        new(Name, [new ExtractedPage(1, blocks, blocks.Sum(b => b.Text.Length), HasImages: false, NeedsOcr: false)]);

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")]
    private static partial Regex TableRule();
}
