using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Processing;

/// <summary>"Processing" section. The workers run inside the API host for now (frame 7: Worker host later).</summary>
public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>Run the processing and outbox workers in this host. An MCP host would turn this off.</summary>
    public bool WorkersEnabled { get; set; } = true;
    public int PollSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Bump when stage logic changes; one job per (version, pipeline version).</summary>
    public int PipelineVersion { get; set; } = 1;

    /// <summary>A Running job not touched for this long is assumed dead and claimed again.</summary>
    public int LeaseMinutes { get; set; } = 10;

    /// <summary>Files larger than this are stored but not parsed (page count stays unknown).</summary>
    public long MaxParseBytes { get; set; } = 50L * 1024 * 1024;
}

/// <summary>
/// Queues a new version for processing in the caller's unit of work: the job row and the
/// "document.version-created" outbox message commit together with the version itself.
/// </summary>
public sealed class ProcessingPipeline(KbDbContext db, IOutbox outbox, IOptions<ProcessingOptions> options) : IProcessingPipeline
{
    public void Enqueue(DocumentVersion version)
    {
        db.ProcessingJobs.Add(new ProcessingJob
        {
            DocumentVersionId = version.Id,
            PipelineVersion = options.Value.PipelineVersion,
        });
        outbox.Enqueue("document.version-created", new
        {
            documentId = version.DocumentId,
            versionId = version.Id,
            versionNumber = version.VersionNumber,
        });
    }
}
