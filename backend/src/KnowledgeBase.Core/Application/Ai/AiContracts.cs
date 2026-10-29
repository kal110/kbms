namespace KnowledgeBase.Core.Application.Ai;

// Permission-aware AI (frames 5, 8, 10, 25). Ai:Enabled switches on chunking, embedding and the
// vector index in the processing pipeline, the index worker and /api/knowledge/chat. The providers
// behind these contracts are swappable: any OpenAI-compatible embeddings endpoint (Ollama, TEI,
// vLLM, a hosted API), Qdrant for vectors, and for answers a local model (Ollama or another
// OpenAI-compatible server) or Claude.

public sealed class AiOptions
{
    public const string Section = "Ai";

    public bool Enabled { get; set; }
    public EmbeddingOptions Embedding { get; set; } = new();
    public VectorStoreOptions VectorStore { get; set; } = new();
    public ChunkingOptions Chunking { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public ChatOptions Chat { get; set; } = new();
    public AiAuditOptions Audit { get; set; } = new();

    /// <summary>How often the index worker backfills, follows builds and syncs ACL payloads.</summary>
    public int IndexSyncSeconds { get; set; } = 30;
}

public sealed class EmbeddingOptions
{
    /// <summary>Base URL of an OpenAI-compatible API; POST {Endpoint}/embeddings. Ollama: http://localhost:11434/v1.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string? ApiKey { get; set; }

    /// <summary>Stored with every point; a query must use the same model as the active collection.</summary>
    public string Model { get; set; } = "bge-m3";
    public int Dimensions { get; set; } = 1024;

    /// <summary>Texts per request (frame 8, step 10: batches of 64).</summary>
    public int BatchSize { get; set; } = 64;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class VectorStoreOptions
{
    /// <summary>Qdrant REST endpoint.</summary>
    public string Url { get; set; } = "http://localhost:6333";
    public string? ApiKey { get; set; }

    /// <summary>Searches go through this alias; builds create "{alias}_v{n}" collections behind it.</summary>
    public string CollectionAlias { get; set; } = "kb_chunks";
}

/// <summary>
/// Chunk sizes (frame 25). Changing them changes the chunks, so bump Processing:PipelineVersion
/// and rebuild the index afterwards.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>A chunk closes at the next paragraph end once it reaches this many tokens.</summary>
    public int TargetTokens { get; set; } = 500;

    /// <summary>Hard ceiling: nothing longer is embedded; longer paragraphs split at sentences.</summary>
    public int MaxTokens { get; set; } = 800;

    /// <summary>Tail of the previous chunk repeated at the start of the next one in the same section.</summary>
    public int OverlapTokens { get; set; } = 80;

    /// <summary>A heading starts a new chunk once the current one has at least this many tokens.</summary>
    public int MinTokens { get; set; } = 120;

    /// <summary>A page with less text than this (and images) is flagged for OCR (frame 8, step 7).</summary>
    public int LowTextChars { get; set; } = 40;
}

public sealed class RetrievalOptions
{
    /// <summary>Candidates from each of the dense and keyword searches before fusion (frame 10, step 5).</summary>
    public int Candidates { get; set; } = 40;

    /// <summary>Passages given to the model (frame 10, step 6).</summary>
    public int Passages { get; set; } = 8;
    public int MaxPassagesPerDocument { get; set; } = 3;

    /// <summary>Best dense cosine similarity below this → "not enough evidence" (frame 10, step 11).</summary>
    public double MinScore { get; set; } = 0.45;

    /// <summary>
    /// With enough evidence, passages scoring below this are left out, so weak keyword matches
    /// don't reach the model or the source list. Without enough evidence, the closest passages are kept.
    /// </summary>
    public double MinPassageScore { get; set; } = 0.40;
    public int MaxQuestionChars { get; set; } = 2000;
    public int RequestsPerMinute { get; set; } = 20;
}

/// <summary>Who writes the answers.</summary>
public enum ChatProvider
{
    /// <summary>A model on this machine or network, behind an OpenAI-compatible chat API (Ollama by default).</summary>
    Local,

    /// <summary>Claude over the Anthropic API; needs an API key.</summary>
    Anthropic,
}

public sealed class ChatOptions
{
    public ChatProvider Provider { get; set; } = ChatProvider.Local;
    public LocalChatOptions Local { get; set; } = new();
    public AnthropicChatOptions Anthropic { get; set; } = new();

    /// <summary>The model of the active provider, as status pages and the audit show it.</summary>
    public string Model => Provider == ChatProvider.Anthropic ? Anthropic.Model : Local.Model;
}

public sealed class LocalChatOptions
{
    /// <summary>Base URL of an OpenAI-compatible API; POST {Endpoint}/chat/completions. Ollama: http://localhost:11434/v1.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string? ApiKey { get; set; }

    /// <summary>A small instruct model that fits the GPU. Ollama: pulled into the server first (ollama pull).</summary>
    public string Model { get; set; } = "qwen3.5:4b";
    public int MaxTokens { get; set; } = 1500;

