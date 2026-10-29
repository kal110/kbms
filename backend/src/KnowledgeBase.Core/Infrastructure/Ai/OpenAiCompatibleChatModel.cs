using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeBase.Core.Application.Ai;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// Answers from a local model, streamed (frame 10, step 9), when Ai:Chat:Provider is Local: any
/// server that speaks the OpenAI chat completions API (Ollama's /v1, vLLM, LM Studio, llama.cpp's
/// llama-server, LocalAI). The passages never leave the network. A thinking model's reasoning comes
/// in its own delta field and is not part of the answer, so it is skipped.
/// </summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, IOptions<AiOptions> options) : IChatModel
{
    private LocalChatOptions O => options.Value.Chat.Local;

    public string ModelId => O.Model;

    private sealed record Message([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

    private sealed record StreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

    private sealed record Request(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("stream_options")] StreamOptions StreamOptions);

    private sealed record Delta([property: JsonPropertyName("content")] string? Content);

    private sealed record Choice([property: JsonPropertyName("delta")] Delta? Delta, [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record Usage([property: JsonPropertyName("prompt_tokens")] long PromptTokens, [property: JsonPropertyName("completion_tokens")] long CompletionTokens);

    // "error" is an object on most servers and a string on some; only its presence matters.
    private sealed record Chunk(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] List<Choice>? Choices,
        [property: JsonPropertyName("usage")] Usage? Usage,
        [property: JsonPropertyName("error")] JsonElement? Error);

    public async IAsyncEnumerable<ChatModelEvent> StreamAsync(string systemPrompt, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var o = O;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{o.Endpoint.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(new Request(
                o.Model,
                [new("system", systemPrompt), new("user", userMessage)],
                o.MaxTokens,
                o.Temperature,
                string.IsNullOrWhiteSpace(o.ReasoningEffort) ? null : o.ReasoningEffort.Trim(),
                Stream: true,
                new StreamOptions(IncludeUsage: true))),
        };
        if (!string.IsNullOrEmpty(o.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        // The timeout covers loading the model and reading the passages, up to the first token;
        // after that the answer is bounded by MaxTokens and the caller's cancellation.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatModelException($"The local model didn't start answering within {o.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException)
        {
            throw new ChatModelException($"The local model server at {Host(o.Endpoint)} can't be reached.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new ChatModelException(Describe(response.StatusCode, o.Model));
            cts.CancelAfter(Timeout.InfiniteTimeSpan);

            string? model = null, finishReason = null;
            long input = 0, output = 0;
            var body = await response.Content.ReadAsStreamAsync(cts.Token);
            var events = SseParser.Create(body).EnumerateAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            await using (events)
            {
                while (true)
                {
                    Chunk? chunk;
                    try
                    {
                        if (!await events.MoveNextAsync() || events.Current.Data == "[DONE]") break;
                        chunk = JsonSerializer.Deserialize<Chunk>(events.Current.Data);
                    }
                    catch (JsonException)
                    {
                        throw new ChatModelException("The local model server sent a reply that isn't an OpenAI-compatible stream.");
                    }
                    catch (IOException)
                    {
                        throw new ChatModelException("The local model server closed the connection mid-answer.");
                    }

                    if (chunk is null) continue;
                    // The server's message stays in its own log: it may quote the request.
                    if (chunk.Error is { ValueKind: not JsonValueKind.Null })
                        throw new ChatModelException("The local model stopped with an error mid-answer. The model server's log has the details.");

                    model ??= chunk.Model;
                    if (chunk.Usage is { } usage) (input, output) = (usage.PromptTokens, usage.CompletionTokens);
                    if (chunk.Choices is [var choice, ..])
                    {
                        if (!string.IsNullOrEmpty(choice.Delta?.Content)) yield return new ChatModelEvent(choice.Delta.Content);
                        finishReason = choice.FinishReason ?? finishReason;
                    }
                }
            }

            yield return new ChatModelEvent(null, StopReason(finishReason), input, output, model ?? o.Model);
        }
    }

    /// <summary>OpenAI finish reasons in Anthropic's stop-reason names, which the assistant checks.</summary>
    public static string StopReason(string? finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "content_filter" => "refusal",
        _ => "end_turn",
    };

    /// <summary>Status only: error bodies may echo request content.</summary>
    private static string Describe(HttpStatusCode status, string model) => status switch
    {
        HttpStatusCode.NotFound => $"The local model server has no model \"{model}\". Pull it first (ollama pull {model}); a new Docker stack downloads it in the background.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The local model server rejected the API key (Ai:Chat:Local:ApiKey).",
        HttpStatusCode.BadRequest => "The local model server rejected the request (400). The passages may not fit the model's context window.",
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => "The local model is busy. Try again in a minute.",
        _ => $"The local model server answered {(int)status}.",
    };

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : "the configured endpoint";
}
