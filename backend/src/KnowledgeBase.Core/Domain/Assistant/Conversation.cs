using System.Text.Json;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Audit;

namespace KnowledgeBase.Core.Domain.Assistant;

/// <summary>
/// One person's chat with the AI assistant (frame 5), kept so they can come back to it. Only its
/// owner reads or deletes it; there is no admin view. Opening it again re-checks access to the
/// documents its answers cite.
/// </summary>
public sealed class Conversation : Entity, IHasTimestamps
{
    public Guid UserId { get; set; }

    /// <summary>The first question, shortened.</summary>
    public required string Title { get; set; }

    /// <summary>Questions asked so far; the next turn's number is one more.</summary>
    public int TurnCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ConversationTurn> Turns { get; set; } = [];
}

/// <summary>
/// A question and its answer. Saved when the question is asked; Outcome stays null while the
/// answer streams, and for good when the stream was cut off (shown as interrupted).
/// </summary>
public sealed class ConversationTurn : Entity
{
    public Guid ConversationId { get; set; }

    /// <summary>1, 2, 3… UNIQUE (ConversationId, Number).</summary>
    public int Number { get; set; }
    public DateTimeOffset AskedAt { get; set; }
    public required string Question { get; set; }

    /// <summary>"published" or "my-drafts".</summary>
    public required string Scope { get; set; }
    public string? Answer { get; set; }
    public AiQueryOutcome? Outcome { get; set; }

    /// <summary>Why a Failed answer failed, as the person saw it.</summary>
    public string? Error { get; set; }

    /// <summary>The sources listed with the answer (AssistantSource[]).</summary>
    public JsonDocument? Sources { get; set; }
    public string? Model { get; set; }

    /// <summary>The answer's AiQueryAudit row.</summary>
    public Guid? QueryId { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}
