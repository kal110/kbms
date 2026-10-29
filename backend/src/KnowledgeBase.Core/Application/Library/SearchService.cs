using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>
/// Library search over the documents the caller can see (frames 1, 5), in two modes.
/// <list type="bullet">
/// <item>Smart (default): PostgreSQL full-text on title, number and description with prefix
/// matching, trigram similarity on the title for typos, and — once the AI index is ready — passages
/// of the current version close in meaning to the query, fused by reciprocal rank.</item>
/// <item>Exact: the phrase as typed (case and spacing aside) in the title, number, description or
/// the extracted text of the current version (version_pages). It needs no AI services.</item>
/// </list>
/// PostgreSQL stays the authority: a meaning match only counts for a document the caller can see
/// through every filter, on its current version.
/// </summary>
public sealed partial class SearchService(KbDbContext db, ICurrentUser currentUser, IAccessEvaluator access, ISemanticSearch semantic)
{
    /// <summary>
    /// Keyword results that take part in fusion with the meaning matches; smart results that mix
    /// both stop there (a search this broad needs a filter, not page 11).
    /// </summary>
    public const int FusionWindow = 200;

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var q = (query.Q ?? "").Trim();
        if (q.Length > 200) throw new ValidationAppException("q", "Keep the search under 200 characters.");
        var (mode, phrase) = ParseMode(q, query.Mode);

        var docs = access.VisibleDocuments(me).AsNoTracking();
        if (query.TypeId is { } typeId) docs = docs.Where(d => d.DocumentTypeId == typeId);
        if (query.YearId is { } yearId) docs = docs.Where(d => d.AcademicYearId == yearId);
        if (query.TermId is { } termId) docs = docs.Where(d => d.AcademicTermId == termId);
        if (query.State is { } state) docs = docs.WhereState(db, state);
        if (query.FolderId is { } folderId)
        {
            var path = await db.Folders.AsNoTracking().Where(f => f.Id == folderId).Select(f => f.Path).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Folder", folderId);
            docs = docs.Where(d => d.Folder.Path.StartsWith(path));
        }

        var page = new PageRequest(query.Page, query.PageSize);

