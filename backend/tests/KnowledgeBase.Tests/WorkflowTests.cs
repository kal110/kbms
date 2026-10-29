using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Application.Workflow;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;

namespace KnowledgeBase.Tests;

public class WorkflowTransitionTests
{
    [Theory]
    [InlineData(SubmissionAction.Submit, SubmissionStatus.Draft, SubmissionStatus.Submitted)]
    [InlineData(SubmissionAction.Open, SubmissionStatus.Submitted, SubmissionStatus.UnderReview)]
    [InlineData(SubmissionAction.Open, SubmissionStatus.Resubmitted, SubmissionStatus.UnderReview)]
    [InlineData(SubmissionAction.Approve, SubmissionStatus.UnderReview, SubmissionStatus.Approved)]
    [InlineData(SubmissionAction.RequestRevision, SubmissionStatus.Submitted, SubmissionStatus.RevisionRequested)]
    [InlineData(SubmissionAction.Reject, SubmissionStatus.Resubmitted, SubmissionStatus.Rejected)]
    [InlineData(SubmissionAction.Resubmit, SubmissionStatus.RevisionRequested, SubmissionStatus.Resubmitted)]
    [InlineData(SubmissionAction.Withdraw, SubmissionStatus.UnderReview, SubmissionStatus.Draft)]
    [InlineData(SubmissionAction.Withdraw, SubmissionStatus.RevisionRequested, SubmissionStatus.Draft)]
    [InlineData(SubmissionAction.DeleteDraft, SubmissionStatus.Draft, SubmissionStatus.Withdrawn)]
    public void Valid_transitions(SubmissionAction action, SubmissionStatus from, SubmissionStatus to) =>
        Assert.Equal(to, WorkflowRules.Transition(action, from));

    [Theory]
    [InlineData(SubmissionAction.Approve, SubmissionStatus.Draft)]
    [InlineData(SubmissionAction.Submit, SubmissionStatus.Submitted)]
    [InlineData(SubmissionAction.Resubmit, SubmissionStatus.UnderReview)]
    [InlineData(SubmissionAction.Withdraw, SubmissionStatus.Approved)]
    [InlineData(SubmissionAction.Reject, SubmissionStatus.Rejected)]
    public void Invalid_transitions_are_422(SubmissionAction action, SubmissionStatus from)
    {
        var e = Assert.Throws<BusinessRuleException>(() => WorkflowRules.Transition(action, from));
        Assert.Equal("invalid-transition", e.Code);
        Assert.Equal(422, e.Status);
    }

    [Fact]
    public void Approval_with_a_later_stage_waits_for_the_next_reviewer()
    {
        Assert.Equal(SubmissionStatus.Submitted, WorkflowRules.AfterApproval(SubmissionStatus.UnderReview, hasNextStage: true));
        Assert.Equal(SubmissionStatus.Approved, WorkflowRules.AfterApproval(SubmissionStatus.UnderReview, hasNextStage: false));
    }
}

public class WorkflowPermissionTests
{
    private static readonly Guid Program = Guid.NewGuid();
    private static readonly Guid Department = Guid.NewGuid();
    private static readonly Guid College = Guid.NewGuid();
    private static readonly UnitInfo Bscs = new(Program, "BSCS", "BS Computer Science", Department, "DCS", "Dept. of CS", College, "CCS", false);

    private static UserContext User(params RoleGrant[] roles) => new()
    {
        UserId = Guid.NewGuid(),
        DisplayName = "Test",
        Roles = roles,
        Programs = [],
        StaffDepartmentIds = [],
        GroupIds = [],
        PrincipalKeys = new HashSet<string>(),
    };

    private static List<string> Allowed(SubmissionRelation r, SubmissionStatus s) =>
        WorkflowRules.AllowedActions(r, s).Select(WorkflowRules.ActionName).ToList();

    [Fact]
    public void Submitter_works_on_drafts_and_can_withdraw_pending()
    {
        var me = User();
        var r = SubmissionRelation.For(me, me.UserId, null, SubmissionStatus.Draft, [], Bscs);

        Assert.Equal(["edit", "attach", "removeAttachment", "submit", "deleteDraft"], Allowed(r, SubmissionStatus.Draft));
        Assert.Equal(["edit", "attach", "removeAttachment", "resubmit", "withdraw"], Allowed(r, SubmissionStatus.RevisionRequested));
        Assert.Equal(["withdraw"], Allowed(r, SubmissionStatus.UnderReview));
        Assert.Empty(Allowed(r, SubmissionStatus.Approved));
    }

