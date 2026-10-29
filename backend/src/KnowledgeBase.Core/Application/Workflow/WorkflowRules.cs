using System.Text.Json;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;

namespace KnowledgeBase.Core.Application.Workflow;

// Pure rules of the submission workflow (frames 3, 4, 9, 16): who may do what in which status,
// where a status goes next, and who reviews each stage. No database access, so they're unit tested.

public enum SubmissionAction
{
    Edit,
    Attach,
    RemoveAttachment,
    Submit,
    DeleteDraft,
    Resubmit,
    Withdraw,
    Open,
    Approve,
    RequestRevision,
    Reject,
    Reassign,
}

/// <summary>Where a submission is filed: a program (→ department → college) or a department.</summary>
public sealed record UnitInfo(
    Guid? ProgramId,
    string? ProgramCode,
    string? ProgramName,
    Guid DepartmentId,
    string DepartmentCode,
    string DepartmentName,
    Guid? CollegeId,
    string? CollegeCode,
    bool IsNonAcademic);

/// <summary>How the caller relates to one submission. Drives visibility and allowed actions.</summary>
public sealed record SubmissionRelation(
    bool IsSubmitter,
    bool IsAssignee,
    bool IsCollegeDean,
    bool IsUnitReviewer,
    bool IsPastReviewer,
    bool IsAdmin,
    bool IsVpaf = false)
{
    /// <summary>The current stage's assignee, or the college Dean as fallback; never the submitter.</summary>
    public bool CanDecide => !IsSubmitter && (IsAssignee || IsCollegeDean);

    public static SubmissionRelation For(
        UserContext me,
        Guid submitterId,
        Guid? assignedReviewerId,
        SubmissionStatus status,
        IEnumerable<Guid> pastReviewerIds,
        UnitInfo unit) => new(
            IsSubmitter: submitterId == me.UserId,
            IsAssignee: assignedReviewerId == me.UserId && WorkflowRules.IsPending(status),
            IsCollegeDean: unit.CollegeId is { } c && me.IsDeanOf(c),
            IsUnitReviewer: unit.ProgramId is { } p ? me.IsSupervisorOf(p) : unit.IsNonAcademic && me.IsHeadOf(unit.DepartmentId),
            IsPastReviewer: pastReviewerIds.Contains(me.UserId),
            IsAdmin: me.IsAdmin,
            // The VPAF reads every unit's submissions from the dashboard (programs and non-academic departments).
            IsVpaf: me.IsVpaf);
}

public static class WorkflowRules
{
    /// <summary>A stage-1 item waiting longer than this is overdue (college overview, review queue).</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromDays(5);

    public static bool IsPending(SubmissionStatus s) =>
        s is SubmissionStatus.Submitted or SubmissionStatus.UnderReview or SubmissionStatus.Resubmitted;

    public static bool IsEditable(SubmissionStatus s) => s is SubmissionStatus.Draft or SubmissionStatus.RevisionRequested;

    public static bool IsFinal(SubmissionStatus s) =>
        s is SubmissionStatus.Approved or SubmissionStatus.Rejected or SubmissionStatus.Withdrawn;

    private static readonly SubmissionStatus[] Pending =
        [SubmissionStatus.Submitted, SubmissionStatus.UnderReview, SubmissionStatus.Resubmitted];

    /// <summary>
    /// The transition table. Approve goes to Approved at the last stage; at an earlier stage the
    /// service sends it to the next stage as Submitted instead (see <see cref="AfterApproval"/>).
    /// Withdraw takes a submission back to Draft (frame 3); deleting a draft marks it Withdrawn.
    /// </summary>
    public static readonly IReadOnlyDictionary<SubmissionAction, (SubmissionStatus[] From, SubmissionStatus To)> Transitions =
        new Dictionary<SubmissionAction, (SubmissionStatus[] From, SubmissionStatus To)>
        {
            [SubmissionAction.Submit] = ([SubmissionStatus.Draft], SubmissionStatus.Submitted),
            [SubmissionAction.Open] = ([SubmissionStatus.Submitted, SubmissionStatus.Resubmitted], SubmissionStatus.UnderReview),
            [SubmissionAction.Approve] = (Pending, SubmissionStatus.Approved),
            [SubmissionAction.RequestRevision] = (Pending, SubmissionStatus.RevisionRequested),
            [SubmissionAction.Reject] = (Pending, SubmissionStatus.Rejected),
            [SubmissionAction.Resubmit] = ([SubmissionStatus.RevisionRequested], SubmissionStatus.Resubmitted),
            [SubmissionAction.Withdraw] = ([.. Pending, SubmissionStatus.RevisionRequested], SubmissionStatus.Draft),
            [SubmissionAction.DeleteDraft] = ([SubmissionStatus.Draft], SubmissionStatus.Withdrawn),
        };