        if (phrase.Length == 0)
        {
            // No words: the most recently updated documents, so the page is never empty.
            var recent = await docs.OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id).ToRows(db).ToPagedAsync(page, ct);
            return new SearchResult(q, recent.Items.Select(r => new SearchHit(r.ToItem(), Snippet(r.Description), 0)).ToList(),
                recent.Total, recent.Page, recent.PageSize, Semantic: false, mode);
        }

        return mode == SearchModes.Exact
            ? await ExactAsync(docs, q, phrase, page, ct)
            : await SmartAsync(me, docs, q, phrase, query, page, ct);
    }

    private async Task<SearchResult> SmartAsync(
        UserContext me, IQueryable<Document> docs, string q, string phrase, SearchQuery query, PageRequest page, CancellationToken ct)
    {
        var found = await semantic.FindDocumentsAsync(me, phrase, new SemanticFilter(query.FolderId, query.TypeId, query.YearId, query.TermId), ct);
        var meaning = new Dictionary<Guid, SemanticMatch>();
        if (found.Matches.Count > 0)
        {
            var ids = found.Matches.Select(m => m.DocumentId).ToArray();
            var current = await docs.Where(d => ids.Contains(d.Id))
                .Select(d => new { d.Id, d.CurrentVersionId })
                .ToDictionaryAsync(d => d.Id, d => d.CurrentVersionId, ct);
            foreach (var m in found.Matches)
                if (current.TryGetValue(m.DocumentId, out var version) && version == m.VersionId) meaning.TryAdd(m.DocumentId, m);
        }

        var matched = Matching(docs, phrase);
        if (meaning.Count == 0)
        {
            var total = await matched.CountAsync(ct);
            var ranked = await Rank(matched, phrase).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
            var keywordHits = await HitsAsync(ranked.Select(r => (r.Id, (double)r.Score)).ToList(), (_, _) => null, ct);
            return new SearchResult(q, keywordHits, total, page.SafePage, page.SafePageSize, found.Ran, SearchModes.Smart, found.Problem);
        }

        var keyword = (await Rank(matched, phrase).Take(FusionWindow).ToListAsync(ct)).Select(r => r.Id).ToList();
        var fused = Fuse(keyword, meaning.Keys.ToList());
        var slice = fused.Skip(page.Skip).Take(page.SafePageSize).ToList();
        var hits = await HitsAsync(slice, (id, row) => meaning.TryGetValue(id, out var m)
            ? new SearchHit(row.ToItem(), m.Snippet, 0, SearchMatch.Meaning, m.Page > 0 ? m.Page : null)
            : null, ct);
        return new SearchResult(q, hits, fused.Count, page.SafePage, page.SafePageSize, Semantic: true, SearchModes.Smart);
    }

    private async Task<SearchResult> ExactAsync(IQueryable<Document> docs, string q, string phrase, PageRequest page, CancellationToken ct)
    {
        var like = Like(phrase);
        var matched = ExactMatching(db, docs, phrase);
        var total = await matched.CountAsync(ct);
        // A phrase in the title or number first, then the most recently updated.
        var ordered = await matched
            .Select(d => new { d.Id, InTitle = EF.Functions.ILike(d.Title, like) || EF.Functions.ILike(d.DocumentNumber ?? "", like), d.UpdatedAt })
            .OrderByDescending(x => x.InTitle).ThenByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(ct);

        var ids = ordered.Select(x => x.Id).ToArray();
        var versionIds = await db.Documents.AsNoTracking()
            .Where(d => ids.Contains(d.Id) && d.CurrentVersionId != null)
            .Select(d => d.CurrentVersionId!.Value)
            .ToArrayAsync(ct);
        var pages = await PhrasePagesOf(db, versionIds, phrase).ToDictionaryAsync(x => x.VersionId, ct);
        // The first matching page of each version (other versions' page numbers may come along; dropped below).
        var firstNumbers = pages.Values.Select(p => p.First).Distinct().ToArray();
        var texts = (await db.VersionPages.AsNoTracking()
                .Where(p => versionIds.Contains(p.VersionId) && firstNumbers.Contains(p.PageNumber))
                .Select(p => new { p.VersionId, p.PageNumber, p.Text })
                .ToListAsync(ct))
            .Where(p => pages.TryGetValue(p.VersionId, out var first) && first.First == p.PageNumber)
            .ToDictionary(p => p.VersionId, p => p.Text);

        var hits = await HitsAsync(ordered.Select(x => (x.Id, 1.0)).ToList(), (_, row) =>
            row.CurrentVersionId is { } version && pages.TryGetValue(version, out var found)
                ? new SearchHit(row.ToItem(), texts.TryGetValue(version, out var text) ? ExactSnippet(text, phrase) : null, 1,
                    SearchMatch.Text, found.First, found.Count)
                : null, ct);
        return new SearchResult(q, hits, total, page.SafePage, page.SafePageSize, Semantic: false, SearchModes.Exact);
    }

    /// <summary>
    /// The hits in the given order. <paramref name="special"/> builds a hit with its own snippet
    /// (a page, a passage); otherwise the hit shows the start of the description.
    /// </summary>
    private async Task<List<SearchHit>> HitsAsync(
        IReadOnlyList<(Guid Id, double Score)> ordered, Func<Guid, DocumentRow, SearchHit?> special, CancellationToken ct)
    {
        var ids = ordered.Select(o => o.Id).ToArray();
        var rows = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).ToRows(db).ToDictionaryAsync(r => r.Id, ct);
        return ordered.Where(o => rows.ContainsKey(o.Id))
            .Select(o =>
            {
                var row = rows[o.Id];
                var score = Math.Round(o.Score, 4);
                return special(o.Id, row) is { } hit ? hit with { Score = score } : new SearchHit(row.ToItem(), Snippet(row.Description), score);
            })
            .ToList();
    }

    public sealed record RankedMatch(Guid Id, float Score, DateTimeOffset UpdatedAt);

    /// <summary>A version's first page with the phrase, and how many pages have it.</summary>
    public sealed record PhrasePages(Guid VersionId, int First, int Count);

    /// <summary>Documents with the phrase in the title, number, description or the text of their current version.</summary>
    public static IQueryable<Document> ExactMatching(KbDbContext db, IQueryable<Document> docs, string phrase)
    {
        var like = Like(phrase);
        return docs.Where(d =>
            EF.Functions.ILike(d.Title, like)
            || EF.Functions.ILike(d.DocumentNumber ?? "", like)
            || EF.Functions.ILike(d.Description ?? "", like)
            || db.VersionPages.Any(p => p.VersionId == d.CurrentVersionId && EF.Functions.ILike(p.Text, like)));
    }

    public static IQueryable<PhrasePages> PhrasePagesOf(KbDbContext db, Guid[] versionIds, string phrase)
    {
        var like = Like(phrase);
        return db.VersionPages.AsNoTracking()
            .Where(p => versionIds.Contains(p.VersionId) && EF.Functions.ILike(p.Text, like))
            .GroupBy(p => p.VersionId)
            .Select(g => new PhrasePages(g.Key, g.Min(p => p.PageNumber), g.Count()));
    }

    /// <summary>Documents whose text matches every word as a prefix, whose title is close to the query, or whose number contains it.</summary>
    public static IQueryable<Document> Matching(IQueryable<Document> docs, string q)
    {
        var tsQuery = ToPrefixQuery(q);
        var like = Like(q);
        return docs.Where(d =>
            (tsQuery != null && EF.Property<NpgsqlTsVector>(d, DocumentConfiguration.SearchVector).Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
            || EF.Functions.TrigramsAreSimilar(d.Title, q)
            || EF.Functions.ILike(d.DocumentNumber ?? "", like));
    }

    /// <summary>Best first: full-text rank plus title similarity, and a bonus when the number matches.</summary>
    public static IQueryable<RankedMatch> Rank(IQueryable<Document> matched, string q)
    {
        var tsQuery = ToPrefixQuery(q);
        var like = Like(q);
        // EF orders by members of anonymous types only, so the record comes last.
        return matched
            .Select(d => new
            {
                d.Id,
                Score = (tsQuery != null
                        ? EF.Property<NpgsqlTsVector>(d, DocumentConfiguration.SearchVector).Rank(EF.Functions.ToTsQuery("simple", tsQuery))
                        : 0f)
                    + (float)EF.Functions.TrigramsSimilarity(d.Title, q)
                    + (EF.Functions.ILike(d.DocumentNumber ?? "", like) ? 1f : 0f),
                d.UpdatedAt,
            })
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Select(x => new RankedMatch(x.Id, x.Score, x.UpdatedAt));
    }

    /// <summary>
    /// The mode that runs and the phrase it looks for: "exact" when asked for or when the whole query
    /// is in double quotes (straight or curly, dropped); whitespace collapsed as page text is stored.
    /// </summary>
    public static (string Mode, string Phrase) ParseMode(string q, string? mode)
    {
        var quoted = q.Length >= 2 && Quotes.Contains(q[0]) && Quotes.Contains(q[^1]);
        var exact = quoted || string.Equals(mode?.Trim(), SearchModes.Exact, StringComparison.OrdinalIgnoreCase);
        return (exact ? SearchModes.Exact : SearchModes.Smart, PlainText.Collapse(quoted ? q[1..^1] : q));
    }

    private const string Quotes = "\"“”";

    /// <summary>Reciprocal rank fusion (k = 60) of the keyword and meaning rankings, best first.</summary>
    public static IReadOnlyList<(Guid Id, double Score)> Fuse(IReadOnlyList<Guid> keyword, IReadOnlyList<Guid> meaning)
    {
        const double k = 60;
        var scores = new Dictionary<Guid, double>();
        for (var i = 0; i < keyword.Count; i++) scores[keyword[i]] = scores.GetValueOrDefault(keyword[i]) + 1 / (k + i + 1);
        for (var i = 0; i < meaning.Count; i++) scores[meaning[i]] = scores.GetValueOrDefault(meaning[i]) + 1 / (k + i + 1);
        // Ties (one list each, same rank) go to the meaning match: it found words the keywords didn't.
        var meaningRank = meaning.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return scores
            .OrderByDescending(s => s.Value)
            .ThenBy(s => meaningRank.GetValueOrDefault(s.Key, int.MaxValue))
            .Select(s => (s.Key, s.Value))
            .ToList();
    }

    /// <summary>The page text around the first occurrence of the phrase, cut at spaces.</summary>
    public static string ExactSnippet(string text, string phrase, int before = 90, int after = 190)
    {
        var at = Math.Max(0, text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, at - before);
        if (start > 0 && text.IndexOf(' ', start) is var space && space >= 0 && space < at) start = space + 1;
        var end = Math.Min(text.Length, at + phrase.Length + after);
        if (end < text.Length && text.LastIndexOf(' ', end) is var last && last > at + phrase.Length) end = last;
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }

    private static string Like(string q) => $"%{q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

    /// <summary>"cs 220 data" → "cs:* &amp; 220:* &amp; data:*" (letters and digits only, so the syntax is always valid).</summary>
    public static string? ToPrefixQuery(string q)
    {
        var words = Word().Matches(q.ToLowerInvariant()).Select(m => m.Value).Distinct().Take(12).ToList();
        return words.Count == 0 ? null : string.Join(" & ", words.Select(w => $"{w}:*"));
    }

    private static string? Snippet(string? description) =>
        description is null ? null : description.Length <= 200 ? description : description[..197].TrimEnd() + "…";

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
