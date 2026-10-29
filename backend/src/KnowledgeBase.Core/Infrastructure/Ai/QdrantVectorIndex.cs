using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeBase.Core.Application.Ai;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// Qdrant over its REST API (frame 10, "COLLECTION kb_chunks_v3"): a named dense vector ("dense",
/// cosine, on disk, int8 quantized in RAM) and a sparse BM25 vector ("bm25", IDF on the server),
/// keyword payload indexes for every filter field, and an alias that searches go through.
/// </summary>
public sealed class QdrantVectorIndex(HttpClient http, IOptions<AiOptions> options) : IVectorIndex
{
    public const string Dense = "dense";
    public const string Sparse = "bm25";

    /// <summary>Payload fields with a keyword index: everything a search filters on.</summary>
    public static readonly string[] KeywordFields =
    [
        "document_id", "version_id", "acl_principals", "state", "lifecycle", "owner_id",
        "folder_ids", "document_type_id", "department_id", "academic_year_id", "academic_term_id",
    ];

    private string Base => options.Value.VectorStore.Url.TrimEnd('/');

    public async Task<VectorCollectionInfo?> GetCollectionAsync(string collection, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/collections/{collection}", null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var result = (await ReadAsync(response, ct))["result"]!;
        var size = result["config"]?["params"]?["vectors"]?[Dense]?["size"]?.GetValue<int>() ?? 0;
        return new VectorCollectionInfo(collection, size, result["points_count"]?.GetValue<long>() ?? 0, result["status"]?.GetValue<string>() ?? "unknown");
    }

    public async Task CreateCollectionAsync(string collection, int dimensions, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["vectors"] = new JsonObject
            {
                [Dense] = new JsonObject { ["size"] = dimensions, ["distance"] = "Cosine", ["on_disk"] = true },
            },
            ["sparse_vectors"] = new JsonObject { [Sparse] = new JsonObject { ["modifier"] = "idf" } },
            ["hnsw_config"] = new JsonObject { ["m"] = 16, ["ef_construct"] = 128 },
            ["quantization_config"] = new JsonObject
            {
                ["scalar"] = new JsonObject { ["type"] = "int8", ["always_ram"] = true },
            },
        };
        (await SendAsync(HttpMethod.Put, $"/collections/{collection}", body, ct)).Dispose();

