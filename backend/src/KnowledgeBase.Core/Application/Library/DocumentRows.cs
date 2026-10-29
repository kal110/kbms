using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;

namespace KnowledgeBase.Core.Application.Library;

/// <summary>Flat SQL projection of a document list row; <see cref="ToItem"/> shapes it in memory.</summary>
public sealed record DocumentRow
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? DocumentNumber { get; init; }
    public Guid TypeId { get; init; }
    public string TypeCode { get; init; } = "";
    public string TypeName { get; init; } = "";
    public AcademicTag TypeTag { get; init; }
    public Guid FolderId { get; init; }
    public string FolderName { get; init; } = "";
    public FolderKind FolderKind { get; init; }
    public Guid OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
    public LifecycleStatus Lifecycle { get; init; }
    public bool InReview { get; init; }
    public Guid? PublishedVersionId { get; init; }
    public int? PublishedNumber { get; init; }
    public Guid? CurrentVersionId { get; init; }
    public int? CurrentNumber { get; init; }
    public string? CurrentFileName { get; init; }
    public string? CurrentContentType { get; init; }
    public long? CurrentSize { get; init; }
    public int? CurrentPages { get; init; }
    public VersionProcessingStatus? CurrentStatus { get; init; }
    public DateTimeOffset? CurrentUploadedAt { get; init; }
    public Guid? YearId { get; init; }
    public string? YearCode { get; init; }
    public string? YearName { get; init; }
    public Guid? TermId { get; init; }
    public string? TermCode { get; init; }
    public string? TermName { get; init; }
    public string? TermShortName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public DocumentState State =>
        Lifecycle == LifecycleStatus.Archived ? DocumentState.Archived
        : InReview ? DocumentState.InReview
        : PublishedVersionId is not null ? DocumentState.Published
        : DocumentState.Draft;

    public DocumentListItem ToItem() => new(
        Id,
        Title,
        DocumentNumber,
        new DocumentTypeRef(TypeId, TypeCode, TypeName, TypeTag),
        new FolderRef(FolderId, FolderName, FolderKind),
        new PersonRef(OwnerId, OwnerName),
        Lifecycle,
        State,
        CurrentVersionId is { } vid
            ? new VersionRef(vid, CurrentNumber ?? 0, CurrentFileName ?? "", CurrentContentType ?? "", CurrentSize ?? 0,
                CurrentPages, CurrentStatus ?? VersionProcessingStatus.Pending, CurrentUploadedAt ?? default)
            : null,
        PublishedNumber,
        Period,
        UpdatedAt);

    public PeriodRef Period => new(YearId, YearCode, YearName, TermId, TermCode, TermName, TermShortName);
}

public static class DocumentRowQueries
{
    /// <summary>Submissions that still hold their documents for review.</summary>
    public static readonly SubmissionStatus[] ActiveSubmissionStatuses =
        [SubmissionStatus.Submitted, SubmissionStatus.UnderReview, SubmissionStatus.RevisionRequested, SubmissionStatus.Resubmitted];

    public static IQueryable<DocumentRow> ToRows(this IQueryable<Document> documents, KbDbContext db)
    {
        var active = ActiveSubmissionStatuses;
        return documents.Select(d => new DocumentRow
        {
            Id = d.Id,
            Title = d.Title,
            Description = d.Description,
            DocumentNumber = d.DocumentNumber,
            TypeId = d.DocumentTypeId,
            TypeCode = d.DocumentType.Code,
            TypeName = d.DocumentType.Name,
            TypeTag = d.DocumentType.AcademicTag,
            FolderId = d.FolderId,
            FolderName = d.Folder.Name,
            FolderKind = d.Folder.Kind,
            OwnerId = d.OwnerUserId,
            OwnerName = d.Owner.DisplayName,
            Lifecycle = d.LifecycleStatus,
            InReview = db.SubmissionDocuments.Any(sd => sd.DocumentId == d.Id
                && db.Submissions.Any(s => s.Id == sd.SubmissionId && active.Contains(s.Status))),
            PublishedVersionId = d.PublishedVersionId,
            PublishedNumber = (int?)d.PublishedVersion!.VersionNumber,
            CurrentVersionId = d.CurrentVersionId,
            CurrentNumber = (int?)d.CurrentVersion!.VersionNumber,
            CurrentFileName = d.CurrentVersion!.OriginalFilename,
            CurrentContentType = d.CurrentVersion!.ContentType,
            CurrentSize = (long?)d.CurrentVersion!.FileSizeBytes,
            CurrentPages = d.CurrentVersion!.PageCount,
            CurrentStatus = (VersionProcessingStatus?)d.CurrentVersion!.ProcessingStatus,
            CurrentUploadedAt = (DateTimeOffset?)d.CurrentVersion!.UploadedAt,
            YearId = d.AcademicYearId,
            YearCode = d.AcademicYear!.Code,
            YearName = d.AcademicYear!.Name,
            TermId = d.AcademicTermId,
            TermCode = d.AcademicTerm!.Code,
            TermName = d.AcademicTerm!.Name,
            TermShortName = d.AcademicTerm!.ShortName,
            UpdatedAt = d.UpdatedAt,
        });
    }

    /// <summary>Filters by the status chip, in SQL.</summary>
    public static IQueryable<Document> WhereState(this IQueryable<Document> documents, KbDbContext db, DocumentState state)
    {
        var active = ActiveSubmissionStatuses;
        IQueryable<Document> NotArchived() => documents.Where(d => d.LifecycleStatus != LifecycleStatus.Archived);
        return state switch
        {
            DocumentState.Archived => documents.Where(d => d.LifecycleStatus == LifecycleStatus.Archived),
            DocumentState.InReview => NotArchived().Where(d => db.SubmissionDocuments.Any(sd => sd.DocumentId == d.Id
                && db.Submissions.Any(s => s.Id == sd.SubmissionId && active.Contains(s.Status)))),
            DocumentState.Published => NotArchived().Where(d => d.PublishedVersionId != null && !db.SubmissionDocuments.Any(sd => sd.DocumentId == d.Id
                && db.Submissions.Any(s => s.Id == sd.SubmissionId && active.Contains(s.Status)))),
            _ => NotArchived().Where(d => d.PublishedVersionId == null && !db.SubmissionDocuments.Any(sd => sd.DocumentId == d.Id
                && db.Submissions.Any(s => s.Id == sd.SubmissionId && active.Contains(s.Status)))),
        };
    }
}
