using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Settings;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Document types, academic years/terms, submission types (frames 17, 19, 20).</summary>
public static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        // Reference lists every signed-in user reads (pickers, term labels).
        var reference = api.MapGroup("").WithTags("Reference data");

        reference.MapGet("/document-types", (bool? includeInactive, DocumentTypeService service, CancellationToken ct) =>
                service.ListAsync(includeInactive ?? false, ct))
            .WithSummary("Active document types (admins may include retired ones)");

        reference.MapGet("/submission-types", (SubmissionTypeService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("Active submission types with their approval stages");

        reference.MapGet("/academic-years", (AcademicCalendarService service, CancellationToken ct) => service.ListYearsAsync(ct))
            .WithSummary("Academic years with their terms, newest first");

        reference.MapGet("/academic-terms/active", (AcademicCalendarService service, CancellationToken ct) => service.GetActiveTermAsync(ct))
            .WithSummary("The active academic term");

        var settings = api.MapGroup("/settings").WithTags("Settings");

        // Document types (frame 17).
        settings.MapGet("/document-types", (DocumentTypeService service, CancellationToken ct) => service.ListForAdminAsync(ct))
            .WithSummary("Every document type with usage, number pattern and retention");

        settings.MapGet("/document-types/{id:guid}", async (Guid id, DocumentTypeService service, HttpResponse response, CancellationToken ct) =>
            {
                var type = await service.GetForAdminAsync(id, ct);
                response.Headers.ETag = type.Etag;
                return type;
            })
            .WithSummary("One document type (ETag for If-Match)");

        settings.MapPost("/document-types", async (CreateDocumentTypeRequest request, DocumentTypeService service, HttpResponse response, CancellationToken ct) =>
            {
                var type = await service.CreateAsync(request, ct);
                response.Headers.ETag = type.Etag;
                return TypedResults.Created($"/api/settings/document-types/{type.Id}", type);
            })
            .WithSummary("Add a document type");

        settings.MapPatch("/document-types/{id:guid}", async (Guid id, UpdateDocumentTypeRequest request, DocumentTypeService service,
                HttpContext http, CancellationToken ct) =>
            {
                var type = await service.UpdateAsync(id, http.Request.IfMatch(), request, ct);
                http.Response.Headers.ETag = type.Etag;
                return type;
            })
            .WithSummary("Edit a document type (code and tag lock once documents use it)");

        settings.MapPost("/document-types/{id:guid}/retire", async (Guid id, RetireDocumentTypeRequest? request, DocumentTypeService service,
                HttpContext http, CancellationToken ct) =>
            {
                var type = await service.RetireAsync(id, http.Request.IfMatch(), request ?? new RetireDocumentTypeRequest(null), ct);
                http.Response.Headers.ETag = type.Etag;
                return type;
            })
            .WithSummary("Retire a document type, optionally naming its replacement");

        settings.MapPost("/document-types/{id:guid}/reactivate", async (Guid id, DocumentTypeService service, HttpContext http, CancellationToken ct) =>
            {
                var type = await service.ReactivateAsync(id, http.Request.IfMatch(), ct);
                http.Response.Headers.ETag = type.Etag;
                return type;
            })
            .WithSummary("Bring a retired document type back");

        settings.MapGet("/retention-policies", (DocumentTypeService service, CancellationToken ct) => service.ListRetentionPoliciesAsync(ct))
            .WithSummary("Retention policies a document type can use");

        // Academic calendar (frame 19).
        settings.MapGet("/academic-terms", (AcademicCalendarService service, CancellationToken ct) => service.ListYearsForAdminAsync(ct))
            .WithSummary("Years and terms with document, submission and requirement-set counts");

        settings.MapPatch("/academic-terms/{id:guid}", async (Guid id, UpdateAcademicTermRequest request, AcademicCalendarService service,
                HttpContext http, CancellationToken ct) =>
            {
                var term = await service.UpdateTermAsync(id, http.Request.IfMatch(), request, ct);
                http.Response.Headers.ETag = term.Etag;
                return term;
            })
            .WithSummary("Set a term's submission window and resubmission rule");

        settings.MapPost("/academic-terms/{id:guid}/activate", async (Guid id, AcademicCalendarService service, HttpContext http, CancellationToken ct) =>
            {
                var term = await service.ActivateTermAsync(id, http.Request.IfMatch(), ct);
                http.Response.Headers.ETag = term.Etag;
                return term;
            })
            .WithSummary("Make a planned term active (closes the current active term)");

        settings.MapPost("/academic-terms/{id:guid}/close", async (Guid id, AcademicCalendarService service, HttpContext http, CancellationToken ct) =>
            {
                var term = await service.CloseTermAsync(id, http.Request.IfMatch(), ct);
                http.Response.Headers.ETag = term.Etag;
                return term;
            })
            .WithSummary("Close the active term");

        settings.MapPost("/academic-terms/sync", async (AcademicCalendarService service, CancellationToken ct) =>
            {
                await service.SyncFromSisAsync(ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Pull years and terms from the SIS (not enabled yet: 501)")
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        // Submission types (frame 20).
        settings.MapGet("/submission-types", (SubmissionTypeService service, CancellationToken ct) => service.ListForAdminAsync(ct))
            .WithSummary("Every submission type, including inactive ones");

        settings.MapPost("/submission-types", async (CreateSubmissionTypeRequest request, SubmissionTypeService service, CancellationToken ct) =>
            {
                var type = await service.CreateAsync(request, ct);
                return TypedResults.Created($"/api/settings/submission-types/{type.Id}", type);
            })
            .WithSummary("Add a submission type with its approval stages");

        settings.MapPatch("/submission-types/{id:guid}", (Guid id, UpdateSubmissionTypeRequest request, SubmissionTypeService service, CancellationToken ct) =>
                service.UpdateAsync(id, request, ct))
            .WithSummary("Edit a submission type or its approval stages");

        return api;
    }
}
