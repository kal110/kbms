using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Domain.Audit;
using KnowledgeBase.Core.Domain.Processing;

namespace KnowledgeBase.Core.Infrastructure.Persistence;

public static class KbJson
{
    // Enums by name, like the API: audit metadata and outbox payloads are read by people, not just code.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static JsonDocument ToDocument(object value) => JsonSerializer.SerializeToDocument(value, Options);
}

/// <summary>Adds audit rows to the caller's unit of work, so they commit with the change.</summary>
public sealed class AuditWriter(KbDbContext db, ICurrentUser currentUser, TimeProvider clock) : IAuditWriter
{
    public void Record(string action, string resourceType, Guid? resourceId, Guid? versionId = null, object? metadata = null, Guid? actorUserId = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = clock.GetUtcNow(),
            ActorUserId = actorUserId ?? currentUser.LoadedUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            VersionId = versionId,
            IpAddress = currentUser.IpAddress,
            Metadata = metadata is null ? null : KbJson.ToDocument(metadata),
        });
    }
}

/// <summary>Adds outbox rows to the caller's unit of work (same PostgreSQL transaction).</summary>
public sealed class Outbox(KbDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(string type, object payload)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = type,
            Payload = KbJson.ToDocument(payload),
            OccurredAt = clock.GetUtcNow(),
        });
    }
}
