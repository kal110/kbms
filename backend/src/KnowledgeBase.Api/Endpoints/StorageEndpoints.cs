using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Library;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Upload sessions and the signed local object endpoint (frame 8).</summary>
public static class StorageEndpoints
{
    public static RouteGroupBuilder MapStorageEndpoints(this RouteGroupBuilder api)
    {
        // Upload in three steps: get a ticket, PUT the bytes to its URL, then complete.
        var uploads = api.MapGroup("/uploads").WithTags("Uploads");

        uploads.MapPost("", (CreateUploadRequest request, UploadService service, CancellationToken ct) => service.CreateAsync(request, ct))
            .WithSummary("Start an upload: checks type and size, returns a signed PUT URL valid for 30 minutes");

        uploads.MapPost("/{id:guid}/complete", async (Guid id, CompleteUploadRequest request, UploadService service,
                HttpResponse response, CancellationToken ct) =>
            {
                var document = await service.CompleteAsync(id, request, ct);
                response.Headers.ETag = document.ETag;
                return TypedResults.Created($"/api/documents/{document.Id}", document);
            })
            .WithIdempotency()
            .WithSummary("File the uploaded bytes as a new document, or as a new version of documentId");

        // The local stand-in for S3/Blob presigned URLs: the token is the authorization.
        var objects = api.MapGroup("/storage/objects").WithTags("Storage").AllowAnonymous();

        objects.MapPut("", async (string token, HttpContext http, UploadService service, CancellationToken ct) =>
            {
                // Kestrel's 30 MB default would cut off allowed files; the service enforces the real limit.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = UploadService.MaxBytes;
                await service.ReceiveAsync(token, http.Request.Body, http.Request.ContentLength, http.Request.ContentType, ct);
                return TypedResults.Ok();
            })
            .WithSummary("Receive an upload's bytes (signed PUT URL from POST /api/uploads)");

        objects.MapGet("", async (string token, HttpResponse response, UploadService service, CancellationToken ct) =>
            {
                var stored = await service.OpenAsync(token, ct);
                if (stored.FileName is { } fileName)
                {
                    var disposition = new ContentDispositionHeaderValue(stored.Inline ? "inline" : "attachment");
                    disposition.SetHttpFileName(fileName);
                    response.Headers.ContentDisposition = disposition.ToString();
                }
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.CacheControl = "private, max-age=300";
                return Results.Stream(stored.Content, stored.ContentType, enableRangeProcessing: stored.Content.CanSeek);
            })
            .WithSummary("Serve a stored object (signed GET URL from a document's content link)");

        return api;
    }
}
