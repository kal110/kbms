using KnowledgeBase.Core.Domain.Assistant;
using KnowledgeBase.Core.Domain.Audit;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Configurations;

internal sealed class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> b)
    {
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.DocumentVersionId);
        b.HasIndex(x => new { x.DocumentVersionId, x.PipelineVersion }).IsUnique();
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.Property(x => x.Type).HasMaxLength(80);
        b.Property(x => x.LastError).HasMaxLength(1000);
        // Only unsent rows are indexed: the dispatcher polls these.
        b.HasIndex(x => x.OccurredAt).HasFilter("processed_at IS NULL").HasDatabaseName("ix_outbox_messages_unsent");
    }
}

internal sealed class IndexBuildConfiguration : IEntityTypeConfiguration<IndexBuild>
{
    public void Configure(EntityTypeBuilder<IndexBuild> b)
    {
        b.Property(x => x.Collection).HasMaxLength(120);
        b.Property(x => x.Model).HasMaxLength(120);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasIndex(x => x.Collection).IsUnique();
        // At most one build in flight; the worker fills one collection at a time.
        b.HasIndex(x => x.Status).IsUnique().HasFilter("status IN ('Pending', 'Building')").HasDatabaseName("ux_index_builds_in_flight");
    }
}

internal sealed class VersionIndexEntryConfiguration : IEntityTypeConfiguration<VersionIndexEntry>
{
    public void Configure(EntityTypeBuilder<VersionIndexEntry> b)
    {
        b.HasKey(x => new { x.Collection, x.VersionId });
        b.Property(x => x.Collection).HasMaxLength(120);
        b.Property(x => x.PayloadHash).HasMaxLength(64);
        b.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.VersionId);
        b.HasIndex(x => x.DocumentId);
    }
}

internal sealed class AiQueryAuditConfiguration : IEntityTypeConfiguration<AiQueryAudit>
{
    public void Configure(EntityTypeBuilder<AiQueryAudit> b)
    {
        b.Property(x => x.Scope).HasMaxLength(20);
        b.Property(x => x.Model).HasMaxLength(80);
        b.Property(x => x.QuestionText).HasMaxLength(2000);
        b.HasIndex(x => x.AskedAt).HasMethod("brin");
        b.HasIndex(x => new { x.UserId, x.AskedAt });
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.Property(x => x.Title).HasMaxLength(120);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Turns).WithOne().HasForeignKey(t => t.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.UpdatedAt });
    }
}

internal sealed class ConversationTurnConfiguration : IEntityTypeConfiguration<ConversationTurn>
{
    public void Configure(EntityTypeBuilder<ConversationTurn> b)
    {
        b.Property(x => x.Question).HasMaxLength(2000);
        b.Property(x => x.Scope).HasMaxLength(20);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.Property(x => x.Model).HasMaxLength(80);
        b.HasIndex(x => new { x.ConversationId, x.Number }).IsUnique();
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.HasKey(x => new { x.UserId, x.Key, x.Route });
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Route).HasMaxLength(300);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ResponseContentType).HasMaxLength(120);
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.Property(x => x.Action).HasMaxLength(80);
        b.Property(x => x.ResourceType).HasMaxLength(60);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        // Append-heavy and queried by time range: BRIN on occurred_at (monthly partitioning is a later step).
        b.HasIndex(x => x.OccurredAt).HasMethod("brin");
        b.HasIndex(x => new { x.ResourceType, x.ResourceId });
        b.HasIndex(x => new { x.ActorUserId, x.OccurredAt });
    }
}
