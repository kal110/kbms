using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Access;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KnowledgeBase.Core.Application.Workflow;

public static class UnitInfoExtensions
{
    public static UnitRef ToRef(this UnitInfo u) => u.ProgramId is { } p
        ? new UnitRef("Program", p, u.ProgramCode!, u.ProgramName!, u.DepartmentId, u.DepartmentName, u.CollegeId, u.CollegeCode)
        : new UnitRef("Department", u.DepartmentId, u.DepartmentCode, u.DepartmentName, u.DepartmentId, u.DepartmentName, u.CollegeId, u.CollegeCode);

    /// <summary>The unit's principal, granted View on published documents so they become shared material.</summary>
    public static (PrincipalType Type, Guid Id) Principal(this UnitInfo u) =>
        u.ProgramId is { } p ? (PrincipalType.Program, p) : (PrincipalType.Department, u.DepartmentId);
}

/// <summary>Org lookups the workflow needs: a submission's unit, its reviewers, people's names.</summary>
public sealed class WorkflowDirectory(KbDbContext db)
{
    public async Task<UnitInfo> GetUnitAsync(Guid? programId, Guid? departmentId, CancellationToken ct = default)
    {
        if (programId is { } p)
            return await db.Programs.AsNoTracking()
                .Where(x => x.Id == p)
                .Select(x => new UnitInfo(x.Id, x.Code, x.Name, x.DepartmentId, x.Department.Code, x.Department.Name,
                    x.Department.CollegeId, x.Department.College!.Code, false))
                .SingleAsync(ct);

        var d = departmentId ?? throw new InvalidOperationException("A submission needs a program or a department.");
        return await db.Departments.AsNoTracking()
            .Where(x => x.Id == d)
            .Select(x => new UnitInfo(null, null, null, x.Id, x.Code, x.Name, x.CollegeId, x.College!.Code,
                x.Kind == DepartmentKind.NonAcademic))
            .SingleAsync(ct);
    }

    /// <summary>The unit's AS / Dean / Head, the VPAF and a System Administrator, all active.</summary>
    public async Task<RoutingUnit> GetRoutingUnitAsync(Guid submitterId, UnitInfo unit, CancellationToken ct = default)
    {
        var scopes = new List<Guid> { unit.DepartmentId };
        if (unit.ProgramId is { } p) scopes.Add(p);
        if (unit.CollegeId is { } c) scopes.Add(c);

        var holders = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.IsActive && r.User.IsActive
                && (r.Role == Role.SystemAdmin || r.Role == Role.Vpaf || (r.ScopeId != null && scopes.Contains(r.ScopeId.Value))))
            .OrderBy(r => r.AssignedAt)
            .Select(r => new { r.Role, r.ScopeId, r.UserId })
            .ToListAsync(ct);

        Guid? Holder(Role role, Guid? scope) =>
            scope is null ? null : holders.FirstOrDefault(h => h.Role == role && h.ScopeId == scope)?.UserId;

        return new RoutingUnit(
            SubmitterId: submitterId,
            ProgramId: unit.ProgramId,
            SupervisorId: Holder(Role.AcademicSupervisor, unit.ProgramId),
            CollegeId: unit.CollegeId,
            DeanId: Holder(Role.Dean, unit.CollegeId),
            IsNonAcademic: unit.IsNonAcademic,
            HeadId: unit.IsNonAcademic ? Holder(Role.DepartmentHead, unit.DepartmentId) : null,
            AdminId: holders.FirstOrDefault(h => h.Role == Role.SystemAdmin && h.UserId != submitterId)?.UserId,
            VpafId: unit.IsNonAcademic ? holders.FirstOrDefault(h => h.Role == Role.Vpaf && h.UserId != submitterId)?.UserId : null);
    }

    /// <summary>The type's approval stages in order, or the single default UnitHead stage.</summary>
    public async Task<IReadOnlyList<(int Order, ReviewerRule Rule)>> GetStagesAsync(Guid submissionTypeId, CancellationToken ct = default)
    {
        var stages = await db.ApprovalStages.AsNoTracking()
            .Where(s => s.SubmissionTypeId == submissionTypeId)
            .OrderBy(s => s.StageOrder)
            .Select(s => new { s.StageOrder, s.ReviewerRule })
            .ToListAsync(ct);
        return stages.Count == 0 ? ReviewerRouting.DefaultStages : stages.Select(s => (s.StageOrder, s.ReviewerRule)).ToList();
    }

    public async Task<Dictionary<Guid, PersonRef>> GetPeopleAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .Select(u => new PersonRef(u.Id, u.DisplayName, u.Title))
            .ToDictionaryAsync(u => u.Id, ct);
    }
}

