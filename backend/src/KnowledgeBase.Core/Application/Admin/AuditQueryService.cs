using System.Text.Json;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Admin;

public sealed record AuditQuery(
    Guid? ActorUserId = null,
    string? Action = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

public sealed record AuditRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? ActorName,
    string Action,
    string ResourceType,
    Guid? ResourceId,
    Guid? VersionId,
    string? IpAddress,
    JsonElement? Metadata);

/// <summary>Admin → Audit log. Read-only; the rows themselves are written by every module.</summary>
public sealed class AuditQueryService(KbDbContext db, ICurrentUser currentUser)
{
    public async Task<PagedResult<AuditRow>> ListAsync(AuditQuery query, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();

        var events = db.AuditEvents.AsNoTracking().AsQueryable();
        if (query.ActorUserId is { } actor) events = events.Where(e => e.ActorUserId == actor);
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            // "document." matches every document action; a full name matches exactly.
            var action = query.Action.Trim();
            events = action.EndsWith('.')
                ? events.Where(e => e.Action.StartsWith(action))
                : events.Where(e => e.Action == action);
        }
        if (!string.IsNullOrWhiteSpace(query.ResourceType)) events = events.Where(e => e.ResourceType == query.ResourceType);
        if (query.ResourceId is { } resource) events = events.Where(e => e.ResourceId == resource);
        if (query.From is { } from) events = events.Where(e => e.OccurredAt >= from);
        if (query.To is { } to) events = events.Where(e => e.OccurredAt < to);

        var request = new PageRequest(query.Page, query.PageSize);
        var total = await events.CountAsync(ct);
        var page = await (
                from e in events
                join u in db.Users on e.ActorUserId equals u.Id into actors
                from u in actors.DefaultIfEmpty()
                orderby e.OccurredAt descending, e.Id descending
                select new { e, ActorName = u == null ? null : u.DisplayName })
            .Skip(request.Skip)
            .Take(request.SafePageSize)
            .ToListAsync(ct);

        var rows = page.Select(x => new AuditRow(
            x.e.Id, x.e.OccurredAt, x.e.ActorUserId, x.ActorName, x.e.Action, x.e.ResourceType,
            x.e.ResourceId, x.e.VersionId, x.e.IpAddress, x.e.Metadata?.RootElement.Clone())).ToList();
        return new PagedResult<AuditRow>(rows, request.SafePage, request.SafePageSize, total);
    }

    /// <summary>Distinct action names, for the filter dropdown.</summary>
    public async Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        return await db.AuditEvents.AsNoTracking().Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
    }
}
