using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Configurations;

internal sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Path).HasMaxLength(2000);
        b.HasOne(x => x.Parent).WithMany(p => p.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ParentId);
        b.HasIndex(x => x.Path).HasOperators("text_pattern_ops");
        // System folders are unique per parent, kind and org record (frames 22, 24).
        b.HasIndex(x => new { x.ParentId, x.Kind, x.OrgRefId }).IsUnique().HasFilter("is_system").HasDatabaseName("ux_folders_system");
    }
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    /// <summary>Shadow tsvector column for keyword search (title, number, description).</summary>
    public const string SearchVector = "SearchVector";

    public void Configure(EntityTypeBuilder<Document> b)
    {
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.DocumentNumber).HasMaxLength(60);

        b.HasOne(x => x.DocumentType).WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AcademicYear).WithMany().HasForeignKey(x => x.AcademicYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AcademicTerm).WithMany().HasForeignKey(x => x.AcademicTermId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Versions).WithOne(v => v.Document).HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CurrentVersion).WithMany().HasForeignKey(x => x.CurrentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PublishedVersion).WithMany().HasForeignKey(x => x.PublishedVersionId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.FolderId, x.LifecycleStatus, x.UpdatedAt });
        b.HasIndex(x => new { x.DocumentTypeId, x.DocumentNumber }).IsUnique().HasFilter("document_number IS NOT NULL");
        b.HasIndex(x => x.OwnerUserId);

        b.Property<NpgsqlTsVector>(SearchVector)
            .HasColumnName("search_vector")
            .HasComputedColumnSql(
                "to_tsvector('simple', coalesce(title, '') || ' ' || coalesce(document_number, '') || ' ' || coalesce(description, ''))",
                stored: true);
        b.HasIndex(SearchVector).HasMethod("GIN");
        b.HasIndex(x => x.Title).HasMethod("GIN").HasOperators("gin_trgm_ops").HasDatabaseName("ix_documents_title_trgm");
        b.HasIndex(x => x.DocumentNumber).HasMethod("GIN").HasOperators("gin_trgm_ops").HasDatabaseName("ix_documents_number_trgm");
    }
}

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> b)
    {
        b.Property(x => x.ObjectKey).HasMaxLength(500);
        b.Property(x => x.OriginalFilename).HasMaxLength(300);
        b.Property(x => x.ContentType).HasMaxLength(120);
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.ChangeDescription).HasMaxLength(1000);
        b.Property(x => x.TextObjectKey).HasMaxLength(500);
        b.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
        b.HasIndex(x => x.ObjectKey).IsUnique();
        // Target of the composite FK from submission_documents: the version must belong to the document.
        b.HasAlternateKey(x => new { x.DocumentId, x.Id });
        b.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.CreatedFromVersionId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_document_versions_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_document_versions_size", "file_size_bytes > 0");
            t.HasCheckConstraint("ck_document_versions_number", "version_number >= 1");
        });
    }
}

internal sealed class VersionPageConfiguration : IEntityTypeConfiguration<VersionPage>
{
    public void Configure(EntityTypeBuilder<VersionPage> b)
    {
        b.HasKey(x => new { x.VersionId, x.PageNumber });
        b.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
        // Exact search is ILIKE '%phrase%'; trigrams keep it off a sequential scan.
        b.HasIndex(x => x.Text).HasMethod("GIN").HasOperators("gin_trgm_ops").HasDatabaseName("ix_version_pages_text_trgm");
    }
}

internal sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class CollectionDocumentConfiguration : IEntityTypeConfiguration<CollectionDocument>
{
    public void Configure(EntityTypeBuilder<CollectionDocument> b)
    {
        b.HasKey(x => new { x.CollectionId, x.DocumentId });
        b.HasOne(x => x.Collection).WithMany(c => c.Documents).HasForeignKey(x => x.CollectionId);
        b.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId);
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> b)
    {
        b.Property(x => x.Name).HasMaxLength(80);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class DocumentTagConfiguration : IEntityTypeConfiguration<DocumentTag>
{
    public void Configure(EntityTypeBuilder<DocumentTag> b)
    {
        b.HasKey(x => new { x.DocumentId, x.TagId });
        b.HasOne(x => x.Document).WithMany(d => d.Tags).HasForeignKey(x => x.DocumentId);
        b.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId);
    }
}

internal sealed class LegalHoldConfiguration : IEntityTypeConfiguration<LegalHold>
{
    public void Configure(EntityTypeBuilder<LegalHold> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasIndex(x => x.DocumentId);
    }
}

internal sealed class PublicationRecordConfiguration : IEntityTypeConfiguration<PublicationRecord>
{
    public void Configure(EntityTypeBuilder<PublicationRecord> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.DocumentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DocumentId, x.CreatedAt });
    }
}

internal sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> b)
    {
        b.Property(x => x.ObjectKey).HasMaxLength(500);
        b.Property(x => x.FileName).HasMaxLength(300);
        b.Property(x => x.ContentType).HasMaxLength(120);
        b.Property(x => x.ExpectedSha256).HasMaxLength(64);
        b.Property(x => x.S3UploadId).HasMaxLength(200);
        b.HasIndex(x => x.ObjectKey).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Status });
    }
}

internal sealed class DocumentPermissionConfiguration : IEntityTypeConfiguration<DocumentPermission>
{
    public void Configure(EntityTypeBuilder<DocumentPermission> b)
    {
        b.Property(x => x.PrincipalKey).HasMaxLength(80);
        b.HasIndex(x => new { x.ResourceType, x.ResourceId, x.PrincipalKey }).IsUnique();
        b.HasIndex(x => x.PrincipalKey);
    }
}

internal sealed class FolderEffectiveAccessConfiguration : IEntityTypeConfiguration<FolderEffectiveAccess>
{
    public void Configure(EntityTypeBuilder<FolderEffectiveAccess> b)
    {
        b.ToTable("folder_effective_access");
        b.HasKey(x => new { x.FolderId, x.PrincipalKey });
        b.Property(x => x.PrincipalKey).HasMaxLength(80);
        b.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId);
        b.HasIndex(x => new { x.PrincipalKey, x.Level });
    }
}
