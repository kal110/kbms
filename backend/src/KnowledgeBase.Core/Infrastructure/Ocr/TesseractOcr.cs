using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using KnowledgeBase.Core.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Infrastructure.Ocr;

// OCR (frame 8, step 7): scanned pages, and photos of documents, read by Tesseract. A PDF page is
// rendered to an image by pdftoppm (poppler) first. Both are command-line programs in the API's
// Docker image, so OCR works offline; a host without them reports OCR as not configured and
// processing goes on without it.

/// <summary>"Ocr" section.</summary>
public sealed class OcrOptions
{
    public const string Section = "Ocr";

    /// <summary>Off: scanned pages are flagged and skipped, as on a host without Tesseract.</summary>
    public bool Enabled { get; set; } = true;

    public string TesseractPath { get; set; } = "tesseract";
    public string PdfToPpmPath { get; set; } = "pdftoppm";

    /// <summary>Tesseract language codes joined by "+": eng English, fil Filipino. Each needs its tesseract-ocr-{code} package.</summary>
    public string Languages { get; set; } = "eng+fil";

    /// <summary>The resolution PDF pages are rendered at; 300 dpi suits print-size text.</summary>
    public int Dpi { get; set; } = 300;

    /// <summary>Pages read at once, each by its own Tesseract process. 0 = half the CPU cores, at most 4.</summary>
    public int Parallelism { get; set; }

    /// <summary>A page that takes longer is left as extracted and noted as failed.</summary>
    public int PageTimeoutSeconds { get; set; } = 120;

    /// <summary>Pages read per version; a longer scan is read up to here and the rest noted as over the limit.</summary>
    public int MaxPages { get; set; } = 200;

    /// <summary>Words Tesseract is less sure of than this (0–100) are dropped: mostly specks, stamps and photo texture.</summary>
    public int MinWordConfidence { get; set; } = 30;

    public int EffectiveParallelism => Parallelism > 0 ? Parallelism : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

/// <param name="Engine">What reads the pages, e.g. "tesseract 5.3.4 (eng+fil)"; null when OCR can't run here.</param>
/// <param name="Reason">Why OCR can't run, when it can't.</param>
public sealed record OcrStatus(string? Engine, string? Reason)
{
    public bool Available => Engine is not null;
}

/// <param name="Words">Words kept (confident enough).</param>
/// <param name="Confidence">Tesseract's mean confidence over those words, 0–100.</param>
public sealed record OcrPage(int Number, IReadOnlyList<PageBlock> Blocks, int Words, double Confidence)
{
    public int Chars => Blocks.Sum(b => b.Text.Length);
}

/// <param name="Failed">Pages that couldn't be read, with why (safe to show admins).</param>
/// <param name="OverLimit">Pages asked for beyond Ocr:MaxPages, not read.</param>
public sealed record OcrResult(string Engine, IReadOnlyList<OcrPage> Pages, IReadOnlyDictionary<int, string> Failed, IReadOnlyList<int> OverLimit);

public interface IOcrEngine
{
    Task<OcrStatus> StatusAsync(CancellationToken ct = default);

    /// <summary>Reads <paramref name="pages"/> of a PDF, or the one page of a PNG or JPEG (pages = [1]).</summary>
    Task<OcrResult> ReadAsync(byte[] file, string contentType, IReadOnlyList<int> pages, CancellationToken ct = default);
}

public sealed class TesseractOcrEngine(IOptions<OcrOptions> options) : IOcrEngine
{
    private readonly SemaphoreSlim probing = new(1, 1);
    private OcrStatus? status;

    public static bool CanRead(string contentType) => contentType is "application/pdf" or "image/png" or "image/jpeg";

    /// <summary>Checked once: Tesseract, every configured language, and pdftoppm.</summary>
    public async Task<OcrStatus> StatusAsync(CancellationToken ct = default)
    {
        if (status is { } known) return known;
        await probing.WaitAsync(ct);
        try
        {
            return status ??= await ProbeAsync(ct);
        }
        finally
        {
            probing.Release();
        }
    }

    private async Task<OcrStatus> ProbeAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) return new OcrStatus(null, "switched off (Ocr:Enabled)");
        var wait = TimeSpan.FromSeconds(30);
        string? name;
        try
        {
            var version = await RunAsync(o.TesseractPath, ["--version"], wait, ct);
            // Tesseract 5 prints its version on stdout, older ones on stderr.
            name = (version.StdOut + "\n" + version.StdErr).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("tesseract ", StringComparison.Ordinal));
            if (name is null) return new OcrStatus(null, "Tesseract didn't report its version");