    /// <summary>Low, so answers stay close to the passages.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>
    /// Sent as reasoning_effort. "none" switches off a thinking model's reasoning, which on a small
    /// GPU costs more time than it adds to quoting passages. Empty = the server's default.
    /// </summary>
    public string? ReasoningEffort { get; set; } = "none";

    /// <summary>
    /// How long to wait for the answer to start: loading the model and reading the passages. The
    /// server's context window must hold them (Ollama: OLLAMA_CONTEXT_LENGTH, 16384 in compose.yaml).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class AnthropicChatOptions
{
    /// <summary>Claude model id.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Anthropic API key; empty = the ANTHROPIC_API_KEY environment variable.</summary>
    public string? ApiKey { get; set; }
    public int MaxTokens { get; set; } = 16000;

    /// <summary>low | medium | high | xhigh | max. Grounded Q&amp;A rarely needs more than medium.</summary>
    public string Effort { get; set; } = "medium";
}

public sealed class AiAuditOptions
{
    /// <summary>Keep the question and answer text in ai_query_audits (retention policy permitting).</summary>
    public bool KeepText { get; set; }
}

/// <summary>A hosted API or a local model (Ollama / TEI) behind an OpenAI-compatible endpoint.</summary>
public interface IEmbeddingProvider
{
    string ModelId { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>Vendor-neutral chat model. Only ever receives passages re-authorized against PostgreSQL.</summary>
public interface IChatModel
{
    string ModelId { get; }
    IAsyncEnumerable<ChatModelEvent> StreamAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
}

/// <summary>
/// A text delta while streaming, then one final event with the stop reason and usage. Stop reasons
/// use Anthropic's names whatever the provider: end_turn, max_tokens, refusal.
/// </summary>
public sealed record ChatModelEvent(string? Text, string? StopReason = null, long InputTokens = 0, long OutputTokens = 0, string? Model = null)
{
    public bool IsFinal => StopReason is not null;
}

/// <summary>A chat model failure with a message safe to show (no keys, no request content).</summary>
public sealed class ChatModelException(string message) : Exception(message);

/// <summary>The assistant's two use cases (frame 10): they share one collection but never one filter.</summary>
public static class KnowledgeScopes
{
    /// <summary>Published versions of documents the caller can open (the official knowledge base).</summary>
    public const string Published = "published";

    /// <summary>The caller's own unpublished current versions (private draft help).</summary>
    public const string MyDrafts = "my-drafts";

    public static string Normalize(string? scope) => scope == MyDrafts ? MyDrafts : Published;
}

/// <summary>A BM25-style sparse vector: hashed term ids and their saturated term frequencies.</summary>
public sealed record SparseVector(uint[] Indices, float[] Values);

/// <summary>One chunk as stored in the vector index.</summary>
public sealed record VectorPoint(Guid Id, float[] Dense, SparseVector Sparse, IReadOnlyDictionary<string, object?> Payload);

/// <summary>What a search may return: the caller's principals plus the scope and UI filters (frame 10).</summary>
public sealed record VectorSearchFilter(
    IReadOnlyCollection<string> PrincipalKeys,
    string Scope,
    Guid? OwnerId,
    Guid? FolderId,
    Guid? DocumentTypeId,
    Guid? AcademicYearId,
    Guid? AcademicTermId);

public sealed record VectorHit(Guid PointId, double DenseScore, double FusedScore, IReadOnlyDictionary<string, System.Text.Json.JsonElement> Payload);

public sealed record VectorCollectionInfo(string Name, int Dimensions, long Points, string Status);

/// <summary>
/// Qdrant (or another vector store). Collections are versioned ("kb_chunks_v2") behind an alias;
/// a rebuild fills a new collection and swaps the alias. The index can only narrow results:
/// PostgreSQL re-checks every hit before anything reaches the model (frame 10, step 7).
/// </summary>
public interface IVectorIndex
{
    Task<VectorCollectionInfo?> GetCollectionAsync(string collection, CancellationToken ct = default);
    Task CreateCollectionAsync(string collection, int dimensions, CancellationToken ct = default);
    Task DeleteCollectionAsync(string collection, CancellationToken ct = default);
    Task PointAliasAsync(string alias, string collection, CancellationToken ct = default);
    Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct = default);

    /// <summary>Deletes a version's points, or only those from other pipeline versions.</summary>
    Task DeleteVersionAsync(string collection, Guid versionId, int? keepPipelineVersion = null, CancellationToken ct = default);

    /// <summary>Overwrites the given payload fields on every point of a version (set_payload by filter).</summary>
    Task SetVersionPayloadAsync(string collection, Guid versionId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct = default);

    /// <summary>Dense and keyword searches with the same filter, fused by reciprocal rank.</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] dense, SparseVector sparse, VectorSearchFilter filter, int limit, CancellationToken ct = default);
}