    [Fact]
    public void Assignee_opens_and_decides()
    {
        var me = User(new RoleGrant(Role.AcademicSupervisor, RoleScope.Program, Program, null));
        var r = SubmissionRelation.For(me, Guid.NewGuid(), me.UserId, SubmissionStatus.Submitted, [me.UserId], Bscs);

        Assert.Equal(["open", "approve", "requestRevision", "reject"], Allowed(r, SubmissionStatus.Submitted));
        Assert.Equal(["approve", "requestRevision", "reject"], Allowed(r, SubmissionStatus.UnderReview));
    }

    [Fact]
    public void Dean_can_decide_as_fallback_and_reassign_but_not_on_their_own_submission()
    {
        var dean = User(new RoleGrant(Role.Dean, RoleScope.College, College, null));
        var other = SubmissionRelation.For(dean, Guid.NewGuid(), Guid.NewGuid(), SubmissionStatus.UnderReview, [], Bscs);
        var own = SubmissionRelation.For(dean, dean.UserId, Guid.NewGuid(), SubmissionStatus.UnderReview, [], Bscs);

        Assert.Equal(["approve", "requestRevision", "reject", "reassign"], Allowed(other, SubmissionStatus.UnderReview));
        Assert.Equal(["withdraw"], Allowed(own, SubmissionStatus.UnderReview));
    }

    [Fact]
    public void Drafts_are_private_to_the_submitter()
    {
        var dean = User(new RoleGrant(Role.Dean, RoleScope.College, College, null));
        var r = SubmissionRelation.For(dean, Guid.NewGuid(), null, SubmissionStatus.Draft, [], Bscs);

        Assert.False(WorkflowRules.CanView(r, SubmissionStatus.Draft));
        Assert.True(WorkflowRules.CanView(r, SubmissionStatus.Submitted));
    }

    [Fact]
    public void Unrelated_people_see_nothing()
    {
        var stranger = User();
        var r = SubmissionRelation.For(stranger, Guid.NewGuid(), Guid.NewGuid(), SubmissionStatus.Approved, [], Bscs);
        Assert.False(WorkflowRules.CanView(r, SubmissionStatus.Approved));
    }

    [Fact]
    public void Vpaf_reads_every_units_submissions_but_takes_no_action()
    {
        var vpaf = User(new RoleGrant(Role.Vpaf, RoleScope.Global, null, "VP for Academic Affairs"));
        var r = SubmissionRelation.For(vpaf, Guid.NewGuid(), Guid.NewGuid(), SubmissionStatus.UnderReview, [], Bscs);
        Assert.True(WorkflowRules.CanView(r, SubmissionStatus.UnderReview));
        Assert.Empty(WorkflowRules.AllowedActions(r, SubmissionStatus.UnderReview));
        Assert.False(WorkflowRules.CanView(r, SubmissionStatus.Draft));

        var office = Bscs with { ProgramId = null, IsNonAcademic = true };
        var cict = SubmissionRelation.For(vpaf, Guid.NewGuid(), Guid.NewGuid(), SubmissionStatus.UnderReview, [], office);
        Assert.True(WorkflowRules.CanView(cict, SubmissionStatus.UnderReview));
        Assert.Empty(WorkflowRules.AllowedActions(cict, SubmissionStatus.UnderReview));
    }

    [Fact]
    public void Vpaf_decides_an_office_submission_routed_to_them()
    {
        var vpaf = User(new RoleGrant(Role.Vpaf, RoleScope.Global, null, "VP for Academic Affairs"));
        var office = Bscs with { ProgramId = null, IsNonAcademic = true };
        var r = SubmissionRelation.For(vpaf, Guid.NewGuid(), vpaf.UserId, SubmissionStatus.UnderReview, [vpaf.UserId], office);
        Assert.Equal(["approve", "requestRevision", "reject"], Allowed(r, SubmissionStatus.UnderReview));
    }

