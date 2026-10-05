using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// A TerraBase request that the server answered with an error status. TerraBase error bodies are
/// <c>{"error":{"code","message","op"}}</c> or, on some Management API failures, a plain JSON string; their messages
/// describe the rule that failed and never echo request input. Subclasses name the statuses callers act on.
/// </summary>
public class TerraBaseException : HttpRequestException
{
    private const int MaxErrorBytes = 64 * 1024;
    private const int MaxMessageLength = 1024;

    internal TerraBaseException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = errorCode;
        OperationIndex = operationIndex;
    }

    /// <summary>TerraBase's machine-readable code, such as <c>not_found</c> or <c>precondition_failed</c>, when the body had one.</summary>
    public string? ErrorCode { get; }

    /// <summary>Zero-based index of the transaction operation that failed, when the request was a transaction.</summary>
    public int? OperationIndex { get; }

    internal static async Task<TerraBaseException> FromResponseAsync(
        HttpStatusCode status, HttpContent content, CancellationToken cancellationToken)
    {
        string? code = null;
        string? message = null;
        int? op = null;
        try
        {
            using var body = new BoundedHttpBodyStream(MaxErrorBytes, () => new InvalidDataException(), cancellationToken);
            await content.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
            if (body.Length > 0)
            {
                using var json = JsonDocument.Parse(body.ToArray());
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    message = root.GetString();
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    op = error.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var index) ? index : null;
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or HttpRequestException)
        {
            // An unreadable error body still reports its status.
        }

        // A Management 404 has an empty body that TerraBase fills with the generic unknown_route code.
        var text = Clean(message) ?? $"TerraBase answered HTTP {(int)status}{(code is null ? null : $" ({Clean(code)})")}.";
        return (int)status switch
        {
            400 or 413 or 415 => new TerraBaseValidationException(status, text, code, op),
            401 => new TerraBaseAuthenticationException(status, text, code, op),
            403 => new TerraBaseForbiddenException(status, text, code, op),
            404 => new TerraBaseNotFoundException(status, text, code, op),
            409 => new TerraBaseConflictException(status, text, code, op),
            412 => new TerraBasePreconditionFailedException(status, text, code, op),
            503 => new TerraBaseUnavailableException(status, text, code, op),
            _ => new TerraBaseException(status, text, code, op),
        };
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new string([.. value.Where(c => !char.IsControl(c))]);
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }
}

/// <summary>400, 413 or 415: the request broke a validation rule (<c>invalid_request</c>, <c>invalid_query</c>, <c>invalid_patch</c>, …).</summary>
public sealed class TerraBaseValidationException : TerraBaseException
{
    internal TerraBaseValidationException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}

/// <summary>401: the API key or service credential is missing or unknown.</summary>
public sealed class TerraBaseAuthenticationException : TerraBaseException
{
    internal TerraBaseAuthenticationException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}

/// <summary>403: the key lacks a scope, or the acting member's role does not allow the operation.</summary>
public sealed class TerraBaseForbiddenException : TerraBaseException
{
    internal TerraBaseForbiddenException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}

/// <summary>
/// 404: the resource, or one of its parents, does not exist or is not reachable by the caller. The Documents API
/// sends <c>not_found</c> or <c>unknown_database</c>. Every Management API 404 (an unknown id, or an organization the
/// acting user is not a member of) reports <c>unknown_route</c>, the same code as a wrong path or listener.
/// </summary>
public sealed class TerraBaseNotFoundException : TerraBaseException
{
    internal TerraBaseNotFoundException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}

/// <summary>
/// 409: a deterministic conflict, such as a taken name, a non-empty delete, the last owner or a patch type conflict.
/// Documents API conflicts carry an <see cref="TerraBaseException.ErrorCode"/>; Management API 400, 403 and 409 bodies
/// are a plain message, so their <see cref="TerraBaseException.ErrorCode"/> is <see langword="null"/>.
/// </summary>
public sealed class TerraBaseConflictException : TerraBaseException
{
    internal TerraBaseConflictException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}

/// <summary>412 <c>precondition_failed</c>: an <c>If-Match</c> version or <c>If-None-Match: *</c> did not hold. Re-read before retrying.</summary>
public sealed class TerraBasePreconditionFailedException : TerraBaseException
{
    internal TerraBasePreconditionFailedException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}

/// <summary>503: TerraBase cannot serve the request now (<c>capacity</c>, or a Management catalog that needs an operator).</summary>
public sealed class TerraBaseUnavailableException : TerraBaseException
{
    internal TerraBaseUnavailableException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex)
        : base(statusCode, message, errorCode, operationIndex)
    {
    }
}
