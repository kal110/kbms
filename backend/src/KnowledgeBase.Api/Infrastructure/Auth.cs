using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>
/// "Auth" section. In production the API trusts the university IdP (OIDC <see cref="Authority"/>).
/// In development, with no Authority, it trusts tokens signed with <see cref="DevSigningKey"/>,
/// which <c>POST /api/dev/token</c> issues for any seeded user.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public string? Authority { get; set; }
    public string Audience { get; set; } = "kbms-api";
    public string DevIssuer { get; set; } = "kbms-dev";
    public string? DevSigningKey { get; set; }
    public int DevTokenHours { get; set; } = 8;

    public bool UsesDevTokens => string.IsNullOrWhiteSpace(Authority);

    public SymmetricSecurityKey DevKey()
    {
        if (string.IsNullOrWhiteSpace(DevSigningKey) || DevSigningKey.Length < 32)
            throw new InvalidOperationException("Auth:DevSigningKey must be set (32+ characters) when Auth:Authority is empty.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DevSigningKey));
    }
}

public static class AuthSetup
{
    public static IServiceCollection AddKbAuthentication(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var options = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        if (options.UsesDevTokens && !env.IsDevelopment())
            throw new InvalidOperationException("Auth:Authority is required outside Development.");

        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Keep claim names as issued ("sub", not the long WS-* URIs).
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "name";
                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                if (options.UsesDevTokens)
                {
                    jwt.TokenValidationParameters.ValidIssuer = options.DevIssuer;
                    jwt.TokenValidationParameters.IssuerSigningKey = options.DevKey();
                }
                else
                {
                    jwt.Authority = options.Authority;
                }
            });
        services.AddAuthorization();
        services.AddSingleton<DevTokenIssuer>();
        return services;
    }
}

/// <summary>Development only: signs a token for a seeded user so the SPA and .http files can call the API.</summary>
public sealed class DevTokenIssuer(Microsoft.Extensions.Options.IOptions<AuthOptions> options, TimeProvider clock)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(string externalId, string displayName, string? email)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddHours(o.DevTokenHours);
        var claims = new List<Claim> { new("sub", externalId), new("name", displayName) };
        if (!string.IsNullOrEmpty(email)) claims.Add(new Claim("email", email));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.DevIssuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(o.DevKey(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
