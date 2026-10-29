using System.Text.Json;
using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.Audit;

public enum AiQueryOutcome
{
    Answered,
    /// <summary>The evidence guard answered "not enough evidence" (frame 10, step 11).</summary>
    InsufficientEvidence,
    /// <summary>The model declined (stop reason "refusal").</summary>
    Declined,
    Failed,
}

/// <summary>
/// One assistant question (frame 10, step 12): who asked, with which filters, which chunks were
/// retrieved and cited, and how long it took. The question and answer text are kept only when
/// Ai:Audit:KeepText allows it.
/// </summary>
public sealed class AiQueryAudit : Entity
{
    public Guid UserId { get; set; }
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>"published" or "my-drafts".</summary>
    public required string Scope { get; set; }
    public JsonDocument? Filters { get; set; }

    /// <summary>Vector point ids of the passages the model was given, in order.</summary>
    public Guid[] ChunkIds { get; set; } = [];

    /// <summary>Documents the answer cited.</summary>
    public Guid[] CitedDocumentIds { get; set; } = [];
    public AiQueryOutcome Outcome { get; set; }
    public string? Model { get; set; }
    public int LatencyMs { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string? QuestionText { get; set; }
    public string? AnswerText { get; set; }
}
