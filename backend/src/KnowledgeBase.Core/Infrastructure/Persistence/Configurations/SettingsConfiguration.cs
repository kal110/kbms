using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Configurations;

internal sealed class DocumentTypeConfiguration : IEntityTypeConfiguration<DocumentType>
{
    public void Configure(EntityTypeBuilder<DocumentType> b)
    {
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(12);
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.NumberPattern).HasMaxLength(60);
        b.HasOne(x => x.RetentionPolicy).WithMany().HasForeignKey(x => x.RetentionPolicyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.ReplacedByTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> b)
    {
        b.Property(x => x.Name).HasMaxLength(120);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class DocumentNumberSequenceConfiguration : IEntityTypeConfiguration<DocumentNumberSequence>
{
    public void Configure(EntityTypeBuilder<DocumentNumberSequence> b)
    {
        b.HasKey(x => new { x.DocumentTypeId, x.Year });
        b.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeId);
    }
}

internal sealed class SubmissionTypeConfiguration : IEntityTypeConfiguration<SubmissionType>
{
    public void Configure(EntityTypeBuilder<SubmissionType> b)
    {
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.Description).HasMaxLength(500);
    }
}

internal sealed class ApprovalStageConfiguration : IEntityTypeConfiguration<ApprovalStage>
{
    public void Configure(EntityTypeBuilder<ApprovalStage> b)
    {
        b.HasOne(x => x.SubmissionType).WithMany(t => t.Stages).HasForeignKey(x => x.SubmissionTypeId);
        b.HasIndex(x => new { x.SubmissionTypeId, x.StageOrder }).IsUnique();
    }
}

internal sealed class RequirementSetConfiguration : IEntityTypeConfiguration<RequirementSet>
{
    public void Configure(EntityTypeBuilder<RequirementSet> b)
    {
        b.HasOne(x => x.SubmissionType).WithMany().HasForeignKey(x => x.SubmissionTypeId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => new { x.ScopeType, x.ScopeId, x.SubmissionTypeId, x.SetVersion }).IsUnique();
        // At most one draft per scope and submission type.
        b.HasIndex(x => new { x.ScopeType, x.ScopeId, x.SubmissionTypeId }).IsUnique()
            .HasFilter("status = 'Draft'").HasDatabaseName("ux_requirement_sets_one_draft");
    }
}

internal sealed class SubmissionRequirementConfiguration : IEntityTypeConfiguration<SubmissionRequirement>
{
    public void Configure(EntityTypeBuilder<SubmissionRequirement> b)
    {
        b.HasOne(x => x.RequirementSet).WithMany(s => s.Requirements).HasForeignKey(x => x.RequirementSetId);
        b.HasOne(x => x.DocumentType).WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Key).HasMaxLength(60);
        b.Property(x => x.Label).HasMaxLength(200);
        b.HasIndex(x => new { x.RequirementSetId, x.Key }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_submission_requirements_files", "min_files >= 0 AND max_files >= 1 AND max_files >= min_files"));
    }
}
