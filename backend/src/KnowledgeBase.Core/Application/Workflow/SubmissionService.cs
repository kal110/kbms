using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Workflow;

/// <summary>
/// The submitter's side (frame 3): create a draft against the pinned checklist, attach files,
/// submit, resubmit after a revision request, withdraw back to draft, or delete a draft.
/// </summary>
public sealed class SubmissionService(
    KbDbContext db,
    ICurrentUser currentUser,
    SubmissionGate gate,
    WorkflowDirectory directory,
    WorkflowEffects effects,
    SubmissionNumbers numbers,
    SubmissionQueries queries,
    IRequirementResolver requirements,
    IAcademicFolderService folders,
    IDocumentFactory documents)
{
    public async Task<SubmissionDetail> CreateAsync(CreateSubmissionRequest r, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var title = RequireTitle(r.Title);

        var type = await db.SubmissionTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == r.SubmissionTypeId && t.IsActive, ct)
            ?? throw new ValidationAppException(nameof(r.SubmissionTypeId), "Pick an active submission type.");
        var term = r.AcademicTermId is { } termId
            ? await db.AcademicTerms.AsNoTracking().SingleOrDefaultAsync(t => t.Id == termId, ct)
                ?? throw new ValidationAppException(nameof(r.AcademicTermId), "That academic term doesn't exist.")
            : await db.AcademicTerms.AsNoTracking().SingleOrDefaultAsync(t => t.Status == TermStatus.Active, ct)
                ?? throw new ValidationAppException(nameof(r.AcademicTermId), "There is no active term; pick one.");
        if (term.Status == TermStatus.Closed)
            throw new BusinessRuleException("term-closed", "That term is closed.", $"{term.Name} no longer takes new submissions.");

        if (r.DepartmentId is not null && (r.ProgramId is not null || r.CourseId is not null))
            throw new ValidationAppException(nameof(r.DepartmentId), "A submission is for a program or a department, not both.");
        var course = await ResolveCourseAsync(me, type, term, r, ct);
        var (programId, departmentId) = course is not null ? (course.ProgramId, (Guid?)null) : ResolveUnit(me, r.ProgramId, r.DepartmentId);
        var checklist = await requirements.GetEffectiveChecklistAsync(me.UserId, type.Id, term.Id, programId, departmentId, ct);
        if (checklist.Requirements.Count == 0)
            throw new BusinessRuleException("no-requirements", "Nothing is required for this submission type yet.",
                "No published requirement set applies to you for this type and term.");

        var s = new Submission
        {
            SubmissionNumber = await numbers.NextAsync(ct),
            Title = title,
            SubmittedByUserId = me.UserId,
            ProgramId = programId,
            DepartmentId = departmentId,
            AcademicTermId = term.Id,
            AcademicYearId = term.AcademicYearId,
            SubmissionTypeId = type.Id,
            CourseId = course?.Id,
            Status = SubmissionStatus.Draft,
        };
        foreach (var setId in checklist.RequirementSetIds.Distinct())
            s.RequirementSets.Add(new SubmissionRequirementSet { SubmissionId = s.Id, RequirementSetId = setId });
        db.Submissions.Add(s);

        effects.Record(s, me.UserId, ReviewActionType.Created, null, SubmissionStatus.Draft);
        effects.Publish("created", s, me.UserId, new { submissionTypeId = type.Id, termId = term.Id, programId, departmentId, courseId = course?.Id });
        await numbers.SaveAsync(s, ct);

        return await queries.GetDetailAsync(s.Id, ct);
    }

    public async Task<SubmissionDetail> UpdateAsync(Guid id, UpdateSubmissionRequest r, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.Edit);
        var s = g.Submission;

        if (r.Title is not null) s.Title = RequireTitle(r.Title);
        s.UpdatedAt = effects.Now;
        effects.Publish("updated", s, g.Me.UserId);
        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    public async Task<SubmissionDetail> AddAttachmentAsync(Guid id, AddAttachmentRequest r, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.Attach);
        var s = g.Submission;

        if (!await db.UploadSessions.AnyAsync(u => u.Id == r.UploadSessionId && u.UserId == g.Me.UserId, ct))
            throw new NotFoundException("Upload", r.UploadSessionId);

        var checklist = await requirements.GetPinnedChecklistAsync(s.RequirementSets.Select(x => x.RequirementSetId).ToList(), ct);
        var slot = checklist.Requirements.FirstOrDefault(x => x.Key == r.RequirementKey)
            ?? throw new ValidationAppException(nameof(r.RequirementKey), "This checklist has no such slot.");
        var inSlot = s.Documents.Where(d => d.RequirementKey == slot.Key).ToList();

        // A full single-file slot takes the upload as the next version of the file already there.
        var reviseDocumentId = r.DocumentId ?? (slot.MaxFiles == 1 && inSlot.Count == 1 ? inSlot[0].DocumentId : null);
        DocumentVersion version;
        if (reviseDocumentId is { } documentId)
        {
            var attachment = inSlot.FirstOrDefault(d => d.DocumentId == documentId)
                ?? throw new ValidationAppException(nameof(r.DocumentId), "That document isn't attached to this slot.");
            version = await documents.AddVersionAsync(documentId, r.UploadSessionId, r.ChangeDescription, ct);
            attachment.DocumentVersionId = version.Id;
        }
        else
        {
            if (inSlot.Count >= slot.MaxFiles)
                throw (BusinessRuleException)new BusinessRuleException("slot-full", "This slot already has as many files as it allows.",
                        $"{slot.Label} takes at most {slot.MaxFiles} file(s).")
                    .With("maxFiles", slot.MaxFiles);

            var tag = await db.DocumentTypes.AsNoTracking()
                .Where(t => t.Id == slot.DocumentTypeId)
                .Select(t => t.AcademicTag)
                .SingleAsync(ct);
            var folderId = await folders.ResolveSubmissionFolderAsync(new SubmissionFolderRequest(
                s.SubmittedByUserId, s.ProgramId, s.DepartmentId, s.AcademicTermId, slot.DocumentTypeId, s.CourseId), ct);
            var (yearId, termId) = tag switch
            {
                AcademicTag.Term => ((Guid?)s.AcademicYearId, (Guid?)s.AcademicTermId),
                AcademicTag.Year => (s.AcademicYearId, null),
                _ => ((Guid?)null, (Guid?)null),
            };
            var title = string.IsNullOrWhiteSpace(r.Title)
                ? WorkflowText.DocumentTitle(s.Title, slot.Label, checklist.Requirements.Count == 1)
                : RequireTitle(r.Title);

            version = await documents.CreateDocumentAsync(r.UploadSessionId,
                new NewDocumentSpec(folderId, slot.DocumentTypeId, title, g.Me.UserId, yearId, termId), ct);
            s.Documents.Add(new SubmissionDocument
            {
                SubmissionId = s.Id,
                DocumentId = DocumentIdOf(version),
                DocumentVersionId = version.Id,
                RequirementKey = slot.Key,
                CreatedAt = effects.Now,
            });
        }

        s.UpdatedAt = effects.Now;
        effects.Publish("attachment-added", s, g.Me.UserId,
            new { requirementKey = slot.Key, documentId = DocumentIdOf(version), versionId = version.Id, versionNumber = version.VersionNumber });
        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    /// <summary>Takes a file out of the checklist; the document itself stays in the submitter's folder.</summary>
    public async Task<SubmissionDetail> RemoveAttachmentAsync(Guid id, Guid attachmentId, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.RemoveAttachment);
        var s = g.Submission;

        var attachment = s.Documents.FirstOrDefault(d => d.Id == attachmentId) ?? throw new NotFoundException("Attachment", attachmentId);
        s.Documents.Remove(attachment);
        db.SubmissionDocuments.Remove(attachment);
        s.UpdatedAt = effects.Now;
        effects.Publish("attachment-removed", s, g.Me.UserId, new { attachment.RequirementKey, attachment.DocumentId, attachment.DocumentVersionId });
        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    public async Task<SubmissionDetail> SubmitAsync(Guid id, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.Submit);
        var s = g.Submission;
        var from = s.Status;
        var to = WorkflowRules.Transition(SubmissionAction.Submit, from);

        WorkflowRules.EnsureWindowOpen(await TermAsync(s, ct), effects.Today, resubmit: false);
        await EnsureCompleteAsync(s, ct);

        var routing = await directory.GetRoutingUnitAsync(s.SubmittedByUserId, g.Unit, ct);
        var stages = await directory.GetStagesAsync(s.SubmissionTypeId, ct);
        var route = ReviewerRouting.NextStage(stages, 0, routing, new HashSet<Guid>())
            ?? throw new BusinessRuleException("no-reviewer", "Nobody can review this submission.", "None of the type's approval stages applies to your unit.");

        effects.CompleteOpenAssignments(s);
        effects.Assign(s, route.StageOrder, route.Decision);
        s.Status = to;
        s.SubmittedAt = effects.Now;
        s.FinalizedAt = null;
        var versions = s.Documents.Select(d => d.DocumentVersionId).ToList();
        effects.Record(s, g.Me.UserId, ReviewActionType.Submitted, from, to, versions: versions);
        await effects.GrantReviewerAsync(s, route.Decision.ReviewerId, g.Me.UserId, ct);
        effects.Notify(route.Decision.ReviewerId, "submission.assigned",
            $"{g.Me.DisplayName} submitted “{s.Title}”",
            route.Decision.Reason == AssignmentReason.Fallback ? $"{s.SubmissionNumber} · routed to you because the unit has no reviewer of its own." : s.SubmissionNumber,
            WorkflowEffects.ReviewLink(s.Id));
        effects.Publish("submitted", s, g.Me.UserId, new { versionIds = versions, reason = route.Decision.Reason.ToString() });

        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    /// <summary>Back to the stage that asked for the revision, with at least one corrected file.</summary>
    public async Task<SubmissionDetail> ResubmitAsync(Guid id, ResubmitRequest? r, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.Resubmit);
        var s = g.Submission;
        var from = s.Status;
        var to = WorkflowRules.Transition(SubmissionAction.Resubmit, from);
        var comments = OptionalComments(r?.Comments);

        WorkflowRules.EnsureWindowOpen(await TermAsync(s, ct), effects.Today, resubmit: true);
        await EnsureCompleteAsync(s, ct);

        var versions = s.Documents.Select(d => d.DocumentVersionId).ToList();
        var revision = await db.ReviewActions.AsNoTracking()
            .Where(a => a.SubmissionId == s.Id && a.ActionType == ReviewActionType.RevisionRequested)
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync(ct);
        if (revision is not null && WorkflowRules.SameVersions(revision.DocumentVersionIds, versions))
            throw new BusinessRuleException("nothing-changed", "Upload a corrected file before resubmitting.",
                "The files are the same versions the reviewer asked you to revise.");

        var stage = revision?.StageOrder ?? s.CurrentStage;
        var routing = await directory.GetRoutingUnitAsync(s.SubmittedByUserId, g.Unit, ct);
        var stages = await directory.GetStagesAsync(s.SubmissionTypeId, ct);
        var rule = stages.Where(x => x.Order == stage).Select(x => (Domain.Requirements.ReviewerRule?)x.Rule).FirstOrDefault();
        var decision = rule is { } rr ? ReviewerRouting.Resolve(rr, routing) : null;
        var route = decision is not null
            ? new StageRoute(stage, rule!.Value, decision)
            : ReviewerRouting.NextStage(stages, stage - 1, routing, new HashSet<Guid>())
                ?? throw new BusinessRuleException("no-reviewer", "Nobody can review this submission.");

        effects.CompleteOpenAssignments(s);
        effects.Assign(s, route.StageOrder, route.Decision);
        s.Status = to;
        effects.Record(s, g.Me.UserId, ReviewActionType.Resubmitted, from, to, comments, versions);
        await effects.GrantReviewerAsync(s, route.Decision.ReviewerId, g.Me.UserId, ct);
        effects.Notify(route.Decision.ReviewerId, "submission.resubmitted",
            $"{g.Me.DisplayName} resubmitted “{s.Title}”", comments ?? s.SubmissionNumber, WorkflowEffects.ReviewLink(s.Id));
        effects.Publish("resubmitted", s, g.Me.UserId, new { versionIds = versions });

        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    /// <summary>Takes a pending or revision-requested submission back to Draft (frame 3).</summary>
    public async Task<SubmissionDetail> WithdrawAsync(Guid id, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.Withdraw);
        var s = g.Submission;
        var from = s.Status;
        var to = WorkflowRules.Transition(SubmissionAction.Withdraw, from);
        var reviewer = s.AssignedReviewerId;

        effects.Record(s, g.Me.UserId, ReviewActionType.Withdrawn, from, to);
        effects.CompleteOpenAssignments(s);
        s.Status = to;
        s.AssignedReviewerId = null;
        s.CurrentStage = 1;
        if (reviewer is { } r && WorkflowRules.IsPending(from))
            effects.Notify(r, "submission.withdrawn", $"{g.Me.DisplayName} withdrew “{s.Title}”",
                "It went back to draft and left your review queue.", WorkflowEffects.ReviewLink(s.Id));
        effects.Publish("withdrawn", s, g.Me.UserId);

        await db.SaveChangesAsync(ct);
        return await queries.BuildDetailAsync(g, ct);
    }

    /// <summary>
    /// Deletes a draft. Review history is append-only, so the row stays as Withdrawn and drops out
    /// of every list; its uploads stay in the submitter's folder.
    /// </summary>
    public async Task DeleteDraftAsync(Guid id, uint? ifMatch, CancellationToken ct = default)
    {
        var g = await gate.LoadAsync(id, track: true, ct, ifMatch);
        SubmissionGate.Require(g, SubmissionAction.DeleteDraft);
        var s = g.Submission;
        var from = s.Status;
        var to = WorkflowRules.Transition(SubmissionAction.DeleteDraft, from);

        effects.Record(s, g.Me.UserId, ReviewActionType.Withdrawn, from, to);
        s.Status = to;
        s.FinalizedAt = effects.Now;
        effects.Publish("deleted", s, g.Me.UserId);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------ helpers

    private static readonly SubmissionStatus[] Dropped = [SubmissionStatus.Withdrawn, SubmissionStatus.Rejected];

    /// <summary>
    /// A per-course type needs a course from the caller's teaching load for the term, and takes one
    /// live submission per course (withdrawn and rejected ones don't count). Other types take no course.
    /// </summary>
    private async Task<Course?> ResolveCourseAsync(UserContext me, SubmissionType type, AcademicTerm term, CreateSubmissionRequest r, CancellationToken ct)
    {
        if (!type.PerCourse)
        {
            if (r.CourseId is not null)
                throw new ValidationAppException(nameof(r.CourseId), $"{type.Name} is submitted once per person, not per course.");
            return null;
        }
        if (r.CourseId is not { } courseId)
            throw new ValidationAppException(nameof(r.CourseId), $"Pick the course this {type.Name.ToLowerInvariant()} is for.");

        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == courseId, ct)
            ?? throw new ValidationAppException(nameof(r.CourseId), "That course doesn't exist.");
        if (!await db.TeachingAssignments.AnyAsync(a => a.UserId == me.UserId && a.AcademicTermId == term.Id && a.CourseId == courseId, ct))
            throw new ValidationAppException(nameof(r.CourseId), $"{course.Code} isn't in your teaching load for {term.Name}.");
        if (r.ProgramId is { } programId && programId != course.ProgramId)
            throw new ValidationAppException(nameof(r.ProgramId), $"{course.Code} belongs to another program.");

        var existing = await db.Submissions.AsNoTracking()
            .Where(s => s.SubmittedByUserId == me.UserId && s.AcademicTermId == term.Id && s.SubmissionTypeId == type.Id
                && s.CourseId == courseId && !Dropped.Contains(s.Status))
            .Select(s => new { s.Id, s.SubmissionNumber })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            throw new ConflictException("course-already-submitted", $"You already have a {type.Name.ToLowerInvariant()} for {course.Code} this term.",
                    $"Continue with {existing.SubmissionNumber} instead.")
                .With("submissionId", existing.Id).With("submissionNumber", existing.SubmissionNumber);
        return course;
    }

    private static (Guid? ProgramId, Guid? DepartmentId) ResolveUnit(UserContext me, Guid? requestedProgramId, Guid? requestedDepartmentId)
    {
        if (requestedDepartmentId is { } d)
        {
            if (me.StaffDepartmentIds.Contains(d) || me.IsHeadOf(d)) return (null, d);
            throw new ForbiddenException("You can only submit for a department you work in or head.");
        }
        if (requestedProgramId is { } p)
        {
            if (me.Programs.Any(x => x.ProgramId == p) || me.IsSupervisorOf(p)) return (p, null);
            throw new ForbiddenException("You can only submit for a program you teach in or supervise.");
        }

        var primary = me.Programs.OrderByDescending(x => x.IsPrimary).FirstOrDefault();
        if (primary is not null) return (primary.ProgramId, null);
        if (me.ScopesOf(Role.AcademicSupervisor).FirstOrDefault() is var supervised && supervised != Guid.Empty) return (supervised, null);
        if (me.StaffDepartmentIds.Count > 0) return (null, me.StaffDepartmentIds[0]);
        if (me.ScopesOf(Role.DepartmentHead).FirstOrDefault() is var headed && headed != Guid.Empty) return (null, headed);

        throw new BusinessRuleException("no-unit", "There's nowhere to file your submission.",
            "You aren't a member of a program or a department.");
    }

    private async Task<AcademicTerm> TermAsync(Submission s, CancellationToken ct) =>
        await db.AcademicTerms.AsNoTracking().SingleAsync(t => t.Id == s.AcademicTermId, ct);

    private async Task EnsureCompleteAsync(Submission s, CancellationToken ct)
    {
        var checklist = await requirements.GetPinnedChecklistAsync(s.RequirementSets.Select(x => x.RequirementSetId).ToList(), ct);
        var attached = s.Documents.GroupBy(d => d.RequirementKey).ToDictionary(x => x.Key, x => x.Count());
        WorkflowRules.EnsureChecklistComplete(checklist.Requirements, attached);
    }

    private static Guid DocumentIdOf(DocumentVersion v) => v.DocumentId != Guid.Empty ? v.DocumentId : v.Document.Id;

    private static string RequireTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationAppException("title", "Give the submission a title.");
        var trimmed = title.Trim();
        if (trimmed.Length > 300) throw new ValidationAppException("title", "Keep the title under 300 characters.");
        return trimmed;
    }

    internal static string? OptionalComments(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments)) return null;
        if (comments.Length > 4000) throw new ValidationAppException("comments", "Keep comments under 4,000 characters.");
        return comments.Trim();
    }
}