            var listed = await RunAsync(o.TesseractPath, ["--list-langs"], wait, ct);
            var installed = listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToHashSet();
            var missing = o.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !installed.Contains(l)).ToList();
            if (missing.Count > 0) return new OcrStatus(null, $"Tesseract has no language data for {string.Join(", ", missing)}");
        }
        catch (Win32Exception)
        {
            return new OcrStatus(null, "Tesseract isn't installed");
        }
        try
        {
            await RunAsync(o.PdfToPpmPath, ["-v"], wait, ct);
        }
        catch (Win32Exception)
        {
            return new OcrStatus(null, "pdftoppm (poppler-utils) isn't installed");
        }
        return new OcrStatus($"{name} ({o.Languages})", null);
    }

    public async Task<OcrResult> ReadAsync(byte[] file, string contentType, IReadOnlyList<int> pages, CancellationToken ct = default)
    {
        var engine = await StatusAsync(ct);
        if (!engine.Available) throw new InvalidOperationException($"OCR can't run here: {engine.Reason}.");
        if (!CanRead(contentType)) throw new ArgumentException($"OCR doesn't read {contentType}.", nameof(contentType));

        var o = options.Value;
        var wanted = pages.Distinct().Order().ToList();
        var read = wanted.Take(Math.Max(1, o.MaxPages)).ToList();
        var pdf = contentType == "application/pdf";
        var parallel = Math.Min(o.EffectiveParallelism, read.Count);

        var dir = Directory.CreateTempSubdirectory("kbms-ocr-");
        try
        {
            var source = Path.Combine(dir.FullName, pdf ? "source.pdf" : contentType == "image/png" ? "source.png" : "source.jpg");
            await File.WriteAllBytesAsync(source, file, ct);

            var done = new ConcurrentBag<OcrPage>();
            var failed = new ConcurrentDictionary<int, string>();
            await Parallel.ForEachAsync(read, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (page, token) =>
            {
                try
                {
                    done.Add(await ReadPageAsync(source, pdf, page, dir.FullName, singleThread: parallel > 1, token));
                }
                catch (OcrPageException ex)
                {
                    failed[page] = ex.Message;
                }
            });
            return new OcrResult(engine.Engine!, done.OrderBy(p => p.Number).ToList(), new SortedDictionary<int, string>(failed), wanted.Skip(read.Count).ToList());
        }
        finally
        {
            try { dir.Delete(recursive: true); }
            catch (IOException) { /* a file still open on Windows; the temp folder is cleaned later */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<OcrPage> ReadPageAsync(string source, bool pdf, int page, string dir, bool singleThread, CancellationToken ct)
    {
        var o = options.Value;
        var timeout = TimeSpan.FromSeconds(Math.Max(5, o.PageTimeoutSeconds));
        var image = source;
        List<string> dpi = [];
        if (pdf)
        {
            var prefix = Path.Combine(dir, $"page-{page}");
            var render = await RunAsync(o.PdfToPpmPath,
                ["-f", $"{page}", "-l", $"{page}", "-r", $"{o.Dpi}", "-gray", "-png", "-singlefile", source, prefix], timeout, ct);
            image = prefix + ".png";
            if (render.TimedOut) throw new OcrPageException("rendering the page timed out");
            if (render.ExitCode != 0 || !File.Exists(image)) throw new OcrPageException("the page couldn't be rendered");
            // A photo carries no reliable resolution, so Tesseract estimates it; a rendered page has a known one.
            dpi = ["--dpi", $"{o.Dpi}"];
        }

        try
        {
            // Page segmentation mode 1 detects the orientation first, so sideways and upside-down scans read too.
            var tsv = await RunAsync(o.TesseractPath, [image, "stdout", "-l", o.Languages, "--psm", "1", .. dpi, "tsv"], timeout, ct, singleThread);
            if (tsv.TimedOut) throw new OcrPageException("Tesseract timed out");
            if (tsv.ExitCode != 0) throw new OcrPageException($"Tesseract stopped with exit code {tsv.ExitCode}");
            return TesseractTsv.ToPage(page, tsv.StdOut, o.MinWordConfidence);
        }
        finally
        {
            if (pdf) File.Delete(image);
        }
    }

    private sealed record ToolRun(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    /// <exception cref="Win32Exception">The program isn't installed (not on the PATH).</exception>
    private static async Task<ToolRun> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, bool singleThread = false)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Pages already run in parallel processes; Tesseract's own threads would only compete with them.
        if (singleThread) start.Environment["OMP_THREAD_LIMIT"] = "1";

        using var process = Process.Start(start) ?? throw new Win32Exception($"{program} didn't start.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* it exited meanwhile */ }
            ct.ThrowIfCancellationRequested();
            return new ToolRun(-1, "", "", TimedOut: true);
        }
        return new ToolRun(process.ExitCode, await stdout, await stderr, TimedOut: false);
    }
}

/// <summary>A page OCR couldn't read; the message is safe to show admins (no paths).</summary>
public sealed class OcrPageException(string message) : Exception(message);