    [Theory]
    [InlineData(SubmissionAction.Approve, SubmissionStatus.UnderReview, true)]
    [InlineData(SubmissionAction.Approve, SubmissionStatus.Draft, false)]
    [InlineData(SubmissionAction.Attach, SubmissionStatus.RevisionRequested, true)]
    [InlineData(SubmissionAction.Attach, SubmissionStatus.Submitted, false)]
    [InlineData(SubmissionAction.Reassign, SubmissionStatus.Resubmitted, true)]
    public void Status_allows_tells_403_from_422(SubmissionAction action, SubmissionStatus status, bool allowed) =>
        Assert.Equal(allowed, WorkflowRules.StatusAllows(action, status));
}

public class ReviewerRoutingTests
{
    private static readonly Guid Submitter = Guid.NewGuid();
    private static readonly Guid Supervisor = Guid.NewGuid();
    private static readonly Guid Dean = Guid.NewGuid();
    private static readonly Guid Head = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static RoutingUnit ProgramUnit(Guid? supervisor = null, Guid? dean = null, Guid? submitter = null) =>
        new(submitter ?? Submitter, Guid.NewGuid(), supervisor, Guid.NewGuid(), dean, false, null, Admin);

    private static readonly Guid Vpaf = Guid.NewGuid();

    private static RoutingUnit Office(Guid? head, Guid? submitter = null, Guid? vpaf = null) =>
        new(submitter ?? Submitter, null, null, null, null, true, head, Admin, vpaf);

    [Fact]
    public void Program_goes_to_its_supervisor() =>
        Assert.Equal(new RoutingDecision(Supervisor, AssignmentReason.Rule), ReviewerRouting.Resolve(ReviewerRule.UnitHead, ProgramUnit(Supervisor, Dean)));

    [Fact]
    public void Program_without_supervisor_falls_back_to_the_dean() =>
        Assert.Equal(new RoutingDecision(Dean, AssignmentReason.Fallback), ReviewerRouting.Resolve(ReviewerRule.UnitHead, ProgramUnit(null, Dean)));

    [Fact]
    public void A_supervisor_submitting_in_their_own_program_goes_to_the_dean() =>
        Assert.Equal(new RoutingDecision(Dean, AssignmentReason.Fallback),
            ReviewerRouting.Resolve(ReviewerRule.UnitHead, ProgramUnit(Supervisor, Dean, submitter: Supervisor)));

    [Fact]
    public void Office_goes_to_its_head_and_the_head_to_the_vpaf()
    {
        Assert.Equal(new RoutingDecision(Head, AssignmentReason.Rule), ReviewerRouting.Resolve(ReviewerRule.UnitHead, Office(Head, vpaf: Vpaf)));
        Assert.Equal(new RoutingDecision(Vpaf, AssignmentReason.Fallback),
            ReviewerRouting.Resolve(ReviewerRule.UnitHead, Office(Head, submitter: Head, vpaf: Vpaf)));
    }

    [Fact]
    public void Office_without_a_head_goes_to_the_vpaf() =>
        Assert.Equal(new RoutingDecision(Vpaf, AssignmentReason.Fallback), ReviewerRouting.Resolve(ReviewerRule.UnitHead, Office(null, vpaf: Vpaf)));

    [Fact]
    public void Office_with_no_head_or_vpaf_goes_to_an_admin() =>
        Assert.Equal(new RoutingDecision(Admin, AssignmentReason.Fallback),
            ReviewerRouting.Resolve(ReviewerRule.UnitHead, Office(Head, submitter: Head)));

    [Fact]
    public void Dean_stage_does_not_apply_to_offices() =>
        Assert.Null(ReviewerRouting.Resolve(ReviewerRule.CollegeDean, Office(Head)));

    [Fact]
    public void Nobody_to_review_is_422()
    {
        var unit = new RoutingUnit(Submitter, Guid.NewGuid(), null, Guid.NewGuid(), null, false, null, null);
        var e = Assert.Throws<BusinessRuleException>(() => ReviewerRouting.Resolve(ReviewerRule.UnitHead, unit));
        Assert.Equal("no-reviewer", e.Code);
    }

