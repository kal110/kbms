using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using KnowledgeBase.Core.Application.Ai;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// Answers with Claude over the Messages API, streamed (frame 10, step 9), when Ai:Chat:Provider is
/// Anthropic. Thinking is adaptive (the model default); Ai:Chat:Anthropic:Effort sets how hard it
/// works. A policy decline is retried server-side on Anthropic's recommended fallback model
/// ("fallbacks": "default"); if the whole chain declines, the final event says "refusal" and the
/// caller discards the partial answer.
/// </summary>
public sealed class ClaudeChatModel : IChatModel, IDisposable
{
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly Lazy<AnthropicClient> client;
    private readonly AnthropicChatOptions options;

    public ClaudeChatModel(IOptions<AiOptions> options)
    {
        this.options = options.Value.Chat.Anthropic;
        // No key in config: the SDK reads ANTHROPIC_API_KEY (or an `ant auth login` profile).
        // Created on first use, so a host with AI switched off never needs a key.
        var key = this.options.ApiKey;
        client = new(() => string.IsNullOrWhiteSpace(key) ? new AnthropicClient() : new AnthropicClient { ApiKey = key });
    }

    public string ModelId => options.Model;

    public async IAsyncEnumerable<ChatModelEvent> StreamAsync(string systemPrompt, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            System = systemPrompt,
            Messages = [new BetaUserMessageParam { Content = userMessage }],
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(options.Effort) },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        };

        string? model = null, stopReason = null;
        long input = 0, output = 0;

        IAsyncEnumerator<BetaRawMessageStreamEvent> stream;
        try
        {
            stream = client.Value.Beta.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
        }
        catch (AnthropicApiException ex)
        {
            throw new ChatModelException(Describe(ex));
        }

        await using (stream)
        {
            while (true)
            {
                BetaRawMessageStreamEvent ev;
                try
                {
                    if (!await stream.MoveNextAsync()) break;
                    ev = stream.Current;
                }
                catch (AnthropicApiException ex)
                {
                    throw new ChatModelException(Describe(ex));
                }
                catch (HttpRequestException)
                {
                    throw new ChatModelException("The answer service can't be reached.");
                }

                if (ev.TryPickStart(out var start))
                {
                    model = start.Message.Model.Raw();
                    input = start.Message.Usage.InputTokens;
                }
                else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return new ChatModelEvent(text.Text);
                }
                else if (ev.TryPickDelta(out var messageDelta))
                {
                    // Raw(): the wire value ("refusal"); ToString() would be the JSON, quotes included.
                    stopReason = messageDelta.Delta.StopReason?.Raw() ?? stopReason;
                    output = messageDelta.Usage.OutputTokens;
                }
            }
        }

        yield return new ChatModelEvent(null, stopReason ?? "end_turn", input, output, model ?? options.Model);
    }

    private static Effort ParseEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    /// <summary>Status and type only: API error bodies may echo request content.</summary>
    private static string Describe(AnthropicApiException ex) => ex switch
    {
        AnthropicRateLimitException => "The answer service is busy (rate limited). Try again in a minute.",
        AnthropicUnauthorizedException => "The answer service rejected the API key (Ai:Chat:Anthropic:ApiKey or ANTHROPIC_API_KEY).",
        Anthropic5xxException => "The answer service had a temporary error. Try again.",
        _ => $"The answer service rejected the request ({ex.GetType().Name}).",
    };

    public void Dispose()
    {
        if (client.IsValueCreated) client.Value.Dispose();
    }
}