/// <summary>A submission loaded for the caller, with its unit and the caller's relation to it.</summary>
public sealed record GatedSubmission(UserContext Me, Submission Submission, UnitInfo Unit, SubmissionRelation Relation);

/// <summary>Loads a submission and enforces visibility: anything the caller can't see is a 404.</summary>
public sealed class SubmissionGate(KbDbContext db, ICurrentUser currentUser, WorkflowDirectory directory)
{
    public async Task<GatedSubmission> LoadAsync(Guid id, bool track, CancellationToken ct = default, uint? expectedVersion = null)
    {
        var me = await currentUser.GetAsync(ct);
        IQueryable<Submission> query = db.Submissions
            .Include(s => s.Documents)
            .Include(s => s.Assignments)
            .Include(s => s.RequirementSets)
            .AsSplitQuery();
        if (!track) query = query.AsNoTracking();

        var s = await query.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Submission", id);
        var unit = await directory.GetUnitAsync(s.ProgramId, s.DepartmentId, ct);
        var relation = SubmissionRelation.For(me, s.SubmittedByUserId, s.AssignedReviewerId, s.Status,
            s.Assignments.Select(a => a.ReviewerUserId), unit);
        if (!WorkflowRules.CanView(relation, s.Status)) throw new NotFoundException("Submission", id);

        ETags.Check(expectedVersion, s);
        return new GatedSubmission(me, s, unit, relation);
    }

    /// <summary>403 unless the caller may take <paramref name="action"/> now; 422 when the status is wrong.</summary>
    public static void Require(GatedSubmission g, SubmissionAction action)
    {
        if (WorkflowRules.AllowedActions(g.Relation, g.Submission.Status).Contains(action)) return;
        if (!WorkflowRules.StatusAllows(action, g.Submission.Status)) throw WorkflowRules.InvalidTransition(action, g.Submission.Status);

        throw new ForbiddenException(action switch
        {
            SubmissionAction.Approve or SubmissionAction.RequestRevision or SubmissionAction.Reject or SubmissionAction.Open =>
                "Only the assigned reviewer (or the college Dean) can decide on this submission.",
            SubmissionAction.Reassign => "Only a system administrator or the college Dean can reassign a review.",
            _ => "Only the submitter can do this.",
        });
    }
}

