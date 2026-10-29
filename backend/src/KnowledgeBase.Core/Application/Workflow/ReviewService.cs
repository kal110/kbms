using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Workflow;

/// <summary>
/// The reviewer's side (frames 4, 9, 15): open, approve (next stage or publish), request a
/// revision, reject, reassign, and the "assigned to me" queue.
/// </summary>
public sealed class ReviewService(
    KbDbContext db,
    ICurrentUser currentUser,
    SubmissionGate gate,
    WorkflowDirectory directory,
    WorkflowEffects effects,
    SubmissionQueries queries,
    IRequirementResolver requirements)
{
    /// <summary>The assignee starts the review: Submitted/Resubmitted → UnderReview. No-op if already open.</summary>
    public async Task<SubmissionDetail> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct);
        var s = g.Submission;
        if (s.Status == SubmissionStatus.UnderReview && g.Relation.CanDecide) return await queries.BuildDetailAsync(g, ct);
        SubmissionGate.Require(g, SubmissionAction.Open);

        var from = s.Status;
        var to = WorkflowRules.Transition(SubmissionAction.Open, from);
        s.Status = to;
        effects.Record(s, g.Me.UserId, ReviewActionType.Opened, from, to, versions: s.Documents.Select(d => d.DocumentVersionId));
        effects.Publish("opened", s, g.Me.UserId);
        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    public Task<SubmissionDetail> ApproveAsync(Guid id, DecisionRequest r, uint? ifMatch, CancellationToken ct = default) =>
        DecideAsync(id, SubmissionAction.Approve, r, ifMatch, ct);

    public Task<SubmissionDetail> RequestRevisionAsync(Guid id, DecisionRequest r, uint? ifMatch, CancellationToken ct = default) =>
        DecideAsync(id, SubmissionAction.RequestRevision, r, ifMatch, ct);

    public Task<SubmissionDetail> RejectAsync(Guid id, DecisionRequest r, uint? ifMatch, CancellationToken ct = default) =>
        DecideAsync(id, SubmissionAction.Reject, r, ifMatch, ct);

    private async Task<SubmissionDetail> DecideAsync(Guid id, SubmissionAction action, DecisionRequest r, uint? ifMatch, CancellationToken ct)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, action);
        var s = g.Submission;
        var me = g.Me;

        var comments = SubmissionService.OptionalComments(r.Comments);
        if (comments is null && action == SubmissionAction.RequestRevision)
            throw new ValidationAppException("comments", "Explain what needs to change.");
        if (comments is null && action == SubmissionAction.Reject)
            throw new ValidationAppException("comments", "Explain why the submission is rejected.");

        // The decision must be on exactly what the submission points at now (frame 9).
        var current = s.Documents.Select(d => d.DocumentVersionId).ToList();
        if (r.ReviewedVersionIds is null)
            throw new ValidationAppException("reviewedVersionIds", "Send the versions you reviewed.");
        if (!WorkflowRules.SameVersions(r.ReviewedVersionIds, current))
            throw (ConflictException)new ConflictException("stale-version", "The files changed since you opened this submission.",
                    "Reload to review the current versions.")
                .With("currentVersionIds", current);

        var from = s.Status;
        effects.CompleteOpenAssignments(s);

        switch (action)
        {
            case SubmissionAction.Approve:
                await ApproveCoreAsync(g, from, comments, current, ct);
                break;

            case SubmissionAction.RequestRevision:
                s.Status = WorkflowRules.Transition(action, from);
                effects.Record(s, me.UserId, ReviewActionType.RevisionRequested, from, s.Status, comments, current);
                effects.Notify(s.SubmittedByUserId, "submission.revision-requested",
                    $"Revision requested on “{s.Title}”", $"{me.DisplayName}: {comments}", WorkflowEffects.SubmissionLink(s.Id));
                effects.Publish("revision-requested", s, me.UserId, new { versionIds = current });
                break;

            case SubmissionAction.Reject:
                s.Status = WorkflowRules.Transition(action, from);
                s.FinalizedAt = effects.Now;
                effects.Record(s, me.UserId, ReviewActionType.Rejected, from, s.Status, comments, current);
                effects.Notify(s.SubmittedByUserId, "submission.rejected",
                    $"“{s.Title}” was rejected", $"{me.DisplayName}: {comments}", WorkflowEffects.SubmissionLink(s.Id));
                effects.Publish("rejected", s, me.UserId, new { versionIds = current });
                break;
        }

        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    private async Task ApproveCoreAsync(GatedSubmission g, SubmissionStatus from, string? comments, List<Guid> current, CancellationToken ct)
    {
        var s = g.Submission;
        var me = g.Me;

        var approvedBy = (await db.ReviewActions.AsNoTracking()
                .Where(a => a.SubmissionId == s.Id && a.ActionType == ReviewActionType.Approved)
                .Select(a => a.ActorUserId)
                .ToListAsync(ct))
            .Append(me.UserId)
            .ToHashSet();
        var routing = await directory.GetRoutingUnitAsync(s.SubmittedByUserId, g.Unit, ct);
        var stages = await directory.GetStagesAsync(s.SubmissionTypeId, ct);
        var next = ReviewerRouting.NextStage(stages, s.CurrentStage, routing, approvedBy);
        var to = WorkflowRules.AfterApproval(from, next is not null);

        // Recorded at the stage that was approved, before moving on.
        var stageApproved = s.CurrentStage;
        s.Status = to;
        effects.Record(s, me.UserId, ReviewActionType.Approved, from, to, comments, current);

        if (next is not null)
        {
            effects.Assign(s, next.StageOrder, next.Decision);
            await effects.GrantReviewerAsync(s, next.Decision.ReviewerId, me.UserId, ct);
            effects.Notify(next.Decision.ReviewerId, "submission.assigned",
                $"“{s.Title}” is waiting for stage {next.StageOrder}", $"{me.DisplayName} approved stage {stageApproved}.",
                WorkflowEffects.ReviewLink(s.Id));
            effects.Notify(s.SubmittedByUserId, "submission.stage-approved",
                $"Stage {stageApproved} approved: “{s.Title}”", $"{me.DisplayName} approved it; it now goes to stage {next.StageOrder}.",
                WorkflowEffects.SubmissionLink(s.Id));
            effects.Publish("stage-approved", s, me.UserId, new { approvedStage = stageApproved, nextStage = next.StageOrder, versionIds = current });
            return;
        }

        s.FinalizedAt = effects.Now;
        await PublishAsync(g, current, ct);
        effects.Record(s, me.UserId, ReviewActionType.Published, to, to, versions: current);
        effects.Notify(s.SubmittedByUserId, "submission.approved",
            $"Approved and published: “{s.Title}”", $"{me.DisplayName} approved {s.SubmissionNumber}; it's now in the library.",
            WorkflowEffects.SubmissionLink(s.Id));
        effects.Publish("approved", s, me.UserId, new { versionIds = current });
    }

    /// <summary>
    /// Publication on final approval (frame 9): each file's reviewed version becomes the published
    /// one, a PublicationRecord keeps the history, and the unit gets View so it's shared material.
    /// </summary>
    private async Task PublishAsync(GatedSubmission g, List<Guid> versionIds, CancellationToken ct)
    {
        var s = g.Submission;
        var documentIds = s.Documents.Select(d => d.DocumentId).Distinct().ToList();
        var docs = await db.Documents.Where(d => documentIds.Contains(d.Id)).ToListAsync(ct);
        var (principalType, principalId) = g.Unit.Principal();

        foreach (var attachment in s.Documents)
        {
            var doc = docs.Single(d => d.Id == attachment.DocumentId);
            doc.PublishedVersionId = attachment.DocumentVersionId;
            db.PublicationRecords.Add(new PublicationRecord
            {
                DocumentId = doc.Id,
                DocumentVersionId = attachment.DocumentVersionId,
                SubmissionId = s.Id,
                Status = PublicationStatus.Published,
                PublishedAt = effects.Now,
                PublishedByUserId = g.Me.UserId,
                Reason = $"Approved in {s.SubmissionNumber}",
                CreatedAt = effects.Now,
            });
            await effects.GrantAsync(doc.Id, principalType, principalId, AccessLevel.View, g.Me.UserId, ct);
        }
        effects.Publish("published", s, g.Me.UserId, new
        {
            documents = s.Documents.Select(d => new { d.DocumentId, versionId = d.DocumentVersionId }).ToList(),
            principalKey = PrincipalKeys.For(principalType, principalId),
        });
    }

    /// <summary>An admin or the college Dean hands the current stage to someone else.</summary>
    public async Task<SubmissionDetail> ReassignAsync(Guid id, ReassignRequest r, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.Reassign);
        var s = g.Submission;

        if (r.ReviewerUserId == s.SubmittedByUserId)
            throw new ValidationAppException(nameof(r.ReviewerUserId), "The submitter can't review their own submission.");
        if (r.ReviewerUserId == s.AssignedReviewerId)
            throw new ValidationAppException(nameof(r.ReviewerUserId), "That person is already the reviewer.");
        if (!await db.Users.AnyAsync(u => u.Id == r.ReviewerUserId && u.IsActive, ct))
            throw new ValidationAppException(nameof(r.ReviewerUserId), "Pick an active user.");
        var reason = SubmissionService.OptionalComments(r.Reason);
        var previous = s.AssignedReviewerId;

        effects.CompleteOpenAssignments(s);
        effects.Assign(s, s.CurrentStage, new RoutingDecision(r.ReviewerUserId, AssignmentReason.Manual));
        effects.Record(s, g.Me.UserId, ReviewActionType.Reassigned, s.Status, s.Status, reason);
        await effects.GrantReviewerAsync(s, r.ReviewerUserId, g.Me.UserId, ct);
        effects.Notify(r.ReviewerUserId, "submission.assigned", $"“{s.Title}” was assigned to you",
            reason ?? $"{g.Me.DisplayName} reassigned stage {s.CurrentStage} of {s.SubmissionNumber}.", WorkflowEffects.ReviewLink(s.Id));
        if (previous is { } p)
            effects.Notify(p, "submission.reassigned", $"“{s.Title}” was reassigned",
                $"{g.Me.DisplayName} moved it to another reviewer.", WorkflowEffects.ReviewLink(s.Id));
        effects.Publish("reassigned", s, g.Me.UserId, new { previousReviewerId = previous, reason });

        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    /// <summary>Submissions waiting for the caller, longest-waiting first (frame 4).</summary>
    public async Task<PagedResult<ReviewQueueItem>> PendingAsync(PageRequest page, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var query = db.Submissions.AsNoTracking().Where(s => s.AssignedReviewerId == me.UserId
            && (s.Status == SubmissionStatus.Submitted || s.Status == SubmissionStatus.UnderReview || s.Status == SubmissionStatus.Resubmitted));

        var total = await query.CountAsync(ct);
        var ordered = await query
            .Select(s => new
            {
                s.Id,
                Since = s.Assignments.Where(a => a.CompletedAt == null).Max(a => (DateTimeOffset?)a.AssignedAt) ?? s.UpdatedAt,
            })
            .OrderBy(x => x.Since)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .ToListAsync(ct);
        var ids = ordered.Select(x => x.Id).ToList();

        var headers = await db.Submissions.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(SubmissionProjections.Header).ToListAsync(ct);
        var assignments = await db.SubmissionAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.SubmissionId) && a.CompletedAt == null)
            .ToListAsync(ct);
        var attachments = await db.SubmissionDocuments.AsNoTracking().Where(d => ids.Contains(d.SubmissionId)).ToListAsync(ct);
        var sets = await db.SubmissionRequirementSets.AsNoTracking().Where(x => ids.Contains(x.SubmissionId)).ToListAsync(ct);
        var actions = await db.ReviewActions.AsNoTracking()
            .Where(a => ids.Contains(a.SubmissionId) && a.ActionType != ReviewActionType.Created && a.ActionType != ReviewActionType.Opened)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync(ct);

        var documentIds = attachments.Select(a => a.DocumentId).Distinct().ToList();
        var typeCodes = await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentType.Code })
            .ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        var versionIds = attachments.Select(a => a.DocumentVersionId)
            .Concat(actions.Where(a => SubmissionQueries.IsDecision(a.ActionType)).SelectMany(a => a.DocumentVersionIds))
            .Distinct().ToList();
        var versions = await db.DocumentVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.DocumentId, v.VersionNumber, v.Sha256, v.PageCount })
            .ToDictionaryAsync(v => v.Id, ct);
        var people = await directory.GetPeopleAsync(actions.Select(a => a.ActorUserId), ct);
        // The units' AS or Head, to tell "the unit has no reviewer" from "the reviewer submitted it themselves".
        var unitIds = headers.Select(h => h.Unit.ProgramId ?? h.Unit.DepartmentId).Distinct().ToList();
        var unitReviewers = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.IsActive && r.ScopeId != null && unitIds.Contains(r.ScopeId.Value)
                && (r.Role == Role.AcademicSupervisor || r.Role == Role.DepartmentHead))
            .Select(r => new { UnitId = r.ScopeId!.Value, r.UserId })
            .ToListAsync(ct);

        var now = effects.Now;
        var items = new List<ReviewQueueItem>();
        foreach (var x in ordered)
        {
            var h = headers.Single(hh => hh.Id == x.Id);
            var open = assignments.Where(a => a.SubmissionId == h.Id).MaxBy(a => a.AssignedAt);
            var stage = open?.StageOrder ?? h.CurrentStage;
            var reason = open?.Reason ?? AssignmentReason.Rule;
            var own = actions.Where(a => a.SubmissionId == h.Id).ToList();
            var lastDecision = own.LastOrDefault(a => SubmissionQueries.IsDecision(a.ActionType));
            var previousStageApproval = own.LastOrDefault(a => a.ActionType == ReviewActionType.Approved && a.StageOrder < stage);

            var checklist = await requirements.GetPinnedChecklistAsync(
                sets.Where(z => z.SubmissionId == h.Id).Select(z => z.RequirementSetId).ToList(), ct);
            var labels = checklist.Requirements.DistinctBy(z => z.Key).ToDictionary(z => z.Key, z => z.Label);

            var files = attachments.Where(a => a.SubmissionId == h.Id).OrderBy(a => a.CreatedAt).Select(a =>
            {
                var v = versions[a.DocumentVersionId];
                var previous = lastDecision?.DocumentVersionIds
                    .Select(id => versions.GetValueOrDefault(id))
                    .FirstOrDefault(pv => pv?.DocumentId == a.DocumentId && pv.Id != a.DocumentVersionId);
                return new QueueAttachment(a.RequirementKey, labels.GetValueOrDefault(a.RequirementKey, a.RequirementKey),
                    typeCodes.GetValueOrDefault(a.DocumentId, ""), a.DocumentId, a.DocumentVersionId, v.VersionNumber,
                    previous?.VersionNumber, v.Sha256, v.PageCount);
            }).ToList();

            var waited = now - x.Since;
            var byUnitReviewer = unitReviewers.Any(r => r.UnitId == (h.Unit.ProgramId ?? h.Unit.DepartmentId) && r.UserId == h.Submitter.Id);
            string? note = reason switch
            {
                AssignmentReason.Manual => "Reassigned to you",
                AssignmentReason.Fallback when byUnitReviewer && h.Unit.IsNonAcademic => $"Submitted by the {h.Unit.DepartmentName} head · routed to you",
                AssignmentReason.Fallback when byUnitReviewer && h.Unit.ProgramId is not null =>
                    $"Submitted by the {h.Unit.ProgramCode} Academic Supervisor · routed to you",
                AssignmentReason.Fallback when h.Unit.IsNonAcademic => $"{h.Unit.DepartmentName} has no head · routed to you",
                AssignmentReason.Fallback when h.Unit.ProgramId is not null => $"{h.Unit.ProgramCode} has no Academic Supervisor · routed to you",
                AssignmentReason.Fallback => "No reviewer for this unit · routed to you",
                _ => previousStageApproval is { } pa
                    ? $"{people.GetValueOrDefault(pa.ActorUserId)?.Name ?? "A reviewer"} approved stage {pa.StageOrder}"
                    : null,
            };

            items.Add(new ReviewQueueItem(
                h.Id, h.Number, h.Title, h.Status, h.Type, h.Course, h.Term, h.Submitter, h.Unit.ToRef(),
                stage,
                stage == 1 ? (reason == AssignmentReason.Fallback ? "Stage 1 fallback" : "Stage 1") : $"Stage {stage}",
                reason,
                note,
                x.Since,
                Math.Round(waited.TotalHours, 1),
                (int)Math.Floor(waited.TotalDays),
                waited > WorkflowRules.OverdueAfter,
                own.FirstOrDefault(a => a.ActionType == ReviewActionType.Submitted)?.OccurredAt ?? h.SubmittedAt,
                own.LastOrDefault(a => a.ActionType == ReviewActionType.Resubmitted)?.OccurredAt,
                lastDecision?.OccurredAt,
                files));
        }
        return new PagedResult<ReviewQueueItem>(items, page.SafePage, page.SafePageSize, total);
    }
}
