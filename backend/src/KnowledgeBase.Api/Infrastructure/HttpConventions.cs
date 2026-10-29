using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>ETag / If-Match helpers for editable resources (frame 12).</summary>
public static class HttpConventions
{
    /// <summary>The If-Match version, or null when the header is absent or "*". Throws 412 if malformed.</summary>
    public static uint? IfMatch(this HttpRequest request) => ETags.Parse(request.Headers.IfMatch);

    /// <summary>Same as <see cref="IfMatch"/>, but an edit without If-Match is refused (412).</summary>
    public static uint RequireIfMatch(this HttpRequest request) =>
        request.IfMatch() ?? throw new PreconditionFailedException("Send the If-Match header with the ETag you loaded.");

    public static void SetETag(this HttpResponse response, uint version) => response.Headers.ETag = ETags.From(version);

    public static void SetETag(this HttpResponse response, IConcurrencyVersioned entity) => response.SetETag(entity.Version);
}
