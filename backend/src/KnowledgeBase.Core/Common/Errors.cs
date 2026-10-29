namespace KnowledgeBase.Core.Common;

/// <summary>
/// Base for every error the Core raises on purpose. The API turns these into RFC 9457 ProblemDetails
/// (type, title, status, detail + extensions), so the HTTP contract lives in one place (frame 12).
/// </summary>
public abstract class AppException(string code, string title, int status, string? detail = null)
    : Exception(detail ?? title)
{
    /// <summary>Kebab-case code; the problem type URI is "/problems/{code}".</summary>
    public string Code { get; } = code;
    public string Title { get; } = title;
    public int Status { get; } = status;
    public string? Detail { get; } = detail;
    public Dictionary<string, object?> Extensions { get; } = [];

    public AppException With(string key, object? value)
    {
        Extensions[key] = value;
        return this;
    }
}

/// <summary>400 — the request itself is malformed.</summary>
public sealed class ValidationAppException(IDictionary<string, string[]> errors)
    : AppException("validation", "One or more fields are invalid.", 400)
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public ValidationAppException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }
}

/// <summary>401 — no signed-in user, or the user is unknown or inactive.</summary>
public sealed class UnauthenticatedException(string? detail = null)
    : AppException("unauthenticated", "Sign in to continue.", 401, detail);

/// <summary>401 or 429 from password sign-in: a wrong sign-in id or password, or locked after too many.</summary>
public sealed class SignInException(string code, string title, int status, string? detail = null)
    : AppException(code, title, status, detail);

/// <summary>403 — the caller can see the resource but the policy forbids the action.</summary>
public sealed class ForbiddenException(string? detail = null)
    : AppException("forbidden", "You don't have permission to do this.", 403, detail);

/// <summary>404 — missing, or not visible to the caller (never 403, so IDs can't be probed).</summary>
public sealed class NotFoundException(string resource, object? id = null)
    : AppException("not-found", $"{resource} not found.", 404, id is null ? null : $"{resource} '{id}' was not found.");

/// <summary>409 — stale or concurrent change (stale-version, set-not-draft, system-folder, …).</summary>
public sealed class ConflictException(string code, string title, string? detail = null)
    : AppException(code, title, 409, detail);

/// <summary>412 — If-Match didn't match the current ETag.</summary>
public sealed class PreconditionFailedException(string? detail = null)
    : AppException("precondition-failed", "The resource changed since you loaded it.", 412, detail);

/// <summary>422 — a valid request that breaks a business rule or an invalid state transition.</summary>
public sealed class BusinessRuleException(string code, string title, string? detail = null)
    : AppException(code, title, 422, detail);

/// <summary>501 — a feature that is designed but not switched on yet (e.g. the AI assistant).</summary>
public sealed class FeatureDisabledException(string feature, string? detail = null)
    : AppException("feature-disabled", $"{feature} is not enabled yet.", 501, detail);

/// <summary>503 — a dependency the feature needs (embeddings, vector index, chat model) is unavailable or not ready.</summary>
public sealed class ServiceUnavailableException(string code, string title, string? detail = null)
    : AppException(code, title, 503, detail);
