using KnowledgeBase.Core.Application.Admin;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Processing jobs, indexing status and audit log (frames 6, 10). Admin only (checked in Core).</summary>
public static class AdminEndpoints
{
    public sealed record RebuildIndexRequest(string? Model);

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin");

        admin.MapGet("/processing-jobs", ([AsParameters] ProcessingJobQuery query, ProcessingAdminService service, CancellationToken ct) =>
                service.ListJobsAsync(query, ct))
            .WithSummary("Processing jobs, problems first");

        admin.MapGet("/indexing-status", (ProcessingAdminService service, CancellationToken ct) =>
                service.GetIndexingStatusAsync(ct))
            .WithSummary("Pipeline stage counts, corpus totals and search index state");

        admin.MapPost("/processing-jobs/{id:guid}/retry", (Guid id, ProcessingAdminService service, CancellationToken ct) =>
                service.RetryAsync(id, ct))
            .WithSummary("Re-queue a failed or dead-lettered job from the first stage");

        admin.MapPost("/processing-jobs/retry-failed", (ProcessingAdminService service, CancellationToken ct) =>
                service.RetryAllFailedAsync(ct))
            .WithSummary("Re-queue every failed and dead-lettered job");

        admin.MapPost("/indexes/rebuild", async (RebuildIndexRequest request, ProcessingAdminService service, CancellationToken ct) =>
            {
                var build = await service.RebuildIndexAsync(request.Model, ct);
                return TypedResults.Accepted((string?)null, build);
            })
            .WithSummary("Re-embed every indexable version into a new vector collection, then swap the alias")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        admin.MapGet("/audit", ([AsParameters] AuditQuery query, AuditQueryService service, CancellationToken ct) =>
                service.ListAsync(query, ct))
            .WithSummary("Audit events, newest first; action \"document.\" matches a prefix");

        admin.MapGet("/audit/actions", (AuditQueryService service, CancellationToken ct) =>
                service.ListActionsAsync(ct))
            .WithSummary("Distinct audit action names");

        return api;
    }
}
