using System.Text.Json;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Audit;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Ai;

public sealed record ConversationSummary(Guid Id, string Title, int Turns, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Answer">The final answer; null while it streams, when the stream was cut off, or when it failed (see Error).</param>
/// <param name="Sources">The sources listed with the answer: its citations, or the closest passages.</param>
public sealed record ConversationTurnDto(
    Guid Id,
    int Number,
    string Question,
    string Scope,
    DateTimeOffset AskedAt,
    AssistantAnswer? Answer,
    string? Error,
    IReadOnlyList<AssistantSource> Sources);

public sealed record ConversationDetail(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ConversationTurnDto> Turns);

/// <summary>
/// The caller's own assistant conversations (frame 5). Nobody else can list, open or delete them —
/// another person's conversation is a 404, administrators included. Sources are re-checked when a
/// conversation opens: a document the caller can no longer open keeps its number but loses its
/// title and passage.
/// </summary>
public sealed class ConversationService(KbDbContext db, ICurrentUser currentUser, IAccessEvaluator access)
{
    public const string NoLongerAvailable = "A document you can no longer open";

    public async Task<PagedResult<ConversationSummary>> ListAsync(int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        return await db.Conversations.AsNoTracking()
            .Where(c => c.UserId == me.UserId)
            .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.TurnCount, c.CreatedAt, c.UpdatedAt))
            .ToPagedAsync(new PageRequest(page, pageSize), ct);
    }

    public async Task<ConversationDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var conversation = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == id && c.UserId == me.UserId)
            .Select(c => new { c.Id, c.Title, c.CreatedAt, c.UpdatedAt })
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Conversation", id);
        var turns = await db.ConversationTurns.AsNoTracking()
            .Where(t => t.ConversationId == id)
            .OrderBy(t => t.Number)
            .ToListAsync(ct);

        var sources = turns.ToDictionary(t => t.Id, t => t.Sources is null
            ? []
            : t.Sources.Deserialize<List<AssistantSource>>(KbJson.Options) ?? []);
        var cited = sources.Values.SelectMany(s => s).Select(s => s.DocumentId).Distinct().ToArray();
        var visible = (await access.VisibleDocuments(me).Where(d => cited.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct)).ToHashSet();

        var dtos = turns.Select(t =>
        {
            var shown = sources[t.Id].Select(s => visible.Contains(s.DocumentId) ? s : Redact(s)).ToList();
            var answer = t.Outcome is { } outcome && outcome != AiQueryOutcome.Failed
                ? new AssistantAnswer(t.QueryId ?? Guid.Empty, t.Answer ?? "", outcome, shown, t.Model)
                : null;
            return new ConversationTurnDto(t.Id, t.Number, t.Question, t.Scope, t.AskedAt, answer, t.Error, shown);
        }).ToList();
        return new ConversationDetail(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt, dtos);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var deleted = await db.Conversations.Where(c => c.Id == id && c.UserId == me.UserId).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw new NotFoundException("Conversation", id);
    }

    private static AssistantSource Redact(AssistantSource s) =>
        s with { Title = NoLongerAvailable, DocumentNumber = null, DocumentType = null, Section = null, Snippet = "", Passage = null };
}
