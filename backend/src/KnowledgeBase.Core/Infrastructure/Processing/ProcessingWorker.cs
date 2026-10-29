using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Processing;

/// <summary>
/// Claims queued jobs one at a time (FOR UPDATE SKIP LOCKED, so several workers can share the
/// table) and runs them. Failures retry with exponential backoff up to MaxAttempts, then the job
/// is dead-lettered and shows up in Admin → Processing for a manual retry.
/// </summary>
public sealed class ProcessingWorker(
    IServiceScopeFactory scopes,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<ProcessingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.WorkersEnabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds)), clock);
        do
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && await ProcessNextAsync(stoppingToken)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Processing poll failed; retrying in {Seconds}s", o.PollSeconds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs one job; false when nothing is due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();

        var now = clock.GetUtcNow();
        var leaseExpired = now.AddMinutes(-o.LeaseMinutes);
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE processing_jobs
               SET status = 'Running', attempt = attempt + 1, updated_at = {now}
             WHERE id = (
                   SELECT id FROM processing_jobs
                    WHERE (status = 'Queued' AND (next_attempt_at IS NULL OR next_attempt_at <= {now}))
                       OR (status = 'Running' AND updated_at < {leaseExpired})
                    ORDER BY created_at
                    LIMIT 1
                      FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (claimed.Count == 0) return false;

        var job = await db.ProcessingJobs.SingleAsync(j => j.Id == claimed[0], ct);
        var version = await db.DocumentVersions.SingleAsync(v => v.Id == job.DocumentVersionId, ct);
        version.ProcessingStatus = VersionProcessingStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            var result = await processor.RunAsync(version, stage => job.Stage = stage, ct);
            version.PageCount = result.PageCount ?? version.PageCount;
            version.TextObjectKey = result.TextObjectKey ?? version.TextObjectKey;
            // Ready = processed; Indexed = also searchable. A deferred index write leaves it Ready
            // and the index worker promotes it once the version is in the active collection.
            version.ProcessingStatus = result.Indexed ? VersionProcessingStatus.Indexed : VersionProcessingStatus.Ready;
            job.Status = JobStatus.Succeeded;
            job.Stage = ProcessingStage.Done;
            job.NextAttemptAt = null;
            job.LastError = null;
            job.Metrics = KbJson.ToDocument(result.Metrics);
            log.LogInformation("Processed version {VersionId} (attempt {Attempt})", version.Id, job.Attempt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var permanent = ex is StageFailedException { Permanent: true };
            job.LastError = Sanitize(ex);
            if (permanent || job.Attempt >= o.MaxAttempts)
            {
                job.Status = JobStatus.DeadLettered;
                job.NextAttemptAt = null;
                version.ProcessingStatus = VersionProcessingStatus.Failed;
            }
            else
            {
                job.Status = JobStatus.Queued;
                job.NextAttemptAt = clock.GetUtcNow() + Backoff(job.Attempt);
                version.ProcessingStatus = VersionProcessingStatus.Pending;
            }
            log.LogWarning("Processing version {VersionId} failed at {Stage} (attempt {Attempt}): {Error}",
                version.Id, job.Stage, job.Attempt, job.LastError);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    /// <summary>30 s, 1 min, 2 min, 4 min… capped at 30 min.</summary>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(0, attempt - 1)), 30 * 60));

    /// <summary>Admin-facing error text: our own stage messages as-is, anything else by type only.</summary>
    public static string Sanitize(Exception ex)
    {
        var message = ex switch
        {
            StageFailedException stage => stage.Message,
            FileNotFoundException or DirectoryNotFoundException => "The original file is missing from storage.",
            IOException => "Reading from object storage failed.",
            TimeoutException => "The stage timed out.",
            _ => $"Unexpected error ({ex.GetType().Name}).",
        };
        return message.Length <= 1000 ? message : message[..1000];
    }
}
