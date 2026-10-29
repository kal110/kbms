using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Ai;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <param name="Section">Heading path at the chunk's start, e.g. "3 Grading › 3.2 Late work".</param>
/// <param name="Tokens">Estimated tokens of <paramref name="Text"/> (see <see cref="Tokens"/>).</param>
public sealed record TextChunk(int Index, int PageStart, int PageEnd, string Section, string Text, int Tokens)
{
    /// <summary>
    /// What gets embedded: the document title and section path in front of the passage, so a chunk
    /// that says "Late work loses 10% per day" is still found by "CS 301 syllabus late policy".
    /// </summary>
    public string EmbeddingText(string title) =>
        Section.Length == 0 ? $"{title}\n\n{Text}" : $"{title}\n{Section}\n\n{Text}";
}

/// <summary>
/// Structure-first chunking (frame 25). Headings, paragraphs, list items and table rows are the
/// units; a chunk never splits inside a unit unless the unit alone is over the ceiling (then it
/// splits at sentences, and a table at rows with its header repeated).
///
/// - A heading closes the current chunk once it holds MinTokens, so sections stay apart.
/// - A chunk closes at the first unit end after TargetTokens, and before any unit that would take it past MaxTokens.
/// - A chunk that closes mid-section starts the next with the last ~OverlapTokens of whole sentences.
/// - Every chunk keeps its first and last page, and the heading path it sits under.
/// </summary>
public static partial class Chunker
{
    private sealed record Unit(BlockKind Kind, string Text, int Page, int Tokens, int Level);

    private sealed class Draft
    {
        public readonly List<Unit> Units = [];
        public string Section = "";
        public int Tokens;

        /// <summary>Holds only the previous chunk's tail so far; dropped if a new section starts.</summary>
        public bool OverlapOnly;
        public bool HasBody => Units.Any(u => u.Kind != BlockKind.Heading);
    }

    public static IReadOnlyList<TextChunk> Split(ExtractedText text, ChunkingOptions o)
    {
        var max = Math.Max(o.MaxTokens, 64);
        var target = Math.Clamp(o.TargetTokens, 32, max);
        var overlap = Math.Clamp(o.OverlapTokens, 0, target / 2);

        var chunks = new List<TextChunk>();
        var headings = new List<(int Level, string Text)>();
        var draft = new Draft();

        void Flush(bool carryOverlap)
        {
            if (!draft.HasBody)
            {
                // Headings with nothing under them yet move on to the next chunk.
                return;
            }
            var body = string.Join("\n\n", draft.Units.Select(u => u.Text));
            chunks.Add(new TextChunk(chunks.Count, draft.Units.Min(u => u.Page), draft.Units.Max(u => u.Page), draft.Section, body, Tokens.Estimate(body)));

            var tail = carryOverlap && overlap > 0 ? Tail(draft.Units, overlap) : null;
            draft = new Draft { Section = SectionPath(headings) };
            if (tail is not null)
            {
                draft.Units.Add(tail);
                draft.Tokens = tail.Tokens;
                draft.OverlapOnly = true;
            }
        }

        void Add(Unit unit)
        {
            if (draft.Units.Count == 0 || !draft.HasBody) draft.Section = SectionPath(headings);
            draft.Units.Add(unit);
            draft.Tokens += unit.Tokens;
            if (unit.Kind != BlockKind.Heading) draft.OverlapOnly = false;
        }

        foreach (var unit in Units(text, max - overlap))
        {
            if (unit.Kind == BlockKind.Heading)
            {
                if (draft.OverlapOnly)
                {
                    // The previous section's tail doesn't belong in front of a new heading.
                    draft = new Draft();
                }
                if (draft.Tokens >= o.MinTokens && draft.HasBody) Flush(carryOverlap: false);
                // A new heading replaces those at its level and below.
                headings.RemoveAll(h => h.Level >= unit.Level);
                headings.Add((unit.Level, unit.Text));
                // A heading always leads its own content, never trails the previous chunk.
                if (!draft.HasBody) draft.Units.RemoveAll(u => u.Kind == BlockKind.Heading && u.Level >= unit.Level);
                Add(unit);
                continue;
            }

            if (draft.HasBody && draft.Tokens + unit.Tokens > max) Flush(carryOverlap: true);
            Add(unit);
            if (draft.Tokens >= target) Flush(carryOverlap: true);
        }
        Flush(carryOverlap: false);
        return chunks;
    }

    /// <summary>Blocks as units no larger than <paramref name="limit"/> tokens.</summary>
    private static IEnumerable<Unit> Units(ExtractedText text, int limit)
    {
        foreach (var page in text.Pages)
        {
            foreach (var block in page.Blocks)
            {
                var tokens = Tokens.Estimate(block.Text);
                if (block.Kind == BlockKind.Heading)
                {
                    yield return new Unit(block.Kind, block.Text, page.Number, tokens, Math.Max(block.Level, 1));
                }
                else if (tokens <= limit)
                {
                    yield return new Unit(block.Kind, block.Text, page.Number, tokens, 0);
                }
                else if (block.Kind == BlockKind.Table)
                {
                    foreach (var part in SplitTable(block.Text, limit))
                        yield return new Unit(BlockKind.Table, part, page.Number, Tokens.Estimate(part), 0);
                }
                else
                {
                    foreach (var part in SplitProse(block.Text, limit))
                        yield return new Unit(block.Kind, part, page.Number, Tokens.Estimate(part), 0);
                }
            }
        }
    }

