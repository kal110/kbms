using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>
/// "Auth" section. With an identity provider (OIDC <see cref="Authority"/>) the API trusts its tokens.
/// Without one it signs its own with <see cref="SigningKey"/>: password sign-in
/// (<c>POST /api/auth/sign-in</c>), and in Development also <c>POST /api/dev/token</c> for any seeded user.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>The key compose and the example settings ship with: fine on a laptop, refused outside Development.</summary>
    public const string ExampleSigningKey = "dev-only-signing-key-change-me-0123456789abcdef";

    public string? Authority { get; set; }
    public string Audience { get; set; } = "kbms-api";
    public string Issuer { get; set; } = "kbms";
    public string? SigningKey { get; set; }

    /// <summary>The earlier name of <see cref="SigningKey"/>, still read when that is empty.</summary>
    public string? DevSigningKey { get; set; }
    public int TokenHours { get; set; } = 8;

    public bool IssuesTokens => string.IsNullOrWhiteSpace(Authority);

    private string? Secret => string.IsNullOrWhiteSpace(SigningKey) ? DevSigningKey : SigningKey;

    public SymmetricSecurityKey Key()
    {
        if (string.IsNullOrWhiteSpace(Secret) || Secret.Length < 32)
            throw new InvalidOperationException("Auth:SigningKey must be set (32+ characters) when Auth:Authority is empty.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
    }

    public bool UsesExampleKey => Secret == ExampleSigningKey;
}

public static class AuthSetup
{
    public static IServiceCollection AddKbAuthentication(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var options = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        if (options.IssuesTokens && options.UsesExampleKey && !env.IsDevelopment())
            throw new InvalidOperationException(
                "Auth:SigningKey is the example key, which anyone can read in the repository. Set your own (e.g. openssl rand -hex 32).");

        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Keep claim names as issued ("sub", not the long WS-* URIs).
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "name";
                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                if (options.IssuesTokens)
                {
                    jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
                    jwt.TokenValidationParameters.IssuerSigningKey = options.Key();
                }
                else
                {
                    jwt.Authority = options.Authority;
                }
            });
        services.AddAuthorization();
        services.AddSingleton<TokenIssuer>();
        return services;
    }
}

/// <summary>Signs the API's own tokens when there is no identity provider: password and development sign-in.</summary>
public sealed class TokenIssuer(Microsoft.Extensions.Options.IOptions<AuthOptions> options, TimeProvider clock)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(string externalId, string displayName, string? email)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddHours(o.TokenHours);
        var claims = new List<Claim> { new("sub", externalId), new("name", displayName) };
        if (!string.IsNullOrEmpty(email)) claims.Add(new Claim("email", email));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(o.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
