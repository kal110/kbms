using System.Globalization;
using System.Text.RegularExpressions;
using KnowledgeBase.Core.Infrastructure.Ai;

namespace KnowledgeBase.Core.Infrastructure.Ocr;

/// <summary>
/// Tesseract's TSV (a row per page, block, paragraph, line and word, with boxes in pixels and a
/// confidence per word) → the typed blocks the extractors produce. Tesseract's paragraphs become
/// paragraphs; short ones set noticeably taller than the page's body lines, or short all-capital
/// ones, become headings; a list marker starts a list item, as in <see cref="PdfTextExtractor"/>.
/// Page numbers in the top and bottom margins are dropped.
/// </summary>
public static partial class TesseractTsv
{
    private sealed record Line(int Block, int Paragraph, double Top, double Height)
    {
        public List<string> Words { get; } = [];
        public List<double> Confidences { get; } = [];
        public string Text => PdfTextExtractor.Normalize(string.Join(' ', Words));
    }

    public static OcrPage ToPage(int number, string tsv, int minConfidence)
    {
        double pageHeight = 0;
        var lines = new Dictionary<(int Block, int Paragraph, int Line), Line>();
        var order = new List<Line>();

        foreach (var row in tsv.Split('\n'))
        {
            // level page_num block_num par_num line_num word_num left top width height conf text
            var c = row.TrimEnd('\r').Split('\t');
            if (c.Length < 11 || !int.TryParse(c[0], out var level)) continue;
            var key = (Int(c[2]), Int(c[3]), Int(c[4]));
            switch (level)
            {
                case 1:
                    pageHeight = Number(c[9]);
                    break;
                case 4:
                    var line = new Line(key.Item1, key.Item2, Number(c[7]), Number(c[9]));
                    lines[key] = line;
                    order.Add(line);
                    break;
                case 5:
                    var text = c.Length > 11 ? c[11].Trim() : "";
                    var confidence = Number(c[10]);
                    if (text.Length == 0 || confidence < minConfidence || !lines.TryGetValue(key, out var owner)) break;
                    owner.Words.Add(text);
                    owner.Confidences.Add(confidence);
                    break;
            }
        }

        var kept = order.Where(l => l.Text.Length > 0 && !IsPageNumber(l, pageHeight)).ToList();
        var body = BodyHeight(kept);
        var blocks = kept
            .GroupBy(l => (l.Block, l.Paragraph))
            .SelectMany(g => Classify(g.ToList(), body))
            .Where(b => b.Text.Length > 0)
            .ToList();
        var confidences = kept.SelectMany(l => l.Confidences).ToList();
        return new OcrPage(number, blocks, confidences.Count, confidences.Count == 0 ? 0 : Math.Round(confidences.Average(), 1));
    }

    private static IEnumerable<PageBlock> Classify(List<Line> paragraph, double body)
    {
        var text = string.Join(" ", paragraph.Select(l => l.Text));
        if (paragraph.Count <= 3 && text.Length is >= 2 and <= 160 && body > 0)
        {
            var height = paragraph.Average(l => l.Height);
            // Line boxes include ascenders and descenders, so a heading's box grows less than its type size.
            var level = height >= body * 1.6 ? 1
                : height >= body * 1.2 ? 2
                : text.Length <= 80 && IsMostlyUpper(text) && !text.EndsWith('.') ? 3
                : 0;
            if (level > 0)
            {
                yield return new PageBlock(BlockKind.Heading, PdfTextExtractor.JoinLines(paragraph.Select(l => l.Text)), level);
                yield break;
            }
        }

        // Tesseract often reads a whole list as one paragraph; each marker starts an item.
        var current = new List<string>();
        var currentIsItem = false;
        foreach (var line in paragraph.Select(l => l.Text))
        {
            if (PdfTextExtractor.ListMarker().IsMatch(line) && current.Count > 0)
            {
                yield return new PageBlock(currentIsItem ? BlockKind.ListItem : BlockKind.Paragraph, PdfTextExtractor.JoinLines(current));
                current.Clear();
            }
            if (current.Count == 0) currentIsItem = PdfTextExtractor.ListMarker().IsMatch(line);
            current.Add(line);
        }
        if (current.Count > 0)
            yield return new PageBlock(currentIsItem ? BlockKind.ListItem : BlockKind.Paragraph, PdfTextExtractor.JoinLines(current));
    }

    /// <summary>The line height most of the page's text is set in (median weighted by characters).</summary>
    private static double BodyHeight(List<Line> lines)
    {
        var total = lines.Sum(l => l.Text.Length);
        var seen = 0;
        foreach (var line in lines.OrderBy(l => l.Height))
        {
            seen += line.Text.Length;
            if (seen * 2 >= total) return line.Height;
        }
        return 0;
    }

    private static bool IsPageNumber(Line line, double pageHeight) =>
        pageHeight > 0
        && (line.Top <= pageHeight * 0.08 || line.Top + line.Height >= pageHeight * 0.92)
        && PageNumber().IsMatch(line.Text);

    private static bool IsMostlyUpper(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.Count(char.IsUpper) >= letters.Count * 0.8;
    }

    private static int Int(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    // The same page-number shapes PdfTextExtractor drops: "12", "Page 3 of 10", "- 4 -".
    [GeneratedRegex(@"^(page\s*)?[-–—]?\s*\d{1,4}\s*([-–—]|(of|/)\s*\d{1,4})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();
}
