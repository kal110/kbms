using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Infrastructure.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Application.Ai;

/// <summary>
/// The library's search by meaning (frame 5): the query's embedding against the current version of
/// every document the caller may see, with the same hybrid dense + keyword search the assistant
/// uses. A document counts only through a passage whose dense similarity reaches
/// Ai:Retrieval:MinScore, so a query that means nothing to the library adds nothing. The matches
/// are candidates: the library re-checks each one in PostgreSQL before showing it.
/// </summary>
public sealed class SemanticDocumentSearch(
    IEmbeddingProvider embedder,
    IVectorIndex index,
    SearchIndexState state,
    IOptions<AiOptions> ai,
    ILogger<SemanticDocumentSearch> log) : ISemanticSearch
{
    /// <summary>Passages fetched per search; the documents come from these.</summary>
    public const int Candidates = 100;

    public async Task<SemanticMatches> FindDocumentsAsync(UserContext user, string query, SemanticFilter filter, CancellationToken ct = default)
    {
        var o = ai.Value;
        if (!o.Enabled) return new SemanticMatches(false, null, []);
        var (active, problem) = await state.WritableAsync(ct);
        if (active is null || problem is not null) return new SemanticMatches(false, problem, []);

        try
        {
            var dense = (await embedder.EmbedAsync([query], ct))[0];
            var scope = new VectorSearchFilter(user.PrincipalKeys, KnowledgeScopes.Current, user.UserId,
                filter.FolderId, filter.DocumentTypeId, filter.AcademicYearId, filter.AcademicTermId);
            var hits = await index.SearchAsync(active.Collection, dense, SparseEncoder.EncodeQuery(query), scope, Candidates, ct);
            return new SemanticMatches(true, null, BestPerDocument(hits, o.Retrieval.MinScore));
        }
        catch (Exception ex) when (ex is EmbeddingException or VectorIndexException)
        {
            log.LogWarning("Search by meaning is unavailable: {Error}", ex.Message);
            return new SemanticMatches(false, ex.Message, []);
        }
    }

    /// <summary>
    /// The hits come best first (fused rank); each document's first passage that is close enough
    /// in meaning represents it, and the documents keep that order.
    /// </summary>
    public static IReadOnlyList<SemanticMatch> BestPerDocument(IReadOnlyList<VectorHit> hits, double minScore)
    {
        var matches = new List<SemanticMatch>();
        var seen = new HashSet<Guid>();
        foreach (var hit in hits)
        {
            if (hit.DenseScore < minScore) continue;
            var documentId = KnowledgeRetriever.GuidField(hit, "document_id");
            var versionId = KnowledgeRetriever.GuidField(hit, "version_id");
            if (documentId == Guid.Empty || versionId == Guid.Empty || !seen.Add(documentId)) continue;

            var section = KnowledgeRetriever.StringField(hit.Payload, "section");
            matches.Add(new SemanticMatch(
                documentId,
                versionId,
                Math.Round(hit.DenseScore, 4),
                KnowledgeRetriever.IntField(hit.Payload, "page_start"),
                string.IsNullOrEmpty(section) ? null : section,
                AssistantService.Snippet(KnowledgeRetriever.StringField(hit.Payload, "text") ?? "")));
        }
        return matches;
    }
}