    /// <summary>The status after <paramref name="action"/>, or 422 invalid-transition.</summary>
    public static SubmissionStatus Transition(SubmissionAction action, SubmissionStatus from)
    {
        if (Transitions.TryGetValue(action, out var t) && t.From.Contains(from)) return t.To;
        throw InvalidTransition(action, from);
    }

    /// <summary>Approval at a stage with a later stage left keeps the item open for the next reviewer.</summary>
    public static SubmissionStatus AfterApproval(SubmissionStatus from, bool hasNextStage)
    {
        var final = Transition(SubmissionAction.Approve, from);
        return hasNextStage ? SubmissionStatus.Submitted : final;
    }

    /// <summary>Whether the status permits the action for someone (tells 403 "not you" from 422 "not now").</summary>
    public static bool StatusAllows(SubmissionAction action, SubmissionStatus status) => action switch
    {
        SubmissionAction.Edit or SubmissionAction.Attach or SubmissionAction.RemoveAttachment => IsEditable(status),
        SubmissionAction.Reassign => IsPending(status),
        _ => Transitions.TryGetValue(action, out var t) && t.From.Contains(status),
    };

    public static BusinessRuleException InvalidTransition(SubmissionAction action, SubmissionStatus from) =>
        (BusinessRuleException)new BusinessRuleException("invalid-transition",
                "This action isn't possible in the submission's current status.",
                $"{ActionName(action)} is not allowed while the submission is {from}.")
            .With("status", from.ToString())
            .With("action", ActionName(action));

    /// <summary>
    /// Drafts are private to the submitter; everything else is visible to its reviewers and overseers. The VPAF reads
    /// every submission but gets no actions.
    /// </summary>
    public static bool CanView(SubmissionRelation r, SubmissionStatus status) =>
        r.IsSubmitter
        || (status is not (SubmissionStatus.Draft or SubmissionStatus.Withdrawn)
            && (r.IsAssignee || r.IsPastReviewer || r.IsCollegeDean || r.IsUnitReviewer || r.IsAdmin || r.IsVpaf));

    public static IReadOnlyList<SubmissionAction> AllowedActions(SubmissionRelation r, SubmissionStatus status)
    {
        var actions = new List<SubmissionAction>();
        if (r.IsSubmitter)
        {
            if (IsEditable(status)) actions.AddRange([SubmissionAction.Edit, SubmissionAction.Attach, SubmissionAction.RemoveAttachment]);
            if (status == SubmissionStatus.Draft) actions.AddRange([SubmissionAction.Submit, SubmissionAction.DeleteDraft]);
            if (status == SubmissionStatus.RevisionRequested) actions.Add(SubmissionAction.Resubmit);
            if (status == SubmissionStatus.RevisionRequested || IsPending(status)) actions.Add(SubmissionAction.Withdraw);
        }
        if (IsPending(status) && r.CanDecide)
        {
            if (r.IsAssignee && status is SubmissionStatus.Submitted or SubmissionStatus.Resubmitted) actions.Add(SubmissionAction.Open);
            actions.AddRange([SubmissionAction.Approve, SubmissionAction.RequestRevision, SubmissionAction.Reject]);
        }
        if (IsPending(status) && !r.IsSubmitter && (r.IsAdmin || r.IsCollegeDean)) actions.Add(SubmissionAction.Reassign);
        return actions;
    }

    /// <summary>camelCase name used in <c>allowedActions</c> and error extensions, e.g. "requestRevision".</summary>
    public static string ActionName(SubmissionAction action) => JsonNamingPolicy.CamelCase.ConvertName(action.ToString());

    /// <summary>
    /// Whether the term takes (re)submissions today. A Planned term opens on its start date unless
    /// an earlier window is set; Closed terms take resubmissions only when the term allows it.
    /// </summary>
    public static void EnsureWindowOpen(AcademicTerm term, DateOnly today, bool resubmit)
    {
        var opensOn = term.SubmissionOpensOn ?? (term.Status == TermStatus.Active ? (DateOnly?)null : term.StartsOn);
        var closesOn = term.SubmissionClosesOn ?? term.EndsOn;
        var notYetOpen = opensOn is { } o && today < o;
        var closed = term.Status == TermStatus.Closed || today > closesOn;

        if (notYetOpen)
            throw (BusinessRuleException)new BusinessRuleException("submission-window-not-open",
                    "Submissions for this term aren't open yet.", $"{term.Name} opens for submissions on {opensOn:yyyy-MM-dd}.")
                .With("opensOn", opensOn);
        if (closed && !(resubmit && term.AllowResubmitAfterClose))
            throw (BusinessRuleException)new BusinessRuleException("submission-window-closed",
                    "Submissions for this term are closed.", $"{term.Name} closed for submissions on {closesOn:yyyy-MM-dd}.")
                .With("closesOn", closesOn);
    }

    /// <summary>A slot of the pinned checklist that doesn't have enough files yet.</summary>
    public sealed record MissingRequirement(string Key, string Label, int MinFiles, int Attached);

