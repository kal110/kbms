using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Infrastructure.Ai;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>
/// The AI assistant and semantic search (frames 5, 10): grounded answers over the caller's
/// permitted, re-checked passages, with citations. Keyword search lives with the library
/// (/api/knowledge/search). With Ai:Enabled off the routes answer 501, and 503 while the index is
/// being built or a service it needs is down.
/// </summary>
public static class KnowledgeEndpoints
{
    public const string RateLimitPolicy = "ai";

    /// <param name="Scope">"published" (default: the official library) or "my-drafts" (your own unpublished versions).</param>
    public sealed record ChatRequest(
        string Question,
        string? Scope = null,
        Guid? FolderId = null,
        Guid? DocumentTypeId = null,
        Guid? AcademicYearId = null,
        Guid? AcademicTermId = null);

    public sealed record PassageQuery(
        [property: FromQuery(Name = "q")] string? Q,
        [property: FromQuery(Name = "scope")] string? Scope,
        [property: FromQuery(Name = "folderId")] Guid? FolderId,
        [property: FromQuery(Name = "documentTypeId")] Guid? DocumentTypeId,
        [property: FromQuery(Name = "academicYearId")] Guid? AcademicYearId,
        [property: FromQuery(Name = "academicTermId")] Guid? AcademicTermId);

    /// <param name="Enabled">Questions can be asked right now.</param>
    /// <param name="Reason">Why not, when Enabled is false.</param>
    public sealed record AssistantStatus(bool Enabled, string? Reason, string EmbeddingModel, string ChatModel, IReadOnlyList<string> Scopes, int MaxQuestionChars);

    public static RouteGroupBuilder MapKnowledgeEndpoints(this RouteGroupBuilder api)
    {
        var knowledge = api.MapGroup("/knowledge").WithTags("Knowledge");

        knowledge.MapGet("/assistant/status", async (IOptions<AiOptions> ai, SearchIndexState state, CancellationToken ct) =>
            {
                var a = ai.Value;
                string? reason;
                if (!a.Enabled) reason = "The AI assistant and semantic search are switched off.";
                else (_, reason) = await state.WritableAsync(ct);
                if (reason is not null) reason = char.ToUpperInvariant(reason[0]) + reason[1..].TrimEnd('.') + ".";
                return new AssistantStatus(reason is null, reason, a.Embedding.Model, a.Chat.Model,
                    [KnowledgeScopes.Published, KnowledgeScopes.MyDrafts], a.Retrieval.MaxQuestionChars);
            })
            .WithSummary("Whether the AI assistant can answer right now, and why not");

        knowledge.MapPost("/chat", async (ChatRequest request, AssistantService assistant, CancellationToken ct) =>
            {
                // Validation, retrieval and the access re-check run before the stream starts, so
                // their errors are ordinary ProblemDetails responses.
                var turn = await assistant.StartAsync(new KnowledgeQuery(request.Question, request.Scope,
                    request.FolderId, request.DocumentTypeId, request.AcademicYearId, request.AcademicTermId), ct);
                return TypedResults.ServerSentEvents(Events(assistant, turn, ct));
            })
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Ask the assistant: text/event-stream of sources, delta…, done (or error)")
            .Produces<AssistantEvent>(StatusCodes.Status200OK, "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        knowledge.MapGet("/passages", ([AsParameters] PassageQuery query, KnowledgeRetriever retriever, CancellationToken ct) =>
                retriever.SearchAsync(new KnowledgeQuery(query.Q ?? "", query.Scope, query.FolderId, query.DocumentTypeId,
                    query.AcademicYearId, query.AcademicTermId), ct))
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Semantic passage search over the caller's permitted documents (no generated answer)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static async IAsyncEnumerable<SseItem<AssistantEvent>> Events(AssistantService assistant, AssistantTurn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in assistant.StreamAsync(turn, ct)) yield return new SseItem<AssistantEvent>(e, e.Type);
    }

    /// <summary>Ai:Retrieval:RequestsPerMinute per signed-in user across /chat and /passages (frame 10, step 3).</summary>
    public static IServiceCollection AddKnowledgeRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, http.RequestServices.GetRequiredService<IOptions<AiOptions>>().Value.Retrieval.RequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Type = ProblemDetailsExceptionHandler.TypeFor("rate-limited"),
                        Title = "Too many questions in a minute. Wait a moment and try again.",
                        Extensions = { ["code"] = "rate-limited" },
                    },
                });
            };
        });
}
