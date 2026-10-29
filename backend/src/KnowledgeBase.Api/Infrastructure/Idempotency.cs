using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Processing;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>
/// <c>Idempotency-Key</c> for the actions a flaky network must not repeat: upload complete,
/// submit, resubmit and review decisions (frame 12). The first request with a key runs and its
/// response is stored per user + route; a retry with the same key and body replays it; the same
/// key with a different body is refused (422); a retry while the first is still running gets 409.
/// Without the header the endpoint runs normally.
/// </summary>
public static class Idempotency
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Marks a POST endpoint as honouring Idempotency-Key.</summary>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<IdempotencyFilter>();

    /// <summary>Lets the filter re-read a request body that model binding already consumed.</summary>
    public static IApplicationBuilder UseIdempotencyBuffering(this IApplicationBuilder app) =>
        app.Use((http, next) =>
        {
            if (http.Request.Headers.ContainsKey(Header)) http.Request.EnableBuffering();
            return next(http);
        });
}

public sealed class IdempotencyFilter(
    ICurrentUser currentUser,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<JsonOptions> json) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[Idempotency.Header].ToString();
        if (string.IsNullOrWhiteSpace(key)) return await next(context);
        if (key.Length > 100) throw new ValidationAppException(Idempotency.Header, "Use a key of at most 100 characters.");

        var user = await currentUser.GetAsync(http.RequestAborted);
        var route = $"{http.Request.Method} {http.Request.Path}";
        var hash = await HashBodyAsync(http.Request);

        // A separate context, so the record never shares a unit of work with the endpoint.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();

        var existing = await db.IdempotencyRecords
            .SingleOrDefaultAsync(r => r.UserId == user.UserId && r.Key == key && r.Route == route, http.RequestAborted);
        if (existing is not null)
        {
            if (existing.CreatedAt + Idempotency.Lifetime < clock.GetUtcNow())
            {
                db.IdempotencyRecords.Remove(existing);
                await db.SaveChangesAsync(http.RequestAborted);
            }
            else
            {
                if (existing.RequestHash != hash)
                    throw new BusinessRuleException("idempotency-key-reused", "This Idempotency-Key was already used with a different request.");
                if (existing.StatusCode == 0)
                    throw new ConflictException("idempotency-in-progress", "The first request with this Idempotency-Key is still running.");
                http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Content(existing.ResponseBody ?? "", existing.ResponseContentType, statusCode: existing.StatusCode);
            }
        }

        // Claim the key first (StatusCode 0 = running); the primary key stops a parallel duplicate.
        var record = new IdempotencyRecord
        {
            Key = key,
            UserId = user.UserId,
            Route = route,
            RequestHash = hash,
            CreatedAt = clock.GetUtcNow(),
        };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("idempotency-in-progress", "The first request with this Idempotency-Key is still running.");
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            // Failed requests aren't remembered, so the client can retry with the same key.
            db.IdempotencyRecords.Remove(record);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        (record.StatusCode, record.ResponseBody, record.ResponseContentType) = Capture(result);
        await db.SaveChangesAsync(CancellationToken.None);
        return result;
    }

    private (int Status, string? Body, string? ContentType) Capture(object? result)
    {
        var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;
        object? value = result switch
        {
            IValueHttpResult v => v.Value,
            IResult => null,
            _ => result,
        };
        return value is null
            ? (status, null, null)
            : (status, JsonSerializer.Serialize(value, value.GetType(), json.Value.SerializerOptions), "application/json; charset=utf-8");
    }

    private static async Task<string> HashBodyAsync(HttpRequest request)
    {
        if (!request.Body.CanSeek) return Convert.ToHexStringLower(SHA256.HashData([]));
        request.Body.Position = 0;
        var hash = await SHA256.HashDataAsync(request.Body, request.HttpContext.RequestAborted);
        request.Body.Position = 0;
        return Convert.ToHexStringLower(hash);
    }
}
