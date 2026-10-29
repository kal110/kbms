using System.Linq.Expressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Workflow;

/// <summary>The columns every submission view shares, read in one query.</summary>
public sealed record SubmissionHeader(
    Guid Id,
    string Number,
    string Title,
    SubmissionStatus Status,
    TypeRef Type,
    CourseRef? Course,
    TermRef Term,
    PersonRef Submitter,
    UnitInfo Unit,
    int CurrentStage,
    Guid? ReviewerId,
    string? ReviewerName,
    string? ReviewerTitle,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? FinalizedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version)
{
    public PersonRef? Reviewer => ReviewerId is { } id ? new PersonRef(id, ReviewerName ?? "", ReviewerTitle) : null;
}

public static class SubmissionProjections
{
    public static readonly Expression<Func<Submission, SubmissionHeader>> Header = x => new SubmissionHeader(
        x.Id,
        x.SubmissionNumber,
        x.Title,
        x.Status,
        new TypeRef(x.SubmissionType.Id, x.SubmissionType.Code, x.SubmissionType.Name),
        x.CourseId != null ? new CourseRef(x.Course!.Id, x.Course.Code, x.Course.Title) : null,
        new TermRef(x.AcademicTerm.Id, x.AcademicTerm.Code, x.AcademicTerm.Name, x.AcademicTerm.ShortName,
            x.AcademicTerm.AcademicYear.Code, x.AcademicTerm.AcademicYear.Name, x.AcademicTerm.Status),
        new PersonRef(x.SubmittedBy.Id, x.SubmittedBy.DisplayName, x.SubmittedBy.Title),
        new UnitInfo(
            x.ProgramId,
            x.Program!.Code,
            x.Program!.Name,
            x.ProgramId != null ? x.Program!.DepartmentId : x.DepartmentId!.Value,
            x.ProgramId != null ? x.Program!.Department.Code : x.Department!.Code,
            x.ProgramId != null ? x.Program!.Department.Name : x.Department!.Name,
            x.ProgramId != null ? x.Program!.Department.CollegeId : x.Department!.CollegeId,
            x.ProgramId != null ? x.Program!.Department.College!.Code : x.Department!.College!.Code,
            x.ProgramId == null && x.Department!.Kind == DepartmentKind.NonAcademic),
        x.CurrentStage,
        x.AssignedReviewerId,
        x.AssignedReviewer!.DisplayName,
        x.AssignedReviewer!.Title,
        x.SubmittedAt,
        x.FinalizedAt,
        x.CreatedAt,
        x.UpdatedAt,
        x.Version);
}

