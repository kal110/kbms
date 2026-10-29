using KnowledgeBase.Core.Infrastructure.Persistence;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>Runs work in a transaction, or inside the caller's when one is already open.</summary>
public static class DbTransactions
{
    public static async Task<T> RunAsync<T>(KbDbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await work();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        await tx.CommitAsync(ct);
        return result;
    }

    public static Task RunAsync(KbDbContext db, Func<Task> work, CancellationToken ct) =>
        RunAsync(db, async () =>
        {
            await work();
            return true;
        }, ct);
}
