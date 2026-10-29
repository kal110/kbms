using KnowledgeBase.Core.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KnowledgeBase.Api.Infrastructure;

/// <summary>
/// Turns every error into RFC 9457 ProblemDetails (frame 12): AppException carries its own status
/// and code; concurrency failures become 409; anything else is a 500 without internals.
/// </summary>
public sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problems, IHostEnvironment env, ILogger<ProblemDetailsExceptionHandler> log)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        // The client went away; nobody is listening for a body.
        if (exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
        {
            http.Response.StatusCode = 499;
            return true;
        }

        var problem = exception switch
        {
            AppException app => FromApp(app),
            DbUpdateConcurrencyException => Problem(409, "concurrent-update", "Someone else changed this at the same time. Reload and try again."),
            // Services validate first; these are the database's backstops (constraints and triggers).
            DbUpdateException { InnerException: PostgresException pg } => pg.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation => Problem(409, "duplicate", "That already exists.", Safe(pg)),
                PostgresErrorCodes.ForeignKeyViolation => Problem(409, "in-use", "This is referenced by other records.", Safe(pg)),
                PostgresErrorCodes.CheckViolation => Problem(422, "constraint-violated", "The change breaks a data rule.", Safe(pg)),
                PostgresErrorCodes.RestrictViolation => Problem(409, "immutable", "This record can't be changed.", Safe(pg)),
                _ => null,
            },
            BadHttpRequestException bad => Problem(bad.StatusCode, "bad-request", "The request couldn't be read.", env.IsDevelopment() ? bad.Message : null),
            _ => null,
        };

        if (problem is null)
        {
            log.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
            problem = Problem(500, "internal", "Something went wrong on our side.", env.IsDevelopment() ? exception.ToString() : null);
        }
        else if (problem.Status >= 500)
        {
            log.LogWarning("{Code}: {Title}", problem.Extensions["code"], problem.Title);
        }

        http.Response.StatusCode = problem.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails FromApp(AppException app)
    {
        var problem = app is ValidationAppException v
            ? new HttpValidationProblemDetails(v.Errors) { Status = 400 }
            : new ProblemDetails();
        problem.Status = app.Status;
        problem.Type = TypeFor(app.Code);
        problem.Title = app.Title;
        problem.Detail = app.Detail;
        problem.Extensions["code"] = app.Code;
        foreach (var (key, value) in app.Extensions) problem.Extensions[key] = value;
        return problem;
    }

    private static ProblemDetails Problem(int status, string code, string title, string? detail = null) => new()
    {
        Status = status,
        Type = TypeFor(code),
        Title = title,
        Detail = detail,
        Extensions = { ["code"] = code },
    };

    public static string TypeFor(string code) => $"/problems/{code}";

    /// <summary>
    /// Trigger errors (no constraint name) carry messages written for people; constraint
    /// violations only reveal their name in development.
    /// </summary>
    private string? Safe(PostgresException pg) =>
        pg.ConstraintName is null ? pg.MessageText
        : env.IsDevelopment() ? $"{pg.MessageText} ({pg.ConstraintName})"
        : null;
}
