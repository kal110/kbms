using System.Threading.RateLimiting;
using KnowledgeBase.Api.Endpoints;
using KnowledgeBase.Core.Application.Ai;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Api.Infrastructure;

public static class RateLimits
{
    /// <summary>
    /// The assistant: Ai:Retrieval:RequestsPerMinute per signed-in user across /chat and /passages
    /// (frame 10, step 3). Sign-in: 10 attempts a minute per client address, against guessing across
    /// many accounts (the lockout in UserCredential covers one).
    /// </summary>
    public static IServiceCollection AddKbRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.AddPolicy(KnowledgeEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, http.RequestServices.GetRequiredService<IOptions<AiOptions>>().Value.Retrieval.RequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            o.AddPolicy(AuthEndpoints.SignInRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Type = ProblemDetailsExceptionHandler.TypeFor("rate-limited"),
                        Title = policy == AuthEndpoints.SignInRateLimit
                            ? "Too many sign-in attempts from this computer. Wait a minute and try again."
                            : "Too many questions in a minute. Wait a moment and try again.",
                        Extensions = { ["code"] = "rate-limited" },
                    },
                });
            };
        });
}
