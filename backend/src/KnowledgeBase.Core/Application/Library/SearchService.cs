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
/// Keyword search over the documents the caller can see (frame 1's search bar): PostgreSQL full-text
/// on title, number and description with prefix matching, plus trigram similarity on the title for
/// typos. Semantic search joins in once the AI index is enabled (frame 5).
/// </summary>
public sealed partial class SearchService(KbDbContext db, ICurrentUser currentUser, IAccessEvaluator access)
{
    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var q = (query.Q ?? "").Trim();
        if (q.Length > 200) throw new ValidationAppException("q", "Keep the search under 200 characters.");

        var docs = access.VisibleDocuments(me).AsNoTracking();
        if (query.TypeId is { } typeId) docs = docs.Where(d => d.DocumentTypeId == typeId);
        if (query.YearId is { } yearId) docs = docs.Where(d => d.AcademicYearId == yearId);
        if (query.TermId is { } termId) docs = docs.Where(d => d.AcademicTermId == termId);
        if (query.FolderId is { } folderId)
        {
            var path = await db.Folders.AsNoTracking().Where(f => f.Id == folderId).Select(f => f.Path).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Folder", folderId);
            docs = docs.Where(d => d.Folder.Path.StartsWith(path));
        }

        var page = new PageRequest(query.Page, query.PageSize);

        if (q.Length == 0)
        {
            // No words: the most recently updated documents, so the page is never empty.
            var recent = await docs.OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id).ToRows(db).ToPagedAsync(page, ct);
            return new SearchResult(q, recent.Items.Select(r => new SearchHit(r.ToItem(), Snippet(r.Description), 0)).ToList(),
                recent.Total, recent.Page, recent.PageSize, Semantic: false);
        }

        var matched = Matching(docs, q);
        var total = await matched.CountAsync(ct);
        var ranked = await Rank(matched, q).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);

        var ids = ranked.Select(r => r.Id).ToArray();
        var rows = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).ToRows(db).ToDictionaryAsync(r => r.Id, ct);
        var hits = ranked.Where(r => rows.ContainsKey(r.Id))
            .Select(r => new SearchHit(rows[r.Id].ToItem(), Snippet(rows[r.Id].Description), Math.Round(r.Score, 4)))
            .ToList();
        return new SearchResult(q, hits, total, page.SafePage, page.SafePageSize, Semantic: false);
    }

    public sealed record RankedMatch(Guid Id, float Score, DateTimeOffset UpdatedAt);

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
