using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Seed;

/// <summary>
/// Starting data for one module. Contributors run in <see cref="Order"/> and must be idempotent: each
/// checks whether its rows already exist and does nothing if so. Later modules look up earlier
/// rows by natural key (user ExternalId, document type Code, term Code…).
/// </summary>
public interface ISeedContributor
{
    /// <summary>0 the System Administrator and default settings.</summary>
    int Order { get; }
    string Name { get; }
    Task SeedAsync(KbDbContext db, CancellationToken ct);
}

/// <summary>Applies migrations and runs the seed contributors (Database:MigrateOnStartup / Database:Seed).</summary>
public sealed class DatabaseInitializer(KbDbContext db, IEnumerable<ISeedContributor> contributors, ILogger<DatabaseInitializer> log)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0) return;
        log.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var contributor in contributors.OrderBy(c => c.Order))
        {
            log.LogInformation("Seeding {Name}", contributor.Name);
            await contributor.SeedAsync(db, ct);
            db.ChangeTracker.Clear();
        }
    }
}
