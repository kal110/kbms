using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Common;

/// <summary>Page and size from query params (1-based page, size capped at 100).</summary>
public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 100);
    public int Skip => (SafePage - 1) * SafePageSize;
}

/// <summary>Every list endpoint returns its items with the total count (frame 12 conventions).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public static class PagingExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(
        this IQueryable<T> query, PageRequest page, CancellationToken ct = default)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}