    public static IReadOnlyList<MissingRequirement> MissingRequirements(
        IEnumerable<EffectiveRequirement> requirements, IReadOnlyDictionary<string, int> attachedByKey) =>
        requirements
            .Where(r => r.IsRequired)
            .Select(r => new MissingRequirement(r.Key, r.Label, Math.Max(1, r.MinFiles), attachedByKey.GetValueOrDefault(r.Key)))
            .Where(m => m.Attached < m.MinFiles)
            .ToList();

    public static void EnsureChecklistComplete(IEnumerable<EffectiveRequirement> requirements, IReadOnlyDictionary<string, int> attachedByKey)
    {
        var missing = MissingRequirements(requirements, attachedByKey);
        if (missing.Count == 0) return;
        throw (BusinessRuleException)new BusinessRuleException("checklist-incomplete",
                "Some required documents are missing.",
                string.Join("; ", missing.Select(m => $"{m.Label}: {m.Attached} of {m.MinFiles}")))
            .With("missing", missing);
    }

    /// <summary>The reviewer decided on exactly the versions the submission points at now.</summary>
    public static bool SameVersions(IEnumerable<Guid> a, IEnumerable<Guid> b) => a.ToHashSet().SetEquals(b);
}

/// <summary>The people who can review a submission's unit, as the router sees them.</summary>
public sealed record RoutingUnit(
    Guid SubmitterId,
    Guid? ProgramId,
    Guid? SupervisorId,
    Guid? CollegeId,
    Guid? DeanId,
    bool IsNonAcademic,
    Guid? HeadId,
    Guid? AdminId,
    Guid? VpafId = null);

public sealed record RoutingDecision(Guid ReviewerId, AssignmentReason Reason);

public sealed record StageRoute(int StageOrder, ReviewerRule Rule, RoutingDecision Decision);

public static class ReviewerRouting
{
    /// <summary>Types without configured stages get one stage reviewed by the unit head.</summary>
    public static readonly IReadOnlyList<(int Order, ReviewerRule Rule)> DefaultStages = [(1, ReviewerRule.UnitHead)];

    /// <summary>
    /// Who reviews a stage (frames 16, 24). UnitHead: the program's AS, else the college Dean
    /// (fallback); an academic department submission goes to the Dean; a non-academic department to
    /// its Head/Director, else the VPAF (fallback: no head, or the head's own submission).
    /// CollegeDean: the Dean of the submitter's college; it doesn't apply to
    /// non-academic departments (returns null, the stage is skipped). Nobody reviews their own
    /// submission, and a System Administrator is the last resort.
    /// </summary>
    public static RoutingDecision? Resolve(ReviewerRule rule, RoutingUnit unit)
    {
        Guid? Usable(Guid? id) => id is { } v && v != unit.SubmitterId ? v : null;
        RoutingDecision LastResort() => Usable(unit.AdminId) is { } admin
            ? new RoutingDecision(admin, AssignmentReason.Fallback)
            : throw new BusinessRuleException("no-reviewer", "Nobody can review this submission.",
                "The unit has no reviewer and there is no system administrator to fall back to.");

        if (rule == ReviewerRule.UnitHead)
        {
            if (unit.IsNonAcademic)
            {
                if (Usable(unit.HeadId) is { } head) return new RoutingDecision(head, AssignmentReason.Rule);
                return Usable(unit.VpafId) is { } vpaf ? new RoutingDecision(vpaf, AssignmentReason.Fallback) : LastResort();
            }
            if (unit.ProgramId is not null && Usable(unit.SupervisorId) is { } supervisor)
                return new RoutingDecision(supervisor, AssignmentReason.Rule);
            if (Usable(unit.DeanId) is { } dean)
                return new RoutingDecision(dean, unit.ProgramId is null ? AssignmentReason.Rule : AssignmentReason.Fallback);
            return LastResort();
        }

        if (unit.IsNonAcademic) return null;
        return Usable(unit.DeanId) is { } collegeDean ? new RoutingDecision(collegeDean, AssignmentReason.Rule) : LastResort();
    }

    /// <summary>
    /// The first stage after <paramref name="afterStage"/> that applies and whose reviewer hasn't
    /// already approved this submission (so a Dean covering stage 1 isn't asked again at stage 2).
    /// Null when no stage is left, i.e. the approval is final.
    /// </summary>
    public static StageRoute? NextStage(
        IEnumerable<(int Order, ReviewerRule Rule)> stages, int afterStage, RoutingUnit unit, IReadOnlySet<Guid> approvedBy)
    {
        foreach (var (order, rule) in stages.Where(s => s.Order > afterStage).OrderBy(s => s.Order))
        {
            var decision = Resolve(rule, unit);
            if (decision is null || approvedBy.Contains(decision.ReviewerId)) continue;
            return new StageRoute(order, rule, decision);
        }
        return null;
    }
}