/// <summary>
/// Side effects of a workflow action, all added to the caller's unit of work: review actions,
/// assignments, reviewer grants, notifications, audit and outbox. The service saves once.
/// </summary>
public sealed class WorkflowEffects(KbDbContext db, IAuditWriter audit, IOutbox outbox, TimeProvider clock)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public ReviewAction Record(
        Submission s,
        Guid actorId,
        ReviewActionType type,
        SubmissionStatus? from,
        SubmissionStatus? to,
        string? comments = null,
        IEnumerable<Guid>? versions = null)
    {
        var action = new ReviewAction
        {
            SubmissionId = s.Id,
            ActorUserId = actorId,
            ActionType = type,
            StageOrder = s.CurrentStage,
            PreviousStatus = from,
            NewStatus = to,
            Comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim(),
            OccurredAt = Now,
            DocumentVersionIds = versions?.ToList() ?? [],
        };
        db.ReviewActions.Add(action);
        return action;
    }

    public void Assign(Submission s, int stageOrder, RoutingDecision decision)
    {
        s.Assignments.Add(new SubmissionAssignment
        {
            SubmissionId = s.Id,
            StageOrder = stageOrder,
            ReviewerUserId = decision.ReviewerId,
            Reason = decision.Reason,
            AssignedAt = Now,
        });
        s.AssignedReviewerId = decision.ReviewerId;
        s.CurrentStage = stageOrder;
    }

    public void CompleteOpenAssignments(Submission s)
    {
        foreach (var a in s.Assignments.Where(a => a.CompletedAt is null)) a.CompletedAt = Now;
    }

    /// <summary>Adds or raises a system grant on one document (unique per resource + principal).</summary>
    public async Task GrantAsync(Guid documentId, PrincipalType type, Guid principalId, AccessLevel level, Guid grantedBy, CancellationToken ct)
    {
        var key = PrincipalKeys.For(type, principalId);
        var existing = db.DocumentPermissions.Local.FirstOrDefault(p =>
                p.ResourceType == ResourceType.Document && p.ResourceId == documentId && p.PrincipalKey == key)
            ?? await db.DocumentPermissions.FirstOrDefaultAsync(p =>
                p.ResourceType == ResourceType.Document && p.ResourceId == documentId && p.PrincipalKey == key, ct);

        if (existing is null)
            db.DocumentPermissions.Add(new DocumentPermission
            {
                ResourceType = ResourceType.Document,
                ResourceId = documentId,
                PrincipalType = type,
                PrincipalId = principalId,
                PrincipalKey = key,
                Level = level,
                IsSystem = true,
                GrantedByUserId = grantedBy,
                GrantedAt = Now,
            });
        else if (existing.Level < level)
            existing.Level = level;
    }

    /// <summary>The reviewer can open and download every attached file (frame 13).</summary>
    public async Task GrantReviewerAsync(Submission s, Guid reviewerId, Guid grantedBy, CancellationToken ct)
    {
        foreach (var documentId in s.Documents.Select(d => d.DocumentId).Distinct())
            await GrantAsync(documentId, PrincipalType.User, reviewerId, AccessLevel.Download, grantedBy, ct);
    }

    public void Notify(Guid userId, string type, string title, string? body, string link) =>
        db.Notifications.Add(new Notification
        {
            UserId = userId,
            Type = type,
            Title = Trim(title, 300),
            Body = body is null ? null : Trim(body, 2000),
            Link = link,
            CreatedAt = Now,
        });

    /// <summary>Audit row plus outbox message "submission.{event}" in the same transaction.</summary>
    public void Publish(string evt, Submission s, Guid actorId, object? data = null)
    {
        var payload = new
        {
            submissionId = s.Id,
            number = s.SubmissionNumber,
            status = s.Status.ToString(),
            stage = s.CurrentStage,
            reviewerUserId = s.AssignedReviewerId,
            actorUserId = actorId,
            data,
        };
        audit.Record($"submission.{evt}", "Submission", s.Id, metadata: payload, actorUserId: actorId);
        outbox.Enqueue($"submission.{evt}", payload);
    }

    public static string SubmissionLink(Guid id) => $"/submissions/{id}";

    public static string ReviewLink(Guid id) => $"/review/{id}";

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>"SUB-2026-0187": per calendar year, max + 1, retried when a parallel request took the number.</summary>
public sealed class SubmissionNumbers(KbDbContext db, TimeProvider clock)
{
    public async Task<string> NextAsync(CancellationToken ct = default)
    {
        var prefix = $"SUB-{clock.GetUtcNow().Year}-";
        var last = await db.Submissions.AsNoTracking()
            .Where(s => s.SubmissionNumber.StartsWith(prefix))
            .OrderByDescending(s => s.SubmissionNumber.Length)
            .ThenByDescending(s => s.SubmissionNumber)
            .Select(s => s.SubmissionNumber)
            .FirstOrDefaultAsync(ct);
        var next = last is not null && int.TryParse(last[prefix.Length..], out var n) ? n + 1 : 1;
        return $"{prefix}{next:D4}";
    }

    /// <summary>Saves the unit of work that adds <paramref name="s"/>, renumbering on a clash.</summary>
    public async Task SaveAsync(Submission s, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException e) when (attempt < 5 && e.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "ix_submissions_submission_number",
            })
            {
                s.SubmissionNumber = await NextAsync(ct);
            }
        }
    }
}
