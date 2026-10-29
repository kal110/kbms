using System.Text;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Processing;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Tests;

public class ProcessingTests
{
    private const string TwoPagePdf = """
        %PDF-1.4
        1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
        2 0 obj << /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >> endobj
        3 0 obj << /Type /Page /Parent 2 0 R >> endobj
        4 0 obj << /Type/Page /Parent 2 0 R >> endobj
        trailer << /Root 1 0 R >>
        %%EOF
        """;

    [Fact]
    public void Pdf_pages_are_counted_without_the_pages_tree()
    {
        Assert.Equal(2, PdfInspector.CountPages(Encoding.Latin1.GetBytes(TwoPagePdf)));
        Assert.Null(PdfInspector.CountPages("not a pdf"u8));
    }

    [Fact]
    public void Encrypted_pdf_is_detected()
    {
        Assert.False(PdfInspector.IsEncrypted(Encoding.Latin1.GetBytes(TwoPagePdf)));
        Assert.True(PdfInspector.IsEncrypted(Encoding.Latin1.GetBytes(TwoPagePdf.Replace("/Root 1 0 R", "/Root 1 0 R /Encrypt 9 0 R"))));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(20, 1800)]
    public void Backoff_doubles_and_caps_at_thirty_minutes(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ProcessingWorker.Backoff(attempt));

    [Fact]
    public void Errors_shown_to_admins_hide_paths()
    {
        var message = ProcessingWorker.Sanitize(new FileNotFoundException("Could not find D:\\secret\\storage\\a.pdf"));
        Assert.DoesNotContain("secret", message);
        Assert.Equal("Unexpected error (InvalidOperationException).", ProcessingWorker.Sanitize(new InvalidOperationException("connection string=...")));
    }

    [Fact]
    public async Task Ai_stages_are_recorded_as_skipped_while_ai_is_disabled()
    {
        var storage = new MemoryStorage { ["docs/a.pdf"] = Encoding.Latin1.GetBytes(TwoPagePdf) };
        var processor = new DocumentProcessor(storage, Options.Create(new AiOptions { Enabled = false }), Options.Create(new ProcessingOptions()));
        var stages = new List<ProcessingStage>();

        var result = await processor.RunAsync(Version("docs/a.pdf", "application/pdf", TwoPagePdf.Length), stages.Add, CancellationToken.None);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(DocumentProcessor.SkippedAiDisabled, result.Metrics["embed"]);
        Assert.Equal(DocumentProcessor.SkippedAiDisabled, result.Metrics["index"]);
        Assert.Equal(
            [ProcessingStage.Extract, ProcessingStage.Ocr, ProcessingStage.Chunk, ProcessingStage.Embed, ProcessingStage.Index, ProcessingStage.Done],
            stages);
    }

    [Fact]
    public async Task Missing_original_fails_the_extract_stage()
    {
        var processor = new DocumentProcessor(new MemoryStorage(), Options.Create(new AiOptions()), Options.Create(new ProcessingOptions()));

        var error = await Assert.ThrowsAsync<StageFailedException>(() =>
            processor.RunAsync(Version("docs/missing.pdf", "application/pdf", 10), _ => { }, CancellationToken.None));

        Assert.Equal(ProcessingStage.Extract, error.Stage);
        Assert.False(error.Permanent);
    }

    internal static DocumentVersion Version(string key, string contentType, long size) => new()
    {
        ObjectKey = key,
        OriginalFilename = Path.GetFileName(key),
        ContentType = contentType,
        FileSizeBytes = size,
        Sha256 = new string('0', 64),
        VersionNumber = 1,
    };

    internal sealed class MemoryStorage : Dictionary<string, byte[]>, IObjectStorage
    {
        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            this[key] = buffer.ToArray();
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(this[key]));
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(ContainsKey(key));
        public Task<long?> GetSizeAsync(string key, CancellationToken ct = default) => Task.FromResult(TryGetValue(key, out var b) ? (long?)b.Length : null);
        public PresignedUrl CreateUploadUrl(string key, string contentType, TimeSpan ttl) => throw new NotSupportedException();
        public PresignedUrl CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan ttl, bool inline = false) => throw new NotSupportedException();
    }
}