/// <summary>Read side of the workflow: lists, the detail with its checklist, and the timeline.</summary>
public sealed class SubmissionQueries(
    KbDbContext db,
    ICurrentUser currentUser,
    SubmissionGate gate,
    WorkflowDirectory directory,
    IRequirementResolver requirements)
{
    public async Task<PagedResult<SubmissionRow>> ListAsync(SubmissionListQuery q, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var scope = (q.Scope ?? "mine").Trim().ToLowerInvariant();
        var query = Scoped(db.Submissions.AsNoTracking(), me, scope, q.CollegeId);

        if (q.Status is { } status) query = query.Where(s => s.Status == status);
        else query = query.Where(s => s.Status != SubmissionStatus.Withdrawn);
        if (q.TermId is { } termId) query = query.Where(s => s.AcademicTermId == termId);
        if (q.TypeId is { } typeId) query = query.Where(s => s.SubmissionTypeId == typeId);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var pattern = $"%{EscapeLike(q.Q.Trim())}%";
            query = query.Where(s => EF.Functions.ILike(s.Title, pattern, "\\") || EF.Functions.ILike(s.SubmissionNumber, pattern, "\\"));
        }

        var page = new PageRequest(q.Page, q.PageSize);
        var total = await query.CountAsync(ct);
        var headers = await query
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.SubmissionNumber)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .Select(SubmissionProjections.Header)
            .ToListAsync(ct);

        var ids = headers.Select(h => h.Id).ToList();
        var keys = await db.SubmissionDocuments.AsNoTracking()
            .Where(d => ids.Contains(d.SubmissionId))
            .Select(d => new { d.SubmissionId, d.RequirementKey })
            .ToListAsync(ct);
        var sets = await db.SubmissionRequirementSets.AsNoTracking()
            .Where(r => ids.Contains(r.SubmissionId))
            .Select(r => new { r.SubmissionId, r.RequirementSetId })
            .ToListAsync(ct);

        var rows = new List<SubmissionRow>(headers.Count);
        foreach (var h in headers)
        {
            var attached = keys.Where(k => k.SubmissionId == h.Id).GroupBy(k => k.RequirementKey)
                .ToDictionary(g => g.Key, g => g.Count());
            int? missing = null;
            if (WorkflowRules.IsEditable(h.Status))
            {
                // Only open drafts and revisions show "n required missing"; the rest are complete by rule.
                var checklist = await requirements.GetPinnedChecklistAsync(
                    sets.Where(x => x.SubmissionId == h.Id).Select(x => x.RequirementSetId).ToList(), ct);
                missing = WorkflowRules.MissingRequirements(checklist.Requirements, attached).Count;
            }
            rows.Add(new SubmissionRow(h.Id, h.Number, h.Title, h.Status, h.Type, h.Course, h.Term, h.Submitter, h.Unit.ToRef(),
                h.CurrentStage, h.Reviewer, attached.Values.Sum(), missing, h.SubmittedAt, h.FinalizedAt, h.CreatedAt, h.UpdatedAt));
        }
        return new PagedResult<SubmissionRow>(rows, page.SafePage, page.SafePageSize, total);
    }

    private static IQueryable<Submission> Scoped(IQueryable<Submission> query, UserContext me, string scope, Guid? collegeId)
    {
        var notDraft = (Expression<Func<Submission, bool>>)(s => s.Status != SubmissionStatus.Draft && s.Status != SubmissionStatus.Withdrawn);
        switch (scope)
        {
            case "mine":
                return query.Where(s => s.SubmittedByUserId == me.UserId);
            case "reviewing":
                return query.Where(notDraft).Where(s => s.Assignments.Any(a => a.ReviewerUserId == me.UserId));
            case "unit":
            {
                var programs = me.ScopesOf(Role.AcademicSupervisor).ToList();
                var departments = me.ScopesOf(Role.DepartmentHead).ToList();
                var colleges = me.ScopesOf(Role.Dean).ToList();
                return query.Where(notDraft).Where(s =>
                    (s.ProgramId != null && (programs.Contains(s.ProgramId.Value)
                        || (s.Program!.Department.CollegeId != null && colleges.Contains(s.Program.Department.CollegeId.Value))))
                    || (s.DepartmentId != null && departments.Contains(s.DepartmentId.Value)));
            }
            case "college":
            {
                if (me.IsAdmin)
                    return collegeId is { } any
                        ? query.Where(notDraft).Where(s => s.ProgramId != null && s.Program!.Department.CollegeId == any)
                        : query.Where(notDraft);

                var deanOf = me.ScopesOf(Role.Dean).ToList();
                if (deanOf.Count == 0)
                    throw new ForbiddenException("Only a college Dean or a system administrator can list a college's submissions.");
                if (collegeId is { } c)
                {
                    if (!deanOf.Contains(c)) throw new ForbiddenException("You can only list the submissions of your own college.");
                    deanOf = [c];
                }
                return query.Where(notDraft).Where(s => s.ProgramId != null
                    && s.Program!.Department.CollegeId != null && deanOf.Contains(s.Program.Department.CollegeId.Value));
            }
            default:
                throw new ValidationAppException("scope", "Use mine, reviewing, unit or college.");
        }
    }

    public async Task<SubmissionDetail> GetDetailAsync(Guid id, CancellationToken ct = default) =>
        await BuildDetailAsync(await gate.LoadAsync(id, track: false, ct), ct);

    /// <summary>The detail as the caller sees it now (also returned by every command).</summary>
    public async Task<SubmissionDetail> BuildDetailAsync(GatedSubmission g, CancellationToken ct = default)
    {
        var s = g.Submission;
        var header = await db.Submissions.AsNoTracking().Where(x => x.Id == s.Id).Select(SubmissionProjections.Header).SingleAsync(ct);
        var relation = SubmissionRelation.For(g.Me, s.SubmittedByUserId, s.AssignedReviewerId, s.Status,
            s.Assignments.Select(a => a.ReviewerUserId), g.Unit);

        var setIds = s.RequirementSets.Select(r => r.RequirementSetId).ToList();
        var checklist = await requirements.GetPinnedChecklistAsync(setIds, ct);
        var pinnedSets = await db.RequirementSets.AsNoTracking()
            .Where(r => setIds.Contains(r.Id))
            .Select(r => new PinnedSetRef(r.Id, r.ScopeType, r.ScopeId, r.SetVersion))
            .ToListAsync(ct);

        var actions = await db.ReviewActions.AsNoTracking()
            .Where(a => a.SubmissionId == s.Id)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync(ct);
        var pinnedIds = actions.LastOrDefault(a => a.ActionType is ReviewActionType.Submitted or ReviewActionType.Resubmitted)
            ?.DocumentVersionIds ?? [];
        var reviewedIds = actions.LastOrDefault(a => IsDecision(a.ActionType))?.DocumentVersionIds ?? [];

        var documentIds = s.Documents.Select(d => d.DocumentId).Distinct().ToList();
        var documents = await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Title, d.PublishedVersionId })
            .ToDictionaryAsync(d => d.Id, ct);

        var versionIds = s.Documents.Select(d => d.DocumentVersionId)
            .Concat(pinnedIds).Concat(reviewedIds)
            .Concat(documents.Values.Where(d => d.PublishedVersionId != null).Select(d => d.PublishedVersionId!.Value))
            .Distinct().ToList();
        var versions = await db.DocumentVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.DocumentId, v.VersionNumber, v.OriginalFilename, v.ContentType, v.FileSizeBytes, v.PageCount, v.Sha256, v.UploadedAt })
            .ToDictionaryAsync(v => v.Id, ct);

        int? NumberIn(IEnumerable<Guid> ids, Guid documentId) =>
            ids.Select(id => versions.GetValueOrDefault(id)).FirstOrDefault(v => v?.DocumentId == documentId)?.VersionNumber;

        AttachmentInfo Attachment(SubmissionDocument d)
        {
            var v = versions[d.DocumentVersionId];
            var doc = documents.GetValueOrDefault(d.DocumentId);
            var published = doc?.PublishedVersionId is { } pv ? versions.GetValueOrDefault(pv)?.VersionNumber : null;
            return new AttachmentInfo(d.Id, d.DocumentId, doc?.Title ?? v.OriginalFilename, v.Id, v.VersionNumber, v.OriginalFilename,
                v.ContentType, v.FileSizeBytes, v.PageCount, v.Sha256, v.UploadedAt,
                NumberIn(pinnedIds, d.DocumentId), NumberIn(reviewedIds, d.DocumentId), published);
        }

        var typeIds = checklist.Requirements.Select(r => r.DocumentTypeId).Distinct().ToList();
        var types = await db.DocumentTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.Id))
            .Select(t => new TypeRef(t.Id, t.Code, t.Name))
            .ToDictionaryAsync(t => t.Id, ct);

        var items = new List<ChecklistItem>();
        foreach (var r in checklist.Requirements.DistinctBy(r => r.Key))
        {
            var attached = s.Documents.Where(d => d.RequirementKey == r.Key).OrderBy(d => d.CreatedAt).Select(Attachment).ToList();
            var needed = r.IsRequired ? Math.Max(1, r.MinFiles) : r.MinFiles;
            items.Add(new ChecklistItem(r.Key, r.Label, types.GetValueOrDefault(r.DocumentTypeId), r.IsRequired, r.MinFiles, r.MaxFiles,
                r.DueOn, r.Scope, attached.Count >= needed, attached));
        }
        // Files in a slot the pinned sets no longer have (e.g. seeded or migrated data) still show up.
        var known = checklist.Requirements.Select(r => r.Key).ToHashSet();
        foreach (var orphan in s.Documents.Where(d => !known.Contains(d.RequirementKey)).GroupBy(d => d.RequirementKey))
            items.Add(new ChecklistItem(orphan.Key, orphan.Key, null, false, 0, orphan.Count(), null, null, true,
                orphan.OrderBy(d => d.CreatedAt).Select(Attachment).ToList()));

        // Stages: who reviewed each, who is reviewing now, and who would review next.
        var stageDefs = await directory.GetStagesAsync(s.SubmissionTypeId, ct);
        var routing = await directory.GetRoutingUnitAsync(s.SubmittedByUserId, g.Unit, ct);
        var stageRows = new List<(int Order, Domain.Requirements.ReviewerRule Rule, string State, Guid? Reviewer, Domain.Workflow.AssignmentReason? Reason, DateTimeOffset? CompletedAt)>();
        var earlierReviewers = new HashSet<Guid>();
        foreach (var (order, rule) in stageDefs)
        {
            var assignment = s.Assignments.Where(a => a.StageOrder == order).MaxBy(a => a.AssignedAt);
            string state;
            if (s.Status is SubmissionStatus.Draft) state = "Upcoming";
            else if (s.Status is SubmissionStatus.Approved || order < s.CurrentStage) state = assignment is null ? "Skipped" : "Done";
            else if (order == s.CurrentStage) state = s.Status is SubmissionStatus.Rejected ? "Done" : "Current";
            else state = WorkflowRules.IsFinal(s.Status) ? "Skipped" : "Upcoming";

            Guid? reviewer = assignment?.ReviewerUserId;
            var reason = assignment?.Reason;
            if (reviewer is null && state is "Upcoming" or "Current")
            {
                var predicted = TryResolve(rule, routing);
                if (predicted is null || earlierReviewers.Contains(predicted.ReviewerId)) state = "Skipped";
                else (reviewer, reason) = (predicted.ReviewerId, predicted.Reason);
            }
            if (reviewer is { } r) earlierReviewers.Add(r);
            stageRows.Add((order, rule, state, reviewer, reason, assignment?.CompletedAt));
        }

        var open = s.Assignments.Where(a => a.CompletedAt is null).MaxBy(a => a.AssignedAt);
        var people = await directory.GetPeopleAsync(
            stageRows.Where(x => x.Reviewer != null).Select(x => x.Reviewer!.Value)
                .Concat(actions.Select(a => a.ActorUserId))
                .Concat(open is null ? [] : [open.ReviewerUserId]), ct);
        PersonRef Person(Guid id) => people.GetValueOrDefault(id) ?? new PersonRef(id, "Unknown user", null);

        var comments = actions
            .Where(a => a.Comments is not null)
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => new ReviewerComment(Person(a.ActorUserId), a.ActionType, a.StageOrder, a.Comments!, a.OccurredAt))
            .ToList();

        return new SubmissionDetail(
            header.Id, header.Number, header.Title, header.Status, header.Type, header.Course, header.Term, header.Submitter, header.Unit.ToRef(),
            header.CurrentStage,
            stageRows.Select(x => new StageInfo(x.Order, x.Rule, x.State, x.Reviewer is { } r ? Person(r) : null, x.Reason, x.CompletedAt)).ToList(),
            open is null ? null : new AssignmentInfo(open.StageOrder, Person(open.ReviewerUserId), open.Reason, open.AssignedAt),
            pinnedSets,
            items,
            s.Documents.Select(d => d.DocumentVersionId).ToList(),
            comments,
            WorkflowRules.AllowedActions(relation, s.Status).Select(WorkflowRules.ActionName).ToList(),
            header.SubmittedAt, header.FinalizedAt, header.CreatedAt, header.UpdatedAt, header.Version);
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(Guid id, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: false, ct);
        var s = g.Submission;

        var actions = await db.ReviewActions.AsNoTracking()
            .Where(a => a.SubmissionId == s.Id)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync(ct);
        var checklist = await requirements.GetPinnedChecklistAsync(s.RequirementSets.Select(r => r.RequirementSetId).ToList(), ct);
        var labelsByKey = checklist.Requirements.DistinctBy(r => r.Key).ToDictionary(r => r.Key, r => r.Label);

        var attachedDocs = s.Documents.Select(d => d.DocumentId).Distinct().ToList();
        var actionVersionIds = actions.SelectMany(a => a.DocumentVersionIds).Distinct().ToList();
        var versions = await db.DocumentVersions.AsNoTracking()
            .Where(v => attachedDocs.Contains(v.DocumentId) || actionVersionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.DocumentId, v.VersionNumber, v.UploadedAt, v.UploadedByUserId })
            .ToListAsync(ct);
        var versionById = versions.ToDictionary(v => v.Id);
        var documentIds = versions.Select(v => v.DocumentId).Distinct().ToList();
        var titles = await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Title })
            .ToDictionaryAsync(d => d.Id, d => d.Title, ct);

        string LabelOf(Guid documentId) =>
            s.Documents.FirstOrDefault(d => d.DocumentId == documentId) is { } d
                ? labelsByKey.GetValueOrDefault(d.RequirementKey, d.RequirementKey)
                : titles.GetValueOrDefault(documentId, "Document");

        IReadOnlyList<VersionRef> Refs(IEnumerable<Guid> ids) => ids
            .Select(id => versionById.GetValueOrDefault(id))
            .Where(v => v is not null)
            .Select(v => new VersionRef(v!.DocumentId, v.Id, v.VersionNumber, LabelOf(v.DocumentId)))
            .ToList();

        var people = await directory.GetPeopleAsync(actions.Select(a => a.ActorUserId).Append(s.SubmittedByUserId), ct);
        PersonRef Person(Guid uid) => people.GetValueOrDefault(uid) ?? new PersonRef(uid, "Unknown user", null);

        var entries = new List<TimelineEntry>();
        foreach (var a in actions)
        {
            var refs = Refs(a.DocumentVersionIds);
            var actor = Person(a.ActorUserId);
            entries.Add(new TimelineEntry(a.OccurredAt, a.ActionType.ToString(), WorkflowText.Describe(a, actor.Name, refs),
                WorkflowText.Tone(a.ActionType), actor, a.StageOrder, a.Comments, refs));
        }

        // Uploads after the submission was started: "Syllabus v2 uploaded — not yet resubmitted".
        var lastRevision = actions.LastOrDefault(a => a.ActionType == ReviewActionType.RevisionRequested)?.OccurredAt;
        foreach (var v in versions.Where(v => attachedDocs.Contains(v.DocumentId) && v.UploadedAt >= s.CreatedAt))
        {
            var refs = new[] { new VersionRef(v.DocumentId, v.Id, v.VersionNumber, LabelOf(v.DocumentId)) };
            var pending = s.Status == SubmissionStatus.RevisionRequested && lastRevision is { } lr && v.UploadedAt > lr;
            entries.Add(new TimelineEntry(v.UploadedAt, "Uploaded",
                $"{WorkflowText.Versions(refs)} uploaded{(pending ? " — not yet resubmitted" : "")}",
                pending ? "pending" : "neutral", Person(v.UploadedByUserId), null, null, refs));
        }
        return entries.OrderBy(e => e.At).ThenBy(e => e.Kind == "Uploaded" ? 1 : 0).ToList();
    }

    private static RoutingDecision? TryResolve(Domain.Requirements.ReviewerRule rule, RoutingUnit routing)
    {
        try
        {
            return ReviewerRouting.Resolve(rule, routing);
        }
        catch (BusinessRuleException)
        {
            return null;
        }
    }

    internal static bool IsDecision(ReviewActionType t) =>
        t is ReviewActionType.Approved or ReviewActionType.RevisionRequested or ReviewActionType.Rejected;

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <summary>Human-readable timeline lines, matching the frontend's wording.</summary>
public static class WorkflowText
{
    public static string Versions(IEnumerable<VersionRef> refs) =>
        string.Join(" + ", refs.Select(r => $"{r.Label} v{r.VersionNumber}"));

