using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Requirements;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Requirement sets per college/department/program and the effective checklist (frames 20, 23, 24).</summary>
public static class RequirementsEndpoints
{
    public static RouteGroupBuilder MapRequirementsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Requirements");

        group.MapGet("/scopes/{scope}/{id:guid}/requirement-sets", (string scope, Guid id, RequirementSetService service, CancellationToken ct) =>
                service.ListForScopeAsync(scope, id, ct))
            .WithSummary("A college's, department's or program's lists: published, draft and inherited slots");

        group.MapPost("/scopes/{scope}/{id:guid}/requirement-sets", async (string scope, Guid id, CreateRequirementSetRequest request,
                RequirementSetService service, HttpResponse response, CancellationToken ct) =>
            {
                var set = await service.CreateDraftAsync(scope, id, request, ct);
                response.Headers.ETag = set.Etag;
                return TypedResults.Created($"/api/requirement-sets/{set.Id}", set);
            })
            .WithSummary("Start a draft (copies the published list, or another published list)");

        group.MapGet("/requirement-scopes/tree", (RequirementSetService service, CancellationToken ct) => service.GetScopeTreeAsync(ct))
            .WithSummary("The scopes the caller can set required documents for");

        group.MapGet("/requirement-sets/effective", (Guid submissionTypeId, Guid? termId, Guid? userId, Guid? programId, Guid? departmentId,
                RequirementSetService service, CancellationToken ct) =>
                service.GetEffectiveAsync(submissionTypeId, termId, userId, programId, departmentId, ct))
            .WithSummary("The caller's checklist for a submission type and term (default: the active term), optionally for one of their programs or departments");

        group.MapGet("/requirement-sets/copy-sources", (Guid submissionTypeId, RequirementSetService service, CancellationToken ct) =>
                service.GetCopySourcesAsync(submissionTypeId, ct))
            .WithSummary("Published lists of a submission type to copy from");

        group.MapGet("/requirement-sets/{id:guid}", async (Guid id, RequirementSetService service, HttpResponse response, CancellationToken ct) =>
            {
                var set = await service.GetAsync(id, ct);
                response.Headers.ETag = set.Etag;
                return set;
            })
            .WithSummary("One requirement set (ETag for If-Match)");

        group.MapPut("/requirement-sets/{id:guid}", async (Guid id, UpdateRequirementSetRequest request, RequirementSetService service,
                HttpContext http, CancellationToken ct) =>
            {
                var set = await service.UpdateDraftAsync(id, http.Request.IfMatch(), request, ct);
                http.Response.Headers.ETag = set.Etag;
                return set;
            })
            .WithSummary("Replace a draft's notes, effective term and slots");

        group.MapPost("/requirement-sets/{id:guid}/publish", async (Guid id, RequirementSetService service, HttpContext http, CancellationToken ct) =>
            {
                var set = await service.PublishAsync(id, http.Request.IfMatch(), ct);
                http.Response.Headers.ETag = set.Etag;
                return set;
            })
            .WithSummary("Publish a draft; the previous version is superseded from its effective term");

        group.MapDelete("/requirement-sets/{id:guid}", async (Guid id, RequirementSetService service, HttpContext http, CancellationToken ct) =>
            {
                await service.DeleteDraftAsync(id, http.Request.IfMatch(), ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Discard a draft");

        return api;
    }
}
