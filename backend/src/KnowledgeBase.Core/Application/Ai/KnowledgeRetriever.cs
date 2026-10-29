using System.Text.Json;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Application.Ai;

/// <param name="Scope">"published" (default) or "my-drafts".</param>
public sealed record KnowledgeQuery(
    string Question,
    string? Scope = null,
    Guid? FolderId = null,
    Guid? DocumentTypeId = null,
    Guid? AcademicYearId = null,
    Guid? AcademicTermId = null);

/// <summary>A chunk the caller may read, numbered as the model and the citations see it.</summary>
public sealed record Passage(
    int Number,
    Guid ChunkId,
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string Title,
    string? DocumentNumber,
    string? DocumentType,
    int PageStart,
    int PageEnd,
    string? Section,
    string Text,
    double Score);

/// <param name="BestScore">Highest dense cosine similarity among the passages.</param>
/// <param name="Sufficient">BestScore reached Ai:Retrieval:MinScore (frame 10, step 11).</param>
public sealed record RetrievalResult(string Scope, IReadOnlyList<Passage> Passages, double BestScore, bool Sufficient, string EmbeddingModel);

/// <summary>
/// Frame 10, steps 3–7: validate, embed the question, run the dense and keyword searches with the
/// caller's access filter, then let PostgreSQL decide. Every hit is re-checked against
/// <see cref="IAccessEvaluator.VisibleDocuments"/> and the scope's version rule, so a stale index
/// payload can only hide a passage, never leak one.
/// </summary>
public sealed class KnowledgeRetriever(
    ICurrentUser currentUser,
    IAccessEvaluator access,
    IEmbeddingProvider embedder,
    IVectorIndex index,
    SearchIndexState state,
    IOptions<AiOptions> ai)
{
    /// <summary>Semantic passage search for the signed-in user (GET /api/knowledge/passages).</summary>
    public async Task<RetrievalResult> SearchAsync(KnowledgeQuery query, CancellationToken ct = default) =>
        await RetrieveAsync(await currentUser.GetAsync(ct), query, ct);

    /// <param name="followUpOf">
    /// The conversation's previous question. It is searched together with this one, so a follow-up
    /// such as "and for the second term?" finds the passages it is about.
    /// </param>
    public async Task<RetrievalResult> RetrieveAsync(UserContext user, KnowledgeQuery query, CancellationToken ct = default, string? followUpOf = null)
    {
        var o = ai.Value;
        if (!o.Enabled) throw new FeatureDisabledException("The AI assistant", "Ai:Enabled is off.");
        var question = Validate(query.Question, o.Retrieval.MaxQuestionChars);
        var scope = KnowledgeScopes.Normalize(query.Scope);

        var (active, problem) = await state.WritableAsync(ct);
        if (active is null || problem is not null)
            throw new ServiceUnavailableException("index-unavailable", "Semantic search isn't ready.", Sentence(problem));

        var searchText = string.IsNullOrWhiteSpace(followUpOf) ? question : $"{followUpOf.Trim()}\n{question}";
        float[] dense;
        try
        {
            dense = (await embedder.EmbedAsync([searchText], ct))[0];
        }
        catch (EmbeddingException ex)
        {
            throw new ServiceUnavailableException("embedding-unavailable", "The embedding service isn't available.", ex.Message);
        }

        var filter = new VectorSearchFilter(user.PrincipalKeys, scope, user.UserId, query.FolderId, query.DocumentTypeId, query.AcademicYearId, query.AcademicTermId);
        IReadOnlyList<VectorHit> hits;
        try
        {
            hits = await index.SearchAsync(active.Collection, dense, SparseEncoder.EncodeQuery(searchText), filter, o.Retrieval.Candidates, ct);
        }
        catch (VectorIndexException ex)
        {
            throw new ServiceUnavailableException("index-unavailable", "The search index isn't available.", ex.Message);
        }

        // Step 7: PostgreSQL is the authority. Drop anything the caller can't open today, or whose
        // version no longer fits the scope (published → the published version of an active
        // document; my-drafts → the caller's own current, unpublished version).
        var candidates = hits
            .Select(h => (Hit: h, DocumentId: GuidField(h, "document_id"), VersionId: GuidField(h, "version_id")))
            .Where(c => c.DocumentId != Guid.Empty && c.VersionId != Guid.Empty)
            .ToList();
        var documentIds = candidates.Select(c => c.DocumentId).Distinct().ToArray();
        var documents = await access.VisibleDocuments(user).AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.DocumentNumber,
                TypeName = d.DocumentType.Name,
                d.CurrentVersionId,
                d.PublishedVersionId,
                d.OwnerUserId,
                d.LifecycleStatus,
            })
            .ToDictionaryAsync(d => d.Id, ct);

        var perDocument = new Dictionary<Guid, int>();
        var passages = new List<Passage>();
        foreach (var c in candidates)
        {
            if (!documents.TryGetValue(c.DocumentId, out var d)) continue;
            var fits = scope == KnowledgeScopes.MyDrafts
                ? d.OwnerUserId == user.UserId && d.CurrentVersionId == c.VersionId && d.PublishedVersionId != c.VersionId
                : d.PublishedVersionId == c.VersionId && d.LifecycleStatus == LifecycleStatus.Active;
            if (!fits) continue;

            // Step 6: diversity, so one long document can't crowd out the rest.
            var taken = perDocument.GetValueOrDefault(c.DocumentId);
            if (taken >= o.Retrieval.MaxPassagesPerDocument) continue;
            perDocument[c.DocumentId] = taken + 1;

            var p = c.Hit.Payload;
            passages.Add(new Passage(
                passages.Count + 1,
                c.Hit.PointId,
                c.DocumentId,
                c.VersionId,
                IntField(p, "version_number"),
                d.Title,
                d.DocumentNumber,
                d.TypeName,
                IntField(p, "page_start"),
                IntField(p, "page_end"),
                StringField(p, "section") is { Length: > 0 } section ? section : null,
                StringField(p, "text") ?? "",
                Math.Round(c.Hit.DenseScore, 4)));
        }

        return Select(passages, o.Retrieval, scope, embedder.ModelId);
    }

    /// <summary>
    /// Steps 6 and 11 over the fused, access-checked candidates (best first): with enough evidence,
    /// drop passages under MinPassageScore; then keep the first Retrieval:Passages and number them.
    /// </summary>
    public static RetrievalResult Select(IReadOnlyList<Passage> candidates, RetrievalOptions o, string scope, string model)
    {
        var best = candidates.Count == 0 ? 0 : candidates.Max(x => x.Score);
        var sufficient = candidates.Count > 0 && best >= o.MinScore;
        var passages = candidates
            .Where(p => !sufficient || p.Score >= o.MinPassageScore)
            .Take(o.Passages)
            .Select((p, i) => p with { Number = i + 1 })
            .ToList();
        return new RetrievalResult(scope, passages, best, sufficient, model);
    }

    public static string Validate(string? question, int maxChars)
    {
        var q = question?.Trim() ?? "";
        if (q.Length == 0) throw new ValidationAppException("question", "Ask a question.");
        if (q.Length > maxChars) throw new ValidationAppException("question", $"Keep the question under {maxChars} characters.");
        return q;
    }

    private static string? Sentence(string? s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..] + ".";

    internal static Guid GuidField(VectorHit hit, string key) =>
        hit.Payload.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id) ? id : Guid.Empty;

    internal static int IntField(IReadOnlyDictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    internal static string? StringField(IReadOnlyDictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