        foreach (var field in KeywordFields)
            (await SendAsync(HttpMethod.Put, $"/collections/{collection}/index?wait=true",
                new JsonObject { ["field_name"] = field, ["field_schema"] = "keyword" }, ct)).Dispose();
        (await SendAsync(HttpMethod.Put, $"/collections/{collection}/index?wait=true",
            new JsonObject { ["field_name"] = "pipeline_version", ["field_schema"] = "integer" }, ct)).Dispose();
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Delete, $"/collections/{collection}", null, ct, allowNotFound: true)).Dispose();

    public async Task PointAliasAsync(string alias, string collection, CancellationToken ct = default)
    {
        using var current = await SendAsync(HttpMethod.Get, "/aliases", null, ct);
        var exists = (await ReadAsync(current, ct))["result"]?["aliases"]?.AsArray()
            .Any(a => a?["alias_name"]?.GetValue<string>() == alias) == true;

        // One request, so the alias is never missing in between (an atomic swap).
        var actions = new JsonArray();
        if (exists) actions.Add(new JsonObject { ["delete_alias"] = new JsonObject { ["alias_name"] = alias } });
        actions.Add(new JsonObject { ["create_alias"] = new JsonObject { ["collection_name"] = collection, ["alias_name"] = alias } });
        (await SendAsync(HttpMethod.Post, "/collections/aliases", new JsonObject { ["actions"] = actions }, ct)).Dispose();
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct = default)
    {
        foreach (var batch in points.Chunk(64))
        {
            var array = new JsonArray();
            foreach (var p in batch)
            {
                array.Add(new JsonObject
                {
                    ["id"] = p.Id.ToString(),
                    ["vector"] = new JsonObject
                    {
                        [Dense] = new JsonArray(p.Dense.Select(v => (JsonNode)v).ToArray()),
                        [Sparse] = new JsonObject
                        {
                            ["indices"] = new JsonArray(p.Sparse.Indices.Select(i => (JsonNode)i).ToArray()),
                            ["values"] = new JsonArray(p.Sparse.Values.Select(v => (JsonNode)v).ToArray()),
                        },
                    },
                    ["payload"] = JsonSerializer.SerializeToNode(p.Payload),
                });
            }
            (await SendAsync(HttpMethod.Put, $"/collections/{collection}/points?wait=true", new JsonObject { ["points"] = array }, ct)).Dispose();
        }
    }

    public async Task DeleteVersionAsync(string collection, Guid versionId, int? keepPipelineVersion = null, CancellationToken ct = default)
    {
        var filter = new JsonObject { ["must"] = new JsonArray(Match("version_id", versionId.ToString())) };
        if (keepPipelineVersion is { } keep) filter["must_not"] = new JsonArray(Match("pipeline_version", keep));
        (await SendAsync(HttpMethod.Post, $"/collections/{collection}/points/delete?wait=true", new JsonObject { ["filter"] = filter }, ct)).Dispose();
    }

    public async Task SetVersionPayloadAsync(string collection, Guid versionId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["payload"] = JsonSerializer.SerializeToNode(payload),
            ["filter"] = new JsonObject { ["must"] = new JsonArray(Match("version_id", versionId.ToString())) },
        };
        (await SendAsync(HttpMethod.Post, $"/collections/{collection}/points/payload?wait=true", body, ct)).Dispose();
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] dense, SparseVector sparse, VectorSearchFilter filter, int limit, CancellationToken ct = default)
    {
        var searches = new JsonArray
        {
            new JsonObject
            {
                ["query"] = new JsonArray(dense.Select(v => (JsonNode)v).ToArray()),
                ["using"] = Dense,
                ["filter"] = Filter(filter),
                ["limit"] = limit,
                ["with_payload"] = true,
            },
        };
        if (sparse.Indices.Length > 0)
        {
            searches.Add(new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["indices"] = new JsonArray(sparse.Indices.Select(i => (JsonNode)i).ToArray()),
                    ["values"] = new JsonArray(sparse.Values.Select(v => (JsonNode)v).ToArray()),
                },
                ["using"] = Sparse,
                ["filter"] = Filter(filter),
                ["limit"] = limit,
                ["with_payload"] = true,
            });
        }

        using var response = await SendAsync(HttpMethod.Post, $"/collections/{collection}/points/query/batch", new JsonObject { ["searches"] = searches }, ct);
        var results = (await ReadAsync(response, ct))["result"]!.AsArray();
        var lists = results.Select(r => r!["points"]!.AsArray()
            .Select(p => (Id: Guid.Parse(p!["id"]!.GetValue<string>()), Score: p["score"]!.GetValue<double>(), Payload: p["payload"]!))
            .ToList()).ToList();
        return Fuse(lists[0], lists.Count > 1 ? lists[1] : [], limit);
    }

    /// <summary>
    /// Reciprocal rank fusion (k = 60) of the dense and keyword lists. The dense cosine is kept for
    /// the evidence guard; a keyword-only hit has no dense score (0).
    /// </summary>
    public static IReadOnlyList<VectorHit> Fuse(
        IReadOnlyList<(Guid Id, double Score, JsonNode Payload)> dense,
        IReadOnlyList<(Guid Id, double Score, JsonNode Payload)> keyword,
        int limit)
    {
        const double k = 60;
        var hits = new Dictionary<Guid, (double Dense, double Fused, JsonNode Payload)>();
        for (var i = 0; i < dense.Count; i++)
            hits[dense[i].Id] = (dense[i].Score, 1 / (k + i + 1), dense[i].Payload);
        for (var i = 0; i < keyword.Count; i++)
        {
            var h = hits.GetValueOrDefault(keyword[i].Id, (0, 0, keyword[i].Payload));
            hits[keyword[i].Id] = (h.Dense, h.Fused + 1 / (k + i + 1), h.Payload);
        }
        return hits
            .OrderByDescending(h => h.Value.Fused).ThenByDescending(h => h.Value.Dense)
            .Take(limit)
            .Select(h => new VectorHit(h.Key, h.Value.Dense, h.Value.Fused,
                h.Value.Payload.Deserialize<Dictionary<string, JsonElement>>() ?? []))
            .ToList();
    }

    /// <summary>The access filter (frame 10, "FILTERS BUILT BY THE API"): it can only remove results.</summary>
    public static JsonObject Filter(VectorSearchFilter f)
    {
        var must = new JsonArray
        {
            new JsonObject { ["key"] = "acl_principals", ["match"] = new JsonObject { ["any"] = new JsonArray(f.PrincipalKeys.Select(k => (JsonNode)k).ToArray()) } },
        };
        if (f.Scope == KnowledgeScopes.MyDrafts)
        {
            must.Add(Match("state", "draft"));
            must.Add(Match("owner_id", (f.OwnerId ?? Guid.Empty).ToString()));
        }
        else
        {
            must.Add(Match("state", "published"));
            must.Add(Match("lifecycle", "active"));
        }
        if (f.FolderId is { } folder) must.Add(Match("folder_ids", folder.ToString()));
        if (f.DocumentTypeId is { } type) must.Add(Match("document_type_id", type.ToString()));
        if (f.AcademicYearId is { } year) must.Add(Match("academic_year_id", year.ToString()));
        if (f.AcademicTermId is { } term) must.Add(Match("academic_term_id", term.ToString()));
        return new JsonObject { ["must"] = must };
    }

    private static JsonObject Match(string key, JsonNode value) =>
        new() { ["key"] = key, ["match"] = new JsonObject { ["value"] = value } };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, Base + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (!string.IsNullOrEmpty(options.Value.VectorStore.ApiKey)) request.Headers.Add("api-key", options.Value.VectorStore.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new VectorIndexException($"The vector store at {Host()} can't be reached.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VectorIndexException("The vector store timed out.");
        }
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;

        // Qdrant's error text names fields and collections, never content; keep it short.
        var status = (int)response.StatusCode;
        string? error = null;
        try { error = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["status"]?["error"]?.GetValue<string>(); }
        catch (Exception) { /* not JSON */ }
        response.Dispose();
        throw new VectorIndexException($"The vector store answered {status}{(error is null ? "" : $": {Truncate(error, 200)}")}.");
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new VectorIndexException("The vector store returned an empty response.");

    private string Host() => Uri.TryCreate(Base, UriKind.Absolute, out var u) ? u.Authority : "the configured URL";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed class VectorIndexException(string message) : Exception(message);
