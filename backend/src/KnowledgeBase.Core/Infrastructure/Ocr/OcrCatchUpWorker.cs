using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ocr;

/// <summary>
/// Once OCR can run (Tesseract in the image), requeues the processing jobs that went without it,
/// so scans and photos uploaded before are read and indexed too. Runs once at startup. A job that
/// ran OCR no longer matches, so nothing is read twice.
/// </summary>
public sealed class OcrCatchUpWorker(
    IServiceScopeFactory scopes,
    IOcrEngine ocr,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<OcrCatchUpWorker> log) : BackgroundService
{
    private const int Attempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkersEnabled) return;

        var status = await ocr.StatusAsync(stoppingToken);
        if (!status.Available)
        {
            log.LogInformation("OCR is off ({Reason}): scanned pages are flagged and skipped.", status.Reason);
            return;
        }
        log.LogInformation("OCR reads scanned pages and images with {Engine}.", status.Engine);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
                var requeued = await RequeueAsync(db, options.Value.PipelineVersion, clock.GetUtcNow(), stoppingToken);
                if (requeued > 0) log.LogInformation("Requeued {Count} document versions for OCR.", requeued);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (attempt == Attempts)
                {
                    log.LogError(ex, "Requeuing documents for OCR failed {Attempts} times; their scanned pages stay unread until they are processed again.", Attempts);
                    return;
                }
                log.LogWarning(ex, "Requeuing documents for OCR failed; trying again in a minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Finished jobs of this pipeline version that skipped OCR for want of an engine, and PNG or
    /// JPEG uploads processed before OCR existed (their notes say the image needs OCR, OCR "not needed").
    /// </summary>
    public static IQueryable<ProcessingJob> Skipped(KbDbContext db, int pipelineVersion) =>
        db.ProcessingJobs.Where(j => j.Status == JobStatus.Succeeded && j.PipelineVersion == pipelineVersion && j.Metrics != null
            && (j.Metrics.RootElement.GetProperty("ocr").GetProperty("status").GetString() == DocumentProcessor.SkippedNoOcr
                || (j.Metrics.RootElement.GetProperty("ocr").GetString() == "not needed"
                    && j.Metrics.RootElement.GetProperty("extract").GetProperty("text").GetString() == DocumentProcessor.SkippedImageNeedsOcr)));

    /// <summary>Puts those jobs back in the queue from the start; Extract reuses its checkpoint.</summary>
    public static async Task<int> RequeueAsync(KbDbContext db, int pipelineVersion, DateTimeOffset now, CancellationToken ct)
    {
        var jobs = await Skipped(db, pipelineVersion).Select(j => new { j.Id, j.DocumentVersionId }).ToListAsync(ct);
        if (jobs.Count == 0) return 0;

        var jobIds = jobs.Select(j => j.Id).ToList();
        var versionIds = jobs.Select(j => j.DocumentVersionId).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var requeued = await db.ProcessingJobs
            .Where(j => jobIds.Contains(j.Id) && j.Status == JobStatus.Succeeded)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.Stage, ProcessingStage.Extract)
                .SetProperty(j => j.Attempt, 0)
                .SetProperty(j => j.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, (string?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);
        await db.DocumentVersions
            .Where(v => versionIds.Contains(v.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, VersionProcessingStatus.Pending), ct);
        await tx.CommitAsync(ct);
        return requeued;
    }
}
