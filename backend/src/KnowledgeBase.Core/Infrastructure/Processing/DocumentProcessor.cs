using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Ai;
using KnowledgeBase.Core.Infrastructure.Ocr;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Exceptions;

namespace KnowledgeBase.Core.Infrastructure.Processing;

/// <param name="Indexed">The version's chunks are in the active vector collection.</param>
/// <param name="Pages">The extracted text page by page, for exact search; null when nothing was extracted.</param>
public sealed record ProcessingResult(
    int? PageCount, string? TextObjectKey, IReadOnlyDictionary<string, object?> Metrics, bool Indexed = false, IReadOnlyList<PageText>? Pages = null);

/// <summary>A stage failure with a message that is safe to show admins (no paths, no credentials).</summary>
public sealed class StageFailedException(ProcessingStage stage, string message, bool permanent = false) : Exception(message)
{
    public ProcessingStage Stage { get; } = stage;

    /// <summary>Retrying won't help (e.g. an encrypted PDF); dead-letter straight away.</summary>
    public bool Permanent { get; } = permanent;
}

/// <summary>
/// Runs the stages for one version (frames 6, 8, 25). Every stage checks its checkpoint first:
/// a version is immutable, so what a pipeline version derives from it never changes once
/// written, and a retry picks up where the last attempt stopped. Bumping Processing:PipelineVersion
/// (after changing the extractors or the chunk sizes) derives both again.
///
///   derived/{versionId}/pages-p{n}.jsonl         Extract: typed blocks per page, low-text pages flagged
///   derived/{versionId}/ocr-p{n}.jsonl           OCR: the same pages with the scanned ones read by OCR
///   derived/{versionId}/chunks-p{n}[-ocr].jsonl  Chunk: 500–800-token chunks with pages and section path
///
/// Embed and Index need Ai:Enabled. When the embedding service or the vector store is down, the
/// job still succeeds ("deferred") and the index worker catches the version up later, so document
/// processing never waits on the AI services.
/// </summary>
public sealed partial class DocumentProcessor(
    IObjectStorage storage,
    IOptions<AiOptions> ai,
    IOptions<ProcessingOptions> options,
    IVersionIndexer? indexer = null,
    IOcrEngine? ocr = null)
{
    public const string SkippedAiDisabled = "skipped: AI disabled";
    public const string SkippedNoOcr = "skipped: OCR engine not configured";
    public const string SkippedImageNeedsOcr = "skipped: an image needs OCR";

    public static string PagesKey(Guid versionId, int pipelineVersion) => $"derived/{versionId:N}/pages-p{pipelineVersion}.jsonl";
    public static string OcrKey(Guid versionId, int pipelineVersion) => $"derived/{versionId:N}/ocr-p{pipelineVersion}.jsonl";

    /// <summary>Chunks of OCR text have their own key, so chunks cut before OCR ran are never reused.</summary>
    public static string ChunksKey(Guid versionId, int pipelineVersion, bool ocr = false) =>
        $"derived/{versionId:N}/chunks-p{pipelineVersion}{(ocr ? "-ocr" : "")}.jsonl";

    public async Task<ProcessingResult> RunAsync(DocumentVersion version, Action<ProcessingStage> onStage, CancellationToken ct)
    {
        var metrics = new Dictionary<string, object?>();

        onStage(ProcessingStage.Extract);
        var (extracted, pageCount) = await ExtractAsync(version, metrics, ct);

        onStage(ProcessingStage.Ocr);
        var (text, ocrText) = await OcrAsync(version, extracted, metrics, ct);

        var pipeline = options.Value.PipelineVersion;
        var textKey = text is null ? null : ocrText ? OcrKey(version.Id, pipeline) : PagesKey(version.Id, pipeline);
        var pages = text?.PlainPages();
        if (!ai.Value.Enabled)
        {
            foreach (var stage in new[] { ProcessingStage.Chunk, ProcessingStage.Embed, ProcessingStage.Index })
            {
                onStage(stage);
                metrics[stage.ToString().ToLowerInvariant()] = SkippedAiDisabled;
            }
            onStage(ProcessingStage.Done);
            return new ProcessingResult(pageCount, textKey, metrics, Pages: pages);
        }

        onStage(ProcessingStage.Chunk);
        var chunks = text is null ? [] : await ChunkAsync(version.Id, text, ocrText, metrics, ct);

        var indexed = false;
        if (chunks.Count == 0)
        {
            const string none = "skipped: no text to index";
            onStage(ProcessingStage.Embed);
            onStage(ProcessingStage.Index);
            metrics["embed"] = none;
            metrics["index"] = none;
        }
        else if (indexer is null)
        {
            metrics["embed"] = metrics["index"] = "skipped: no indexer in this host";
        }
        else
        {
            try
            {
                var result = await indexer.IndexAsync(version.Id, chunks, onStage, ct);
                indexed = result.Indexed;
                metrics["embed"] = result.Skipped ?? (object)new Dictionary<string, object?>
                {
                    ["chunks"] = result.Chunks, ["tokens"] = result.Tokens, ["ms"] = result.EmbedMs,
                };
                metrics["index"] = result.Skipped ?? (object)new Dictionary<string, object?> { ["points"] = result.Chunks, ["ms"] = result.IndexMs };
            }
            catch (Exception ex) when (ex is EmbeddingException or VectorIndexException)
            {
                // The AI services are optional for processing: the index worker retries later.
                metrics["embed"] = metrics["index"] = $"deferred: {ex.Message}";
            }
        }

        onStage(ProcessingStage.Done);
        return new ProcessingResult(pageCount, textKey, metrics, indexed, pages);
    }

    /// <summary>
    /// Extract + Chunk only, from their checkpoints when present (the index worker's backfill). OCR
    /// text is used when the processing job left some; OCR itself only runs in the job.
    /// </summary>
    public async Task<IReadOnlyList<TextChunk>> PrepareChunksAsync(DocumentVersion version, CancellationToken ct)
    {
        var pipeline = options.Value.PipelineVersion;
        var ocrKey = OcrKey(version.Id, pipeline);
        var ocrText = await storage.ExistsAsync(ocrKey, ct);
        var chunksKey = ChunksKey(version.Id, pipeline, ocrText);
        if (await storage.ExistsAsync(chunksKey, ct)) return Chunker.FromJsonLines(await ReadTextAsync(chunksKey, ct));

        var metrics = new Dictionary<string, object?>();
        var text = ocrText ? ExtractedText.FromJsonLines(await ReadTextAsync(ocrKey, ct)) : (await ExtractAsync(version, metrics, ct)).Text;
        return text is null ? [] : await ChunkAsync(version.Id, text, ocrText, metrics, ct);
    }

    private async Task<(ExtractedText? Text, int? Pages)> ExtractAsync(DocumentVersion version, Dictionary<string, object?> metrics, CancellationToken ct)
    {
        var pagesKey = PagesKey(version.Id, options.Value.PipelineVersion);
        if (await storage.ExistsAsync(pagesKey, ct))
        {
            var cached = ExtractedText.FromJsonLines(await ReadTextAsync(pagesKey, ct));
            metrics["extract"] = ExtractMetrics(cached, version.FileSizeBytes, checkpoint: true);
            return (cached, version.ContentType == "application/pdf" ? cached.Pages.Count : version.PageCount);
        }

        if (!await storage.ExistsAsync(version.ObjectKey, ct))
            throw new StageFailedException(ProcessingStage.Extract, "The original file is missing from storage.");

        var skipped = new Dictionary<string, object?> { ["bytes"] = version.FileSizeBytes };
        metrics["extract"] = skipped;
        if (version.FileSizeBytes > options.Value.MaxParseBytes)
        {
            skipped["text"] = "skipped: file larger than the parse limit";
            return (null, null);
        }
        if (!TextExtractors.CanExtract(version.ContentType))
        {
            skipped["text"] = version.ContentType.StartsWith("image/", StringComparison.Ordinal)
                ? SkippedImageNeedsOcr
                : $"skipped: no extractor for {version.ContentType}";
            return (null, null);
        }

        var bytes = await ReadBytesAsync(version.ObjectKey, ct);
        var watch = Stopwatch.StartNew();
        ExtractedText text;
        try
        {
            text = TextExtractors.Extract(version.ContentType, bytes, ai.Value.Chunking.LowTextChars);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new StageFailedException(ProcessingStage.Extract, "Encrypted PDF — the owner needs to upload an unprotected copy.", permanent: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A file the parser can't read is still a valid upload; it just isn't searchable.
            skipped["text"] = $"skipped: the file couldn't be parsed ({ex.GetType().Name})";
            return (null, version.ContentType == "application/pdf" ? PdfInspector.CountPages(bytes) : null);
        }

        await storage.PutAsync(pagesKey, new MemoryStream(text.ToJsonLines()), "application/x-ndjson", ct);
        var m = ExtractMetrics(text, version.FileSizeBytes, checkpoint: false);
        m["ms"] = watch.ElapsedMilliseconds;
        metrics["extract"] = m;
        return (text, version.ContentType == "application/pdf" ? text.Pages.Count : null);
    }

    /// <summary>
    /// Reads the pages Extract flagged (little text but an image: a scan), or a whole PNG or JPEG,
    /// and returns the text with those pages replaced; Ocr = it came from OCR (its own checkpoint).
    /// Without an engine the text stays as extracted and the notes say so; OcrCatchUpWorker
    /// requeues those jobs once there is one. A page OCR can't read keeps its extracted text, but
    /// when no page could be read the stage fails, so the job retries and then shows up for a retry.
    /// </summary>
    private async Task<(ExtractedText? Text, bool Ocr)> OcrAsync(
        DocumentVersion version, ExtractedText? extracted, Dictionary<string, object?> metrics, CancellationToken ct)
    {
        var image = extracted is null && version.ContentType.StartsWith("image/", StringComparison.Ordinal);
        int[] wanted = image ? [1] : extracted?.LowTextPages ?? [];
        if (wanted.Length == 0 || (image && !TesseractOcrEngine.CanRead(version.ContentType)))
        {
            metrics["ocr"] = "not needed";
            return (extracted, false);
        }

        var key = OcrKey(version.Id, options.Value.PipelineVersion);
        if (await storage.ExistsAsync(key, ct))
        {
            var cached = ExtractedText.FromJsonLines(await ReadTextAsync(key, ct));
            metrics["ocr"] = new Dictionary<string, object?> { ["extractor"] = cached.Extractor, ["pages"] = Ranges(wanted), ["checkpoint"] = true };
            return (cached, true);
        }

        if (version.FileSizeBytes > options.Value.MaxParseBytes)
        {
            metrics["ocr"] = "skipped: file larger than the parse limit";
            return (extracted, false);
        }
        var status = ocr is null ? new OcrStatus(null, "no OCR engine in this host") : await ocr.StatusAsync(ct);
        if (!status.Available)
        {
            metrics["ocr"] = new Dictionary<string, object?> { ["status"] = SkippedNoOcr, ["pages"] = Ranges(wanted), ["reason"] = status.Reason };
            return (extracted, false);
        }

        var bytes = await ReadBytesAsync(version.ObjectKey, ct);
        var watch = Stopwatch.StartNew();
        var result = await ocr!.ReadAsync(bytes, version.ContentType, wanted, ct);
        if (result.Pages.Count == 0 && result.Failed.Count > 0)
            throw new StageFailedException(ProcessingStage.Ocr, $"OCR couldn't read any page ({result.Failed.Values.First()}).");

        var name = result.Engine.Split(' ')[0];
        var text = extracted is null
            ? new ExtractedText(name, result.Pages.Select(p => new ExtractedPage(p.Number, p.Blocks, p.Chars, HasImages: true, NeedsOcr: false)).ToList())
            : Merge(extracted, result, name);
        await storage.PutAsync(key, new MemoryStream(text.ToJsonLines()), "application/x-ndjson", ct);

        var words = result.Pages.Sum(p => p.Words);
        var m = new Dictionary<string, object?>
        {
            ["engine"] = result.Engine,
            ["pages"] = Ranges(result.Pages.Select(p => p.Number).ToList()),
            ["chars"] = result.Pages.Sum(p => p.Chars),
            // Mean word confidence (0–100): below about 70 the scan is poor and search may miss words.
            ["confidence"] = words == 0 ? 0 : Math.Round(result.Pages.Sum(p => p.Confidence * p.Words) / words),
            ["ms"] = watch.ElapsedMilliseconds,
            ["checkpoint"] = false,
        };
        if (result.Failed.Count > 0)
            m["failed"] = string.Join("; ", result.Failed.GroupBy(f => f.Value).Select(g => $"{Ranges(g.Select(f => f.Key).ToList())} ({g.Key})"));
        if (result.OverLimit.Count > 0) m["overLimit"] = Ranges(result.OverLimit);
        metrics["ocr"] = m;
        return (text, true);
    }

    /// <summary>The extracted pages with each page OCR read replaced; a page it found nothing on keeps what was extracted.</summary>
    private static ExtractedText Merge(ExtractedText extracted, OcrResult result, string engine)
    {
        var read = result.Pages.ToDictionary(p => p.Number);
        var pages = extracted.Pages.Select(p => !read.TryGetValue(p.Number, out var o) ? p
                : o.Blocks.Count > 0 ? new ExtractedPage(p.Number, o.Blocks, o.Chars, p.HasImages, NeedsOcr: false)
                : p with { NeedsOcr = false })
            .ToList();
        return new ExtractedText($"{extracted.Extractor}+{engine}", pages);
    }

    private async Task<IReadOnlyList<TextChunk>> ChunkAsync(
        Guid versionId, ExtractedText text, bool ocrText, Dictionary<string, object?> metrics, CancellationToken ct)
    {
        var key = ChunksKey(versionId, options.Value.PipelineVersion, ocrText);
        IReadOnlyList<TextChunk> chunks;
        var checkpoint = await storage.ExistsAsync(key, ct);
        if (checkpoint)
        {
            chunks = Chunker.FromJsonLines(await ReadTextAsync(key, ct));
        }
        else
        {
            chunks = Chunker.Split(text, ai.Value.Chunking);
            await storage.PutAsync(key, new MemoryStream(Chunker.ToJsonLines(chunks)), "application/x-ndjson", ct);
        }

        metrics["chunk"] = new Dictionary<string, object?>
        {
            ["chunks"] = chunks.Count,
            ["tokens"] = chunks.Sum(c => c.Tokens),
            ["maxTokens"] = chunks.Count == 0 ? 0 : chunks.Max(c => c.Tokens),
            ["avgTokens"] = chunks.Count == 0 ? 0 : (int)chunks.Average(c => c.Tokens),
            ["checkpoint"] = checkpoint,
        };
        return chunks;
    }

    private static Dictionary<string, object?> ExtractMetrics(ExtractedText text, long bytes, bool checkpoint)
    {
        var blocks = text.Pages.SelectMany(p => p.Blocks).ToList();
        return new Dictionary<string, object?>
        {
            ["bytes"] = bytes,
            ["extractor"] = text.Extractor,
            ["pages"] = text.Pages.Count,
            ["chars"] = text.Chars,
            ["headings"] = blocks.Count(b => b.Kind == BlockKind.Heading),
            ["tables"] = blocks.Count(b => b.Kind == BlockKind.Table),
            ["lowTextPages"] = text.LowTextPages.Length,
            ["checkpoint"] = checkpoint,
        };
    }

    private async Task<byte[]> ReadBytesAsync(string key, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(key, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private async Task<string> ReadTextAsync(string key, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(key, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>[1,2,3,7,9,10] → "1–3, 7, 9–10".</summary>
    public static string Ranges(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            parts.Add(start == pages[i] ? $"{start}" : $"{start}–{pages[i]}");
        }
        return string.Join(", ", parts);
    }
}

/// <summary>Just enough PDF parsing to count pages and spot encryption, without a PDF library (the fallback when PdfPig can't parse a file).</summary>
public static partial class PdfInspector
{
    public static int? CountPages(ReadOnlySpan<byte> pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        if (!text.StartsWith("%PDF-", StringComparison.Ordinal)) return null;
        var count = PageObject().Count(text);
        return count > 0 ? count : null;
    }

    public static bool IsEncrypted(ReadOnlySpan<byte> pdf) => EncryptEntry().IsMatch(Encoding.Latin1.GetString(pdf));

    // "/Type /Page" but not "/Type /Pages".
    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
    private static partial Regex PageObject();

    [GeneratedRegex(@"/Encrypt\s+\d+\s+\d+\s+R")]
    private static partial Regex EncryptEntry();
}
