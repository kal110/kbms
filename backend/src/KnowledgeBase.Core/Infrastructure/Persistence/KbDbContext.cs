using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Audit;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Infrastructure.Persistence;

/// <summary>
/// The authoritative PostgreSQL schema (frame 11). Tables and columns are snake_case, enums are
/// stored as text, and per-module configurations live in ./Configurations.
/// </summary>
public sealed class KbDbContext(DbContextOptions<KbDbContext> options, TimeProvider clock) : DbContext(options)
{
    // Org & identity
    public DbSet<User> Users => Set<User>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<College> Colleges => Set<College>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<AcademicProgram> Programs => Set<AcademicProgram>();
    public DbSet<FacultyProgram> FacultyPrograms => Set<FacultyProgram>();
    public DbSet<DepartmentStaff> DepartmentStaff => Set<DepartmentStaff>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<TeachingAssignment> TeachingAssignments => Set<TeachingAssignment>();
    public DbSet<TeachingLoadImport> TeachingLoadImports => Set<TeachingLoadImport>();

    // Calendar & settings
    public DbSet<AcademicYear> AcademicYears => Set<AcademicYear>();
    public DbSet<AcademicTerm> AcademicTerms => Set<AcademicTerm>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<DocumentNumberSequence> DocumentNumberSequences => Set<DocumentNumberSequence>();

    // Requirements
    public DbSet<SubmissionType> SubmissionTypes => Set<SubmissionType>();
    public DbSet<ApprovalStage> ApprovalStages => Set<ApprovalStage>();
    public DbSet<RequirementSet> RequirementSets => Set<RequirementSet>();
    public DbSet<SubmissionRequirement> SubmissionRequirements => Set<SubmissionRequirement>();

    // Library
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionDocument> CollectionDocuments => Set<CollectionDocument>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<DocumentTag> DocumentTags => Set<DocumentTag>();
    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();
    public DbSet<PublicationRecord> PublicationRecords => Set<PublicationRecord>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    // Access
    public DbSet<DocumentPermission> DocumentPermissions => Set<DocumentPermission>();
    public DbSet<FolderEffectiveAccess> FolderEffectiveAccess => Set<FolderEffectiveAccess>();

    // Workflow
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<SubmissionRequirementSet> SubmissionRequirementSets => Set<SubmissionRequirementSet>();
    public DbSet<SubmissionDocument> SubmissionDocuments => Set<SubmissionDocument>();
    public DbSet<ReviewAction> ReviewActions => Set<ReviewAction>();
    public DbSet<SubmissionAssignment> SubmissionAssignments => Set<SubmissionAssignment>();
    public DbSet<Notification> Notifications => Set<Notification>();

    // Processing, outbox & audit
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IndexBuild> IndexBuilds => Set<IndexBuild>();
    public DbSet<VersionIndexEntry> VersionIndexEntries => Set<VersionIndexEntry>();
    public DbSet<AiQueryAudit> AiQueryAudits => Set<AiQueryAudit>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums as readable text so CHECK constraints and ad-hoc SQL stay legible.
        var enums = typeof(KbDbContext).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Namespace?.StartsWith("KnowledgeBase.Core.Domain", StringComparison.Ordinal) == true);
        foreach (var e in enums) builder.Properties(e).HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("pg_trgm");
        model.ApplyConfigurationsFromAssembly(typeof(KbDbContext).Assembly);

        // Ids are UUIDv7s set in C# (Entity), never by the database. Without this EF treats a new row
        // reached through a navigation (s.Assignments.Add(...)) as existing and UPDATEs it instead of inserting.
        foreach (var entity in model.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            model.Entity(entity.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();

        // xmin as the concurrency token on every versioned entity.
        foreach (var entity in model.Model.GetEntityTypes().Where(t => typeof(IConcurrencyVersioned).IsAssignableFrom(t.ClrType)))
            model.Entity(entity.ClrType).Property(nameof(IConcurrencyVersioned.Version)).IsRowVersion();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void StampTimestamps()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<IHasTimestamps>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default) entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
