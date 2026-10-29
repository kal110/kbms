using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Workflow;
using KnowledgeBase.Core.Common;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Submissions, reviews, college overview and notifications (frames 3, 4, 9, 15, 16, 22, 24).</summary>
public static class WorkflowEndpoints
{
    public static RouteGroupBuilder MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        var submissions = api.MapGroup("/submissions").WithTags("Submissions");

        submissions.MapGet("", async ([AsParameters] SubmissionListQuery query, SubmissionQueries queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.ListAsync(query, ct)))
            .WithSummary("List submissions: scope = mine · reviewing · unit · college");

        submissions.MapPost("", async (CreateSubmissionRequest request, SubmissionService service, HttpResponse response, CancellationToken ct) =>
            {
                var detail = await service.CreateAsync(request, ct);
                response.SetETag(detail.Version);
                return TypedResults.Created($"/api/submissions/{detail.Id}", detail);
            })
            .WithSummary("Create a draft against the effective checklist");

        submissions.MapGet("/{id:guid}", async (Guid id, SubmissionQueries queries, HttpResponse response, CancellationToken ct) =>
                Detail(response, await queries.GetDetailAsync(id, ct)))
            .WithSummary("Submission detail with checklist, stages and allowed actions");

        submissions.MapPatch("/{id:guid}", async (Guid id, UpdateSubmissionRequest request, SubmissionService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.UpdateAsync(id, request, http.Request.IfMatch(), ct)))
            .WithSummary("Rename a draft or a submission under revision (If-Match)");

        submissions.MapDelete("/{id:guid}", async (Guid id, SubmissionService service, HttpRequest request, CancellationToken ct) =>
            {
                await service.DeleteDraftAsync(id, request.IfMatch(), ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete a draft");

        submissions.MapGet("/{id:guid}/timeline", async (Guid id, SubmissionQueries queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.GetTimelineAsync(id, ct)))
            .WithSummary("Append-only history, oldest first");

        submissions.MapPost("/{id:guid}/attachments", async (Guid id, AddAttachmentRequest request, SubmissionService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.AddAttachmentAsync(id, request, http.Request.IfMatch(), ct)))
            .WithSummary("Attach a completed upload to a checklist slot (or add a new version)");

        submissions.MapDelete("/{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, SubmissionService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.RemoveAttachmentAsync(id, attachmentId, http.Request.IfMatch(), ct)))
            .WithSummary("Take a file out of the checklist");

        submissions.MapPost("/{id:guid}/submit", async (Guid id, SubmissionService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.SubmitAsync(id, http.Request.IfMatch(), ct)))
            .WithIdempotency()
            .WithSummary("Submit for review: pins the versions and routes stage 1");

        submissions.MapPost("/{id:guid}/resubmit", async (Guid id, [FromBody] ResubmitRequest? request, SubmissionService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.ResubmitAsync(id, request, http.Request.IfMatch(), ct)))
            .WithIdempotency()
            .WithSummary("Resubmit after a revision request");

        submissions.MapPost("/{id:guid}/withdraw", async (Guid id, SubmissionService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.WithdrawAsync(id, http.Request.IfMatch(), ct)))
            .WithSummary("Withdraw back to draft");

        submissions.MapPost("/{id:guid}/open", async (Guid id, ReviewService service, HttpResponse response, CancellationToken ct) =>
                Detail(response, await service.OpenAsync(id, ct)))
            .WithSummary("Start reviewing (Submitted/Resubmitted → UnderReview)");

        submissions.MapPost("/{id:guid}/approve", async (Guid id, DecisionRequest request, ReviewService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.ApproveAsync(id, request, http.Request.IfMatch(), ct)))
            .WithIdempotency()
            .WithSummary("Approve the reviewed versions (next stage, or publish)");

        submissions.MapPost("/{id:guid}/request-revision", async (Guid id, DecisionRequest request, ReviewService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.RequestRevisionAsync(id, request, http.Request.IfMatch(), ct)))
            .WithIdempotency()
            .WithSummary("Request a revision (comment required)");

        submissions.MapPost("/{id:guid}/reject", async (Guid id, DecisionRequest request, ReviewService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.RejectAsync(id, request, http.Request.IfMatch(), ct)))
            .WithIdempotency()
            .WithSummary("Reject (explanation required); never published");

        submissions.MapPost("/{id:guid}/reassign", async (Guid id, ReassignRequest request, ReviewService service, HttpContext http, CancellationToken ct) =>
                Detail(http.Response, await service.ReassignAsync(id, request, http.Request.IfMatch(), ct)))
            .WithSummary("Hand the current stage to another reviewer (admin or college Dean)");

        api.MapGet("/reviews/pending", async ([AsParameters] PageQuery page, ReviewService service, CancellationToken ct) =>
                TypedResults.Ok(await service.PendingAsync(page.ToRequest(), ct)))
            .WithTags("Reviews")
            .WithSummary("Submissions waiting for me, longest-waiting first");

        api.MapGet("/dashboard/submissions", async (Guid? collegeId, Guid? programId, Guid? departmentId, bool? nonAcademic, Guid? termId,
                SubmissionDashboardService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(collegeId, programId, departmentId, nonAcademic == true, termId, ct)))
            .WithTags("Reviews")
            .WithSummary("Submissions dashboard: all colleges (VPAF, admin), a college (Dean) or a program (AS); nonAcademic=true for every " +
                "non-academic department (VPAF, admin), departmentId for one (its Head). Pass collegeId, programId or departmentId to drill in");

        var notifications = api.MapGroup("/notifications").WithTags("Notifications");

        notifications.MapGet("", async (bool? unread, [AsParameters] PageQuery page, NotificationService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(unread == true, page.ToRequest(), ct)))
            .WithSummary("My notifications, newest first");

        notifications.MapGet("/summary", async (NotificationService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SummaryAsync(ct)))
            .WithSummary("Unread count");

        notifications.MapPost("/{id:guid}/read", async (Guid id, NotificationService service, CancellationToken ct) =>
            {
                await service.MarkReadAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Mark one notification read");

        notifications.MapPost("/read-all", async (NotificationService service, CancellationToken ct) =>
                TypedResults.Ok(new { updated = await service.MarkAllReadAsync(ct) }))
            .WithSummary("Mark all my notifications read");

        return api;
    }

    private static Ok<SubmissionDetail> Detail(HttpResponse response, SubmissionDetail detail)
    {
        response.SetETag(detail.Version);
        return TypedResults.Ok(detail);
    }

    /// <summary>?page=&amp;pageSize= for list endpoints.</summary>
    public sealed record PageQuery(int Page = 1, int PageSize = 25)
    {
        public PageRequest ToRequest() => new(Page, PageSize);
    }
}
