using KnowledgeBase.Core.Application.Security;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>The API's identity: the validated JWT's <c>sub</c> and <c>iat</c> claims and the caller's IP.</summary>
public sealed class HttpIdentitySource(IHttpContextAccessor accessor) : IIdentitySource
{
    public string? ExternalId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? user.FindFirst("sub")?.Value : null;
        }
    }

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public DateTimeOffset? IssuedAt =>
        long.TryParse(accessor.HttpContext?.User.FindFirst("iat")?.Value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
