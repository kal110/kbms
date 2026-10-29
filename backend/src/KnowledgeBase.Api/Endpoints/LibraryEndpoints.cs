using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Library;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Folders, documents, versions, content download and keyword search (frames 1, 2, 8, 14, 21, 22).</summary>
public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        // Folders (frames 1, 21, 22).
        var folders = api.MapGroup("/folders").WithTags("Folders");

        folders.MapGet("/tree", (string? root, FolderService service, CancellationToken ct) => service.GetTreeAsync(root, ct))
            .WithSummary("The folder tree the caller can navigate (root: academic, offices, library or all)");

        folders.MapGet("/me", (FolderService service, CancellationToken ct) => service.GetMineAsync(ct))
            .WithSummary("The caller's own faculty/staff folders, where their submissions are filed");

        folders.MapGet("/{id:guid}", (Guid id, FolderService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("One folder with breadcrumbs, org context and allowed actions");

        folders.MapGet("/{id:guid}/children", (Guid id, [AsParameters] DocumentListQuery query, FolderService service, CancellationToken ct) =>
                service.GetChildrenAsync(id, query, ct))
            .WithSummary("Subfolders and a page of documents (filter by q, type, year, term, state; sort)");

        folders.MapGet("/{id:guid}/groups", (Guid id, Guid? termId, FolderService service, CancellationToken ct) =>
                service.GetGroupsAsync(id, termId, ct))
            .WithSummary("Documents in the folder and below, counted by academic year and type (optionally one term)");

        folders.MapPost("", async (CreateFolderRequest request, FolderService service, CancellationToken ct) =>
            {
                var folder = await service.CreateAsync(request, ct);
                return TypedResults.Created($"/api/folders/{folder.Folder.Id}", folder);
            })
            .WithSummary("Create a library folder (Edit on the parent; top-level folders are admin only)");

        folders.MapPatch("/{id:guid}", (Guid id, UpdateFolderRequest request, FolderService service, CancellationToken ct) =>
                service.UpdateAsync(id, request, ct))
            .WithSummary("Rename, move or restrict a library folder");

        folders.MapDelete("/{id:guid}", async (Guid id, FolderService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete an empty library folder");

        api.MapPost("/admin/org-sync/folders", (FolderService service, CancellationToken ct) => service.SyncOrgFoldersAsync(ct))
            .WithTags("Folders")
            .WithSummary("Rebuild the college/department/program/faculty and office folders and their system grants");

        // Documents (frames 2, 14).
        var documents = api.MapGroup("/documents").WithTags("Documents");

        documents.MapGet("/{id:guid}", async (Guid id, DocumentService service, HttpResponse response, CancellationToken ct) =>
            {
                var document = await service.GetAsync(id, ct);
                response.Headers.ETag = document.ETag;
                return document;
            })
            .WithSummary("One document with versions, submissions, the caller's level and allowed actions (ETag for If-Match)");

        documents.MapPatch("/{id:guid}", async (Guid id, UpdateDocumentRequest request, DocumentService service, HttpContext http, CancellationToken ct) =>
            {
                var document = await service.UpdateAsync(id, http.Request.RequireIfMatch(), request, ct);
                http.Response.Headers.ETag = document.ETag;
                return document;
            })
            .WithSummary("Edit title, description, type, period or tags (If-Match required)");

        documents.MapGet("/{id:guid}/versions", (Guid id, DocumentService service, CancellationToken ct) => service.GetVersionsAsync(id, ct))
            .WithSummary("Version history, newest first");

        documents.MapGet("/{id:guid}/versions/compare", (Guid id, int from, int to, DocumentService service, CancellationToken ct) =>
                service.CompareAsync(id, from, to, ct))
            .WithSummary("What changed between two version numbers");

        documents.MapPost("/{id:guid}/versions/{versionId:guid}/restore", async (Guid id, Guid versionId, RestoreVersionRequest? request,
                DocumentService service, HttpResponse response, CancellationToken ct) =>
            {
                var document = await service.RestoreVersionAsync(id, versionId, request, ct);
                response.Headers.ETag = document.ETag;
                return document;
            })
            .WithSummary("Make an older version current again (as a new version)");

        documents.MapGet("/{id:guid}/content", async (Guid id, Guid? versionId, bool? inline, bool? redirect,
                DocumentService service, CancellationToken ct) =>
            {
                var link = await service.GetContentLinkAsync(id, versionId, inline ?? false, ct);
                return redirect ?? true ? Results.Redirect(link.Url) : Results.Ok(link);
            })
            .WithSummary("Preview (inline=true, View) or download (Download) a version: 302 to a short-lived signed URL, or the link as JSON with redirect=false");

        documents.MapGet("/{id:guid}/versions/{versionId:guid}/pages/{page:int}", (Guid id, Guid versionId, int page,
                DocumentService service, CancellationToken ct) => service.GetPageTextAsync(id, versionId, page, ct))
            .WithSummary("One page of a version's extracted text (View): previews files the browser can't draw, such as Word documents");

        documents.MapPost("/{id:guid}/archive", async (Guid id, DocumentService service, HttpContext http, CancellationToken ct) =>
            {
                var document = await service.ArchiveAsync(id, http.Request.IfMatch(), ct);
                http.Response.Headers.ETag = document.ETag;
                return document;
            })
            .WithSummary("Archive a document (stays readable, no new versions)");

        documents.MapPost("/{id:guid}/restore", async (Guid id, DocumentService service, HttpContext http, CancellationToken ct) =>
            {
                var document = await service.RestoreAsync(id, http.Request.IfMatch(), ct);
                http.Response.Headers.ETag = document.ETag;
                return document;
            })
            .WithSummary("Bring an archived document back");

        documents.MapDelete("/{id:guid}", async (Guid id, DocumentService service, HttpContext http, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, http.Request.IfMatch(), ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete a document (Manage; refused under legal hold or while in review)");

        // Keyword search (frame 1). The AI assistant's routes live in KnowledgeEndpoints.
        api.MapGet("/knowledge/search", ([AsParameters] SearchQuery query, SearchService service, CancellationToken ct) =>
                service.SearchAsync(query, ct))
            .WithTags("Knowledge")
            .WithSummary("Keyword search over the documents the caller can see (full-text with prefix match, plus title typos)");

        return api;
    }
}
