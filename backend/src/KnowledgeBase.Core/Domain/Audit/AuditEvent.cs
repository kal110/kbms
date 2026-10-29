using System.Text.Json;
using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Domain.Audit;

/// <summary>Who did what to which resource; every write and every download records one.</summary>
public sealed class AuditEvent : Entity
{
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }

    /// <summary>e.g. "document.downloaded", "submission.approved", "settings.document-type.updated".</summary>
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? VersionId { get; set; }
    public string? IpAddress { get; set; }
    public JsonDocument? Metadata { get; set; }
}
