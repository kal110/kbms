using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KnowledgeBase.Core.Application.Ai;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// Embeddings from any server that speaks the OpenAI embeddings API: Ollama (/v1), Hugging Face
/// TEI, vLLM, LocalAI or a hosted provider. The model and dimensions come from Ai:Embedding and are
/// checked on every response, so a misconfigured server fails fast instead of writing vectors of the
/// wrong size (frame 10, step 4).
/// </summary>
public sealed class OpenAiCompatibleEmbeddings(HttpClient http, IOptions<AiOptions> options) : IEmbeddingProvider
{
    private EmbeddingOptions O => options.Value.Embedding;

    public string ModelId => O.Model;
    public int Dimensions => O.Dimensions;

    private sealed record Request(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("encoding_format")] string EncodingFormat = "float");

    private sealed record Item([property: JsonPropertyName("index")] int Index, [property: JsonPropertyName("embedding")] float[] Embedding);

    private sealed record Response([property: JsonPropertyName("data")] List<Item>? Data);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(Math.Max(1, O.BatchSize)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{O.Endpoint.TrimEnd('/')}/embeddings")
            {
                Content = JsonContent.Create(new Request(O.Model, batch)),
            };
            if (!string.IsNullOrEmpty(O.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, O.TimeoutSeconds)));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new EmbeddingException("The embedding service timed out.");
            }
            catch (HttpRequestException)
            {
                throw new EmbeddingException($"The embedding service at {Host(O.Endpoint)} can't be reached.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new EmbeddingException($"The embedding service answered {(int)response.StatusCode} for model \"{O.Model}\".");

                var body = await response.Content.ReadFromJsonAsync<Response>(ct);
                var items = body?.Data?.OrderBy(d => d.Index).ToList() ?? [];
                if (items.Count != batch.Length)
                    throw new EmbeddingException($"The embedding service returned {items.Count} vectors for {batch.Length} texts.");
                foreach (var item in items)
                {
                    if (item.Embedding.Length != O.Dimensions)
                        throw new EmbeddingException($"Model \"{O.Model}\" returned {item.Embedding.Length} dimensions; Ai:Embedding:Dimensions is {O.Dimensions}.", permanent: true);
                    results.Add(Normalize(item.Embedding));
                }
            }
        }
        return results;
    }

    /// <summary>Unit length, so cosine similarity equals the dot product whatever the server returns.</summary>
    private static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm is 0 or double.NaN) return v;
        return v.Select(x => (float)(x / norm)).ToArray();
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : "the configured endpoint";
}

/// <summary>An embedding failure with an admin-safe message (no keys, no response bodies).</summary>
public sealed class EmbeddingException(string message, bool permanent = false) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}