    /// <summary>Rows in order, each part starting with the header row so its columns stay named.</summary>
    private static IEnumerable<string> SplitTable(string table, int limit)
    {
        var rows = table.Split('\n');
        var header = rows[0];
        var part = new StringBuilder(header);
        var tokens = Tokens.Estimate(header);
        foreach (var row in rows.Skip(1))
        {
            var t = Tokens.Estimate(row);
            if (tokens + t > limit && part.Length > header.Length)
            {
                yield return part.ToString();
                part.Clear().Append(header);
                tokens = Tokens.Estimate(header);
            }
            part.Append('\n').Append(row);
            tokens += t;
        }
        yield return part.ToString();
    }

    /// <summary>Sentences packed up to the limit; a sentence longer than the limit splits at words.</summary>
    private static IEnumerable<string> SplitProse(string text, int limit)
    {
        var part = new StringBuilder();
        var tokens = 0;
        foreach (var sentence in Sentences(text).SelectMany(s => Tokens.Estimate(s) <= limit ? [s] : SplitWords(s, limit)))
        {
            var t = Tokens.Estimate(sentence);
            if (tokens + t > limit && part.Length > 0)
            {
                yield return part.ToString();
                part.Clear();
                tokens = 0;
            }
            if (part.Length > 0) part.Append(' ');
            part.Append(sentence);
            tokens += t;
        }
        if (part.Length > 0) yield return part.ToString();
    }

    private static IEnumerable<string> SplitWords(string sentence, int limit)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var part = new List<string>();
        foreach (var word in words)
        {
            part.Add(word);
            if (Tokens.Estimate(string.Join(' ', part)) >= limit)
            {
                yield return string.Join(' ', part);
                part.Clear();
            }
        }
        if (part.Count > 0) yield return string.Join(' ', part);
    }

    /// <summary>The last whole sentences of the chunk's body, up to <paramref name="budget"/> tokens.</summary>
    private static Unit? Tail(List<Unit> units, int budget)
    {
        // Only prose overlaps; a chunk ending in a table or heading starts the next one clean.
        var last = units[^1];
        if (last.Kind is not (BlockKind.Paragraph or BlockKind.ListItem)) return null;

        var picked = new List<string>();
        var tokens = 0;
        foreach (var sentence in Sentences(last.Text).Reverse())
        {
            var t = Tokens.Estimate(sentence);
            if (tokens + t > budget) break;
            picked.Insert(0, sentence);
            tokens += t;
        }
        if (picked.Count == 0)
        {
            // One long sentence: its last words instead.
            var words = last.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var take = Math.Max(1, (int)(budget / 1.3));
            picked.Add("…" + string.Join(' ', words.Skip(Math.Max(0, words.Length - take))));
        }
        var text = string.Join(' ', picked);
        return text == last.Text && units.Count == 1 ? null : new Unit(last.Kind, text, last.Page, Tokens.Estimate(text), 0);
    }

    private static string SectionPath(List<(int Level, string Text)> headings) =>
        string.Join(" › ", headings.Select(h => h.Text.Length <= 80 ? h.Text : h.Text[..77] + "…"));

    public static IEnumerable<string> Sentences(string text) =>
        SentenceEnd().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0);

    /// <summary>chunks/p{n}.jsonl: one chunk per line, the artifact the Embed stage reads (frame 8).</summary>
    public static byte[] ToJsonLines(IEnumerable<TextChunk> chunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks) sb.Append(JsonSerializer.Serialize(chunk, JsonOptions)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static IReadOnlyList<TextChunk> FromJsonLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<TextChunk>(l, JsonOptions)!).ToList();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // A sentence ends at . ! ? (optionally closed by a quote or bracket) before whitespace and an
    // upper-case letter, digit or opening quote. "e.g. the" and "Sec. 4.2" stay together.
    [GeneratedRegex(@"(?<=(?<!\b(?:e\.g|i\.e|etc|vs|No|Sec|Art|Mr|Ms|Dr|Prof))[.!?][""')\]]?)\s+(?=[A-Z0-9""'(\[])")]
    private static partial Regex SentenceEnd();
}

/// <summary>
/// Token estimate for sizing chunks. Embedding models use different tokenizers (bge-m3 is
/// XLM-RoBERTa's), so this deliberately leans high: the larger of chars/4 and words×1.3.
/// </summary>
public static partial class Tokens
{
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var words = Word().Count(text);
        return (int)Math.Ceiling(Math.Max(text.Length / 4.0, words * 1.3));
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();
}
