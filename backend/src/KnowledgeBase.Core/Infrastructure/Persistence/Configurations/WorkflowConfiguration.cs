using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Configurations;

internal sealed class SubmissionConfiguration : IEntityTypeConfiguration<Submission>
{
    public void Configure(EntityTypeBuilder<Submission> b)
    {
        b.Property(x => x.SubmissionNumber).HasMaxLength(30);
        b.Property(x => x.Title).HasMaxLength(300);
        b.HasIndex(x => x.SubmissionNumber).IsUnique();

        b.HasOne(x => x.SubmittedBy).WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AssignedReviewer).WithMany().HasForeignKey(x => x.AssignedReviewerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Program).WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AcademicTerm).WithMany().HasForeignKey(x => x.AcademicTermId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Domain.Calendar.AcademicYear>().WithMany().HasForeignKey(x => x.AcademicYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SubmissionType).WithMany().HasForeignKey(x => x.SubmissionTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.RequirementSets).WithOne().HasForeignKey(x => x.SubmissionId);
        b.HasMany(x => x.Documents).WithOne().HasForeignKey(x => x.SubmissionId);
        b.HasMany(x => x.Actions).WithOne().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Assignments).WithOne().HasForeignKey(x => x.SubmissionId);

        b.HasIndex(x => new { x.AssignedReviewerId, x.Status, x.SubmittedAt });
        b.HasIndex(x => new { x.SubmittedByUserId, x.Status });
        b.HasIndex(x => new { x.ProgramId, x.AcademicTermId });
        b.HasIndex(x => new { x.CourseId, x.AcademicTermId });
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_submissions_status",
                "status IN ('Draft', 'Submitted', 'UnderReview', 'RevisionRequested', 'Resubmitted', 'Approved', 'Rejected', 'Withdrawn')");
            // A faculty submission belongs to a program; a staff submission to a non-academic department.
            t.HasCheckConstraint("ck_submissions_unit", "program_id IS NOT NULL OR department_id IS NOT NULL");
        });
    }
}

internal sealed class SubmissionRequirementSetConfiguration : IEntityTypeConfiguration<SubmissionRequirementSet>
{
    public void Configure(EntityTypeBuilder<SubmissionRequirementSet> b)
    {
        b.HasKey(x => new { x.SubmissionId, x.RequirementSetId });
        b.HasOne(x => x.RequirementSet).WithMany().HasForeignKey(x => x.RequirementSetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubmissionDocumentConfiguration : IEntityTypeConfiguration<SubmissionDocument>
{
    public void Configure(EntityTypeBuilder<SubmissionDocument> b)
    {
        b.Property(x => x.RequirementKey).HasMaxLength(60);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        // Composite FK: the attached version must belong to the attached document.
        b.HasOne<DocumentVersion>().WithMany()
            .HasForeignKey(x => new { x.DocumentId, x.DocumentVersionId })
            .HasPrincipalKey(v => new { v.DocumentId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SubmissionId, x.RequirementKey });
        b.HasIndex(x => new { x.SubmissionId, x.DocumentVersionId }).IsUnique();
    }
}

internal sealed class ReviewActionConfiguration : IEntityTypeConfiguration<ReviewAction>
{
    public void Configure(EntityTypeBuilder<ReviewAction> b)
    {
        b.Property(x => x.Comments).HasMaxLength(4000);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SubmissionId, x.OccurredAt });
        b.ToTable(t => t.HasCheckConstraint("ck_review_actions_comment",
            "action_type NOT IN ('RevisionRequested', 'Rejected') OR (comments IS NOT NULL AND length(btrim(comments)) > 0)"));
    }
}

internal sealed class SubmissionAssignmentConfiguration : IEntityTypeConfiguration<SubmissionAssignment>
{
    public void Configure(EntityTypeBuilder<SubmissionAssignment> b)
    {
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ReviewerUserId, x.CompletedAt });
        b.HasIndex(x => new { x.SubmissionId, x.StageOrder });
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(x => x.Type).HasMaxLength(80);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Body).HasMaxLength(2000);
        b.Property(x => x.Link).HasMaxLength(500);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        b.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
    }
}
