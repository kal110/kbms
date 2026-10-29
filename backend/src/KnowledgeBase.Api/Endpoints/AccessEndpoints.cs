using KnowledgeBase.Core.Application.Access;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Folder/document permission grants (frames 12, 13).</summary>
public static class AccessEndpoints
{
    public static RouteGroupBuilder MapAccessEndpoints(this RouteGroupBuilder api)
    {
        var access = api.MapGroup("").WithTags("Access");

        // Folders: grants flow down to subfolders and documents that inherit.
        access.MapGet("/folders/{id:guid}/access", (Guid id, AccessService service, CancellationToken ct) =>
                service.GetFolderAccessAsync(id, ct))
            .WithSummary("Grants on a folder and everyone's effective level, with where it comes from");

        access.MapPost("/folders/{id:guid}/access", (Guid id, GrantRequest request, AccessService service, CancellationToken ct) =>
                service.GrantFolderAsync(id, request, ct))
            .WithSummary("Grant or change a principal's level on a folder (Manage)");

        access.MapDelete("/folders/{id:guid}/access/{grantId:guid}", async (Guid id, Guid grantId, AccessService service, CancellationToken ct) =>
            {
                await service.RevokeFolderAsync(id, grantId, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Revoke a folder grant (system grants from the org tree can't be revoked)");

        access.MapPut("/folders/{id:guid}/inheritance", (Guid id, InheritanceRequest request, AccessService service, CancellationToken ct) =>
                service.SetFolderInheritanceAsync(id, request.Inherit, ct))
            .WithSummary("Start or stop inheriting the parent folder's access");

        // Documents: their own grants add to what the folder gives.
        access.MapGet("/documents/{id:guid}/access", (Guid id, AccessService service, CancellationToken ct) =>
                service.GetDocumentAccessAsync(id, ct))
            .WithSummary("Grants on a document and everyone's effective level");

        access.MapPost("/documents/{id:guid}/access", (Guid id, GrantRequest request, AccessService service, CancellationToken ct) =>
                service.GrantDocumentAsync(id, request, ct))
            .WithSummary("Grant or change a principal's level on one document (Manage)");

        access.MapDelete("/documents/{id:guid}/access/{grantId:guid}", async (Guid id, Guid grantId, AccessService service, CancellationToken ct) =>
            {
                await service.RevokeDocumentAsync(id, grantId, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Revoke a document grant");

        access.MapPut("/documents/{id:guid}/inheritance", (Guid id, InheritanceRequest request, AccessService service, CancellationToken ct) =>
                service.SetDocumentInheritanceAsync(id, request.Inherit, ct))
            .WithSummary("Start or stop inheriting the folder's access");

        access.MapGet("/access/principals", (string? q, AccessService service, CancellationToken ct) =>
                service.SearchPrincipalsAsync(q, ct))
            .WithSummary("People, groups and org units (and their dean, supervisor or head) to grant access to");

        return api;
    }
}