    [Fact]
    public void Two_stage_type_goes_supervisor_then_dean()
    {
        (int, ReviewerRule)[] stages = [(1, ReviewerRule.UnitHead), (2, ReviewerRule.CollegeDean)];
        var unit = ProgramUnit(Supervisor, Dean);

        var first = ReviewerRouting.NextStage(stages, 0, unit, new HashSet<Guid>());
        var second = ReviewerRouting.NextStage(stages, 1, unit, new HashSet<Guid> { Supervisor });
        var done = ReviewerRouting.NextStage(stages, 2, unit, new HashSet<Guid> { Supervisor, Dean });

        Assert.Equal((1, Supervisor), (first!.StageOrder, first.Decision.ReviewerId));
        Assert.Equal((2, Dean), (second!.StageOrder, second.Decision.ReviewerId));
        Assert.Null(done);
    }

    [Fact]
    public void A_dean_covering_stage_one_is_not_asked_again_at_stage_two()
    {
        (int, ReviewerRule)[] stages = [(1, ReviewerRule.UnitHead), (2, ReviewerRule.CollegeDean)];
        var unit = ProgramUnit(null, Dean);

        Assert.Equal(Dean, ReviewerRouting.NextStage(stages, 0, unit, new HashSet<Guid>())!.Decision.ReviewerId);
        Assert.Null(ReviewerRouting.NextStage(stages, 1, unit, new HashSet<Guid> { Dean }));
    }
}

public class SubmissionWindowAndChecklistTests
{
    private static AcademicTerm Term(TermStatus status, DateOnly? opens = null, DateOnly? closes = null, bool resubmitAfterClose = false) => new()
    {
        Code = "2026-1",
        Name = "1st Semester",
        ShortName = "1st Sem",
        StartsOn = new DateOnly(2026, 8, 10),
        EndsOn = new DateOnly(2026, 12, 18),
        Status = status,
        SubmissionOpensOn = opens,
        SubmissionClosesOn = closes,
        AllowResubmitAfterClose = resubmitAfterClose,
    };

    private static readonly DateOnly Today = new(2026, 10, 26);

    [Fact]
    public void Active_term_takes_submissions() => WorkflowRules.EnsureWindowOpen(Term(TermStatus.Active), Today, resubmit: false);

    [Fact]
    public void Closed_window_refuses_submit_but_may_take_resubmissions()
    {
        var term = Term(TermStatus.Active, closes: new DateOnly(2026, 10, 1), resubmitAfterClose: true);
        Assert.Equal("submission-window-closed",
            Assert.Throws<BusinessRuleException>(() => WorkflowRules.EnsureWindowOpen(term, Today, resubmit: false)).Code);
        WorkflowRules.EnsureWindowOpen(term, Today, resubmit: true);
    }

    [Fact]
    public void Planned_term_opens_on_its_start_date()
    {
        var planned = Term(TermStatus.Planned);
        Assert.Equal("submission-window-not-open",
            Assert.Throws<BusinessRuleException>(() => WorkflowRules.EnsureWindowOpen(planned, new DateOnly(2026, 8, 1), false)).Code);
        WorkflowRules.EnsureWindowOpen(planned, new DateOnly(2026, 8, 10), false);
    }

    private static EffectiveRequirement Slot(string key, bool required, int min = 1, int max = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), RequirementScope.Program, Guid.NewGuid(), key, key, Guid.NewGuid(), required, min, max, null);

    [Fact]
    public void Required_slots_need_their_minimum_and_optional_ones_nothing()
    {
        EffectiveRequirement[] checklist = [Slot("syllabus", true), Slot("rubric", true, 2, 3), Slot("reading_list", false, 0, 2)];
        var missing = WorkflowRules.MissingRequirements(checklist, new Dictionary<string, int> { ["rubric"] = 1 });

        Assert.Equal(["syllabus", "rubric"], missing.Select(m => m.Key));
        var e = Assert.Throws<BusinessRuleException>(() =>
            WorkflowRules.EnsureChecklistComplete(checklist, new Dictionary<string, int> { ["syllabus"] = 1 }));
        Assert.Equal("checklist-incomplete", e.Code);
        WorkflowRules.EnsureChecklistComplete(checklist, new Dictionary<string, int> { ["syllabus"] = 1, ["rubric"] = 2 });
    }

    [Fact]
    public void Reviewed_versions_compare_as_sets()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.True(WorkflowRules.SameVersions([a, b], [b, a]));
        Assert.False(WorkflowRules.SameVersions([a], [a, b]));
    }
}
