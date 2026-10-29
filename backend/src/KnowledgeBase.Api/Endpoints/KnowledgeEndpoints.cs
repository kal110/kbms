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
    /// <param name="ConversationId">Continue one of your conversations; omit to start a new one (its id comes in the "sources" event).</param>
    public sealed record ChatRequest(
        string Question,
        string? Scope = null,
        Guid? FolderId = null,
        Guid? DocumentTypeId = null,
        Guid? AcademicYearId = null,
        Guid? AcademicTermId = null,
        Guid? ConversationId = null);

    public sealed record ConversationListQuery(
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize);

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
                    request.FolderId, request.DocumentTypeId, request.AcademicYearId, request.AcademicTermId), request.ConversationId, ct);
                return TypedResults.ServerSentEvents(Events(assistant, turn, ct));
            })
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Ask the assistant: text/event-stream of sources, delta…, done (or error)")
            .Produces<AssistantEvent>(StatusCodes.Status200OK, "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // The caller's own conversations; they stay readable while AI is switched off.
        var conversations = knowledge.MapGroup("/conversations");
        conversations.MapGet("/", ([AsParameters] ConversationListQuery query, ConversationService service, CancellationToken ct) =>
                service.ListAsync(query.Page ?? 1, query.PageSize ?? 30, ct))
            .WithSummary("Your assistant conversations, most recent first");
        conversations.MapGet("/{id:guid}", (Guid id, ConversationService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("One of your conversations with its questions and answers (sources re-checked against your access)")
            .ProducesProblem(StatusCodes.Status404NotFound);
        conversations.MapDelete("/{id:guid}", async (Guid id, ConversationService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete one of your conversations")
            .ProducesProblem(StatusCodes.Status404NotFound);

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
}
