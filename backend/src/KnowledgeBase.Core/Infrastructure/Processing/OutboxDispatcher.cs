using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Processing;

/// <summary>
/// Delivers outbox rows to in-process <see cref="IOutboxHandler"/>s. This is where RabbitMQ plugs
/// in later (frame 7): the dispatcher would publish instead of calling handlers. A message with no
/// handler is simply marked processed; a failing one is retried until MaxAttempts.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> log) : BackgroundService
{
    public const int BatchSize = 50;
    public const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.WorkersEnabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds)), clock);
        do
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && await DispatchBatchAsync(stoppingToken) == BatchSize) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Outbox poll failed; retrying in {Seconds}s", o.PollSeconds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Delivers up to <see cref="BatchSize"/> messages; returns how many were taken.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.OutboxMessages.FromSql($"""
            SELECT * FROM outbox_messages
             WHERE processed_at IS NULL AND attempts < {MaxAttempts}
             ORDER BY occurred_at
             LIMIT {BatchSize}
               FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                // Each message gets its own scope, so a handler's unit of work never mixes with ours.
                await using var handlerScope = scopes.CreateAsyncScope();
                var handlers = handlerScope.ServiceProvider.GetServices<IOutboxHandler>()
                    .Where(h => h.MessageType == message.Type);
                foreach (var handler in handlers)
                    await handler.HandleAsync(message.Payload, ct);

                message.ProcessedAt = clock.GetUtcNow();
                message.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var error = $"{ex.GetType().Name}: {ex.Message}";
                message.Attempts++;
                message.LastError = error.Length > 1000 ? error[..1000] : error;
                log.LogWarning(ex, "Outbox message {Id} ({Type}) failed, attempt {Attempt}", message.Id, message.Type, message.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }
}

/// <summary>
/// Every few minutes: revokes expired access grants (and recomputes effective access), then
/// removes short-lived rows — expired idempotency keys and old delivered outbox messages.
/// </summary>
public sealed class HousekeepingWorker(
    IServiceScopeFactory scopes,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<HousekeepingWorker> log) : BackgroundService
{
    public static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(30);

    /// <summary>Also the longest an expired grant keeps working.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkersEnabled) return;

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var folders = await scope.ServiceProvider.GetRequiredService<AccessExpirySweeper>().SweepAsync(stoppingToken);
                if (folders > 0) log.LogInformation("Recomputed access for {Count} folder(s) with expired grants", folders);

                var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
                var now = clock.GetUtcNow();
                var keys = await db.IdempotencyRecords
                    .Where(r => r.CreatedAt < now - IdempotencyLifetime)
                    .ExecuteDeleteAsync(stoppingToken);
                var sent = await db.OutboxMessages
                    .Where(m => m.ProcessedAt != null && m.ProcessedAt < now - OutboxRetention)
                    .ExecuteDeleteAsync(stoppingToken);
                if (keys + sent > 0) log.LogInformation("Housekeeping removed {Keys} idempotency keys and {Sent} outbox messages", keys, sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Housekeeping failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
