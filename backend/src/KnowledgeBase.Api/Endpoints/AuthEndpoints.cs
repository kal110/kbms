using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Application.Org;
using KnowledgeBase.Core.Application.Security;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>
/// Signing in to KBMS itself while there is no identity provider (Auth:Authority empty): the API
/// checks the password and signs the token. With an identity provider only /options is mapped.
/// </summary>
public static class AuthEndpoints
{
    public const string SignInRateLimit = "sign-in";

    /// <summary>PasswordSignIn: the sign-in form works. DevSignIn: Development's pick-a-person list too.</summary>
    public sealed record SignInOptions(bool PasswordSignIn, bool DevSignIn);

    public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, SignedInUser User);

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api, IHostEnvironment env)
    {
        var options = ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var auth = api.MapGroup("/auth").WithTags("Sign-in");

        auth.MapGet("/options", () => new SignInOptions(options.IssuesTokens, options.IssuesTokens && env.IsDevelopment()))
            .AllowAnonymous()
            .WithSummary("How people sign in to this API");

        if (!options.IssuesTokens) return api;

        auth.MapPost("/sign-in", async (PasswordSignInRequest request, PasswordSignInService service, TokenIssuer issuer, CancellationToken ct) =>
                Issue(issuer, await service.SignInAsync(request, ct)))
            .AllowAnonymous()
            .RequireRateLimiting(SignInRateLimit)
            .WithSummary("Sign in with a sign-in id (or email) and password")
            .WithDescription("A temporary password answers 422 password-change-required: send it again with newPassword.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/password", async (ChangePasswordRequest request, PasswordSignInService service, TokenIssuer issuer, CancellationToken ct) =>
                Issue(issuer, await service.ChangePasswordAsync(request, ct)))
            .RequireRateLimiting(SignInRateLimit)
            .WithSummary("Change your password")
            .WithDescription("Returns a new token for this session; the account's other sessions end.");

        api.MapPut("/admin/users/{id:guid}/password", (Guid id, SetPasswordRequest request, UserAdminService service, CancellationToken ct) =>
                service.SetPasswordAsync(id, request, ct))
            .WithTags("Org administration")
            .WithSummary("Set or reset someone's password")
            .WithDescription("mustChange makes it temporary. Clears a lockout and ends their open sessions.");

        return api;
    }

    private static TokenResponse Issue(TokenIssuer issuer, SignedInUser user)
    {
        var (token, expiresAt) = issuer.Issue(user.ExternalId, user.DisplayName, user.Email);
        return new TokenResponse(token, "Bearer", expiresAt, user);
    }
}