    public static string Describe(ReviewAction a, string actor, IReadOnlyList<VersionRef> refs) => a.ActionType switch
    {
        ReviewActionType.Created => "Draft created",
        ReviewActionType.Submitted => refs.Count > 0 ? $"Submitted — {Versions(refs)} pinned" : "Submitted",
        ReviewActionType.Opened => $"Under review — opened by {actor}",
        ReviewActionType.Approved => a.NewStatus == SubmissionStatus.Approved
            ? $"Approved by {actor}"
            : $"Stage {a.StageOrder} approved by {actor}",
        ReviewActionType.RevisionRequested => $"Revision requested by {actor}",
        ReviewActionType.Rejected => $"Rejected by {actor}",
        ReviewActionType.Resubmitted => refs.Count > 0 ? $"Resubmitted — {Versions(refs)} pinned" : "Resubmitted",
        ReviewActionType.Withdrawn => a.NewStatus == SubmissionStatus.Withdrawn ? "Draft deleted" : "Withdrawn — back to draft",
        ReviewActionType.Published => refs.Count > 0 ? $"Published to the library — {Versions(refs)}" : "Published to the library",
        ReviewActionType.Reassigned => $"Review reassigned by {actor}",
        ReviewActionType.Commented => $"{actor} commented",
        _ => a.ActionType.ToString(),
    };

    public static string Tone(ReviewActionType t) => t switch
    {
        ReviewActionType.Submitted or ReviewActionType.Opened or ReviewActionType.Resubmitted or ReviewActionType.Reassigned => "info",
        ReviewActionType.Approved or ReviewActionType.Published => "success",
        ReviewActionType.RevisionRequested => "warning",
        ReviewActionType.Rejected => "danger",
        _ => "neutral",
    };

    /// <summary>Default library title of an attached file.</summary>
    public static string DocumentTitle(string submissionTitle, string slotLabel, bool onlySlot)
    {
        var title = onlySlot ? submissionTitle : $"{submissionTitle} · {slotLabel}";
        return title.Length <= 300 ? title : title[..300];
    }
}
