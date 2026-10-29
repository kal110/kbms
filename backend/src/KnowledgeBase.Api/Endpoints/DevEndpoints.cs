using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>
/// Development sign-in: lists the seeded people and issues a token for one of them, standing in
/// for the university IdP. Mapped only in Development when no Auth:Authority is configured.
/// </summary>
public static class DevEndpoints
{
    public sealed record DevUser(string ExternalId, string DisplayName, string Initials, string? Email, string? Title, IReadOnlyList<string> Roles);
    public sealed record DevTokenRequest(string ExternalId);
    public sealed record DevTokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, DevUser User);

    public static RouteGroupBuilder MapDevEndpoints(this RouteGroupBuilder api, IHostEnvironment env)
    {
        var auth = ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!env.IsDevelopment() || !auth.UsesDevTokens) return api;

        var dev = api.MapGroup("/dev").AllowAnonymous().WithTags("Development");

        dev.MapGet("/users", async (KbDbContext db, CancellationToken ct) =>
                await LoadAsync(db.Users.Where(u => u.IsActive).OrderBy(u => u.DisplayName), ct))
            .WithSummary("Seeded people you can sign in as");

        dev.MapPost("/token", async (DevTokenRequest request, KbDbContext db, DevTokenIssuer issuer, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.ExternalId))
                    throw new ValidationAppException(nameof(request.ExternalId), "Pick a user to sign in as.");

                var user = (await LoadAsync(db.Users.Where(u => u.ExternalId == request.ExternalId && u.IsActive), ct)).SingleOrDefault()
                    ?? throw new NotFoundException("User", request.ExternalId);

                var (token, expiresAt) = issuer.Issue(user.ExternalId, user.DisplayName, user.Email);
                return new DevTokenResponse(token, "Bearer", expiresAt, user);
            })
            .WithSummary("Sign in as a seeded person (development only)");

        return api;
    }

    // Initials are computed on User, not stored, so map after loading.
    private static async Task<List<DevUser>> LoadAsync(IQueryable<User> users, CancellationToken ct) =>
        (await users.AsNoTracking()
            .Select(u => new { User = u, Roles = u.Roles.Where(r => r.IsActive).Select(r => r.Role.ToString()).ToList() })
            .ToListAsync(ct))
        .Select(x => new DevUser(x.User.ExternalId, x.User.DisplayName, x.User.Initials, x.User.Email, x.User.Title, x.Roles))
        .ToList();
}
