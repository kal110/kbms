using KnowledgeBase.Core.Application.Security;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>The API's identity: the validated JWT's <c>sub</c> claim and the caller's IP.</summary>
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
}
