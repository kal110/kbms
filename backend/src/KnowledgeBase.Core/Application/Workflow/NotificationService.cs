using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Workflow;

/// <summary>The caller's in-app notifications (written by the workflow in the same unit of work).</summary>
public sealed class NotificationService(KbDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<PagedResult<NotificationItem>> ListAsync(bool unreadOnly, PageRequest page, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        return await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == me.UserId && (!unreadOnly || n.ReadAt == null))
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationItem(n.Id, n.Type, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt))
            .ToPagedAsync(page, ct);
    }

    public async Task<NotificationSummary> SummaryAsync(CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        return new NotificationSummary(await db.Notifications.CountAsync(n => n.UserId == me.UserId && n.ReadAt == null, ct));
    }

    public async Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.UserId == me.UserId, ct)
            ?? throw new NotFoundException("Notification", id);
        if (notification.ReadAt is not null) return;
        notification.ReadAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var now = clock.GetUtcNow();
        return await db.Notifications
            .Where(n => n.UserId == me.UserId && n.ReadAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, now), ct);
    }
}
