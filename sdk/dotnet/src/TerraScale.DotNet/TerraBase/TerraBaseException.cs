using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

public class TerraBaseException : HttpRequestException
{
    private const int MaxErrorBytes = 64 * 1024;
    private const int MaxMessageLength = 1024;

    internal TerraBaseException(HttpStatusCode? statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(message, inner, statusCode)
    {
        ErrorCode = errorCode;
        OperationIndex = operationIndex;
    }

    public string? ErrorCode { get; }

    public int? OperationIndex { get; }

    public string? Region { get; internal set; }

    public string? RequestId { get; private set; }

    public TimeSpan? RetryAfter { get; private set; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ResponseHeaders { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

    public string? Machine { get; private set; }

    public string? OutcomeRegion { get; private set; }

    public IReadOnlyList<TerraBaseValidationDetail> Details { get; private set; } = [];

    public IReadOnlyList<TerraBaseRulesProblem> Problems { get; private set; } = [];

    internal static async Task<TerraBaseException> FromResponseAsync(HttpResponseMessage response, Exception? inner, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        var content = response.Content;
        string? code = null;
        string? message = null;
        int? op = null, statement = null;
        string? sqlstate = null;
        string? machine = null, outcomeRegion = null;
        var details = new List<TerraBaseValidationDetail>();
        IReadOnlyList<TerraBaseRulesProblem> problems = [];
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
                    machine = error.TryGetProperty("machine", out var host) && host.ValueKind == JsonValueKind.String ? Clean(host.GetString()) : null;
                    outcomeRegion = error.TryGetProperty("region", out var origin) && origin.ValueKind == JsonValueKind.String && origin.GetString() is { } named && TerraBaseRegion.IsValid(named) ? named : null;
                    if (error.TryGetProperty("details", out var issues) && issues.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var issue in issues.EnumerateArray().Take(256))
                        {
                            if (issue.ValueKind == JsonValueKind.Object && issue.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { } pointer
                                && issue.TryGetProperty("keyword", out var keyword) && keyword.ValueKind == JsonValueKind.String && keyword.GetString() is { } rule)
                                details.Add(new(pointer, rule));
                        }
                    }
                    if (code == "invalid_rules" && error.TryGetProperty("details", out var ruleDetails)
                        && ruleDetails.ValueKind == JsonValueKind.Object && ruleDetails.TryGetProperty("problems", out var diagnostics))
                        problems = diagnostics.Deserialize(TerraBaseRulesJson.Default.IReadOnlyListTerraBaseRulesProblem)
                            ?? throw new JsonException("Rules problems must be an array.");
                    statement = error.TryGetProperty("statement", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var index0) ? index0 : null;
                    sqlstate = (error.TryGetProperty("sqlState", out var sc) || error.TryGetProperty("sqlstate", out sc)) && sc.ValueKind == JsonValueKind.String
                        ? Clean(sc.GetString())
                        : null;
                    op = error.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var index) ? index : null;
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or HttpRequestException)
        {
        }

        var text = Clean(message) ?? $"TerraBase answered HTTP {(int)status}{(code is null ? null : $" ({Clean(code)})")}.";
        TerraBaseException exception = (int)status switch
        {
            _ when code is not null && statement is { } failed && sqlstate is not null => new TerraBaseSqlException(status, text, code, failed, sqlstate, inner),
            _ when code is { } consistencyCode && TerraBaseSqlErrorCodes.ConsistencySqlState(consistencyCode) is { } consistencyState =>
                new TerraBaseSqlException(status, text, consistencyCode, statement ?? 0, sqlstate ?? consistencyState, inner),
            400 or 413 or 415 => new TerraBaseValidationException(status, text, code, op, inner),
            401 when code == "key_revoked" => new TerraBaseKeyRevokedException(status, text, code, op, inner),
            401 when code == "key_expired" => new TerraBaseKeyExpiredException(status, text, code, op, inner),
            401 => new TerraBaseAuthenticationException(status, text, code, op, inner),
            403 => new TerraBaseForbiddenException(status, text, code, op, inner),
            404 => new TerraBaseNotFoundException(status, text, code, op, inner),
            409 => new TerraBaseConflictException(status, text, code, op, inner),
            410 when code == "cursor_expired" => new TerraBaseCursorExpiredException(status, text, code, op, inner),
            429 => new TerraBaseRateLimitedException(status, text, code, op, inner),
            428 => new TerraBasePreconditionRequiredException(status, text, code, op, inner),
            412 => new TerraBasePreconditionFailedException(status, text, code, op, inner),
            503 => new TerraBaseUnavailableException(status, text, code, op, inner),
            _ => new TerraBaseException(status, text, code, op, inner),
        };
        exception.Machine = machine;
        exception.OutcomeRegion = outcomeRegion;
        exception.Details = details.AsReadOnly();
        exception.Problems = problems;
        exception.CaptureResponse(response);
        return exception;
    }

    internal void CaptureResponse(HttpResponseMessage response)
    {
        RequestId = ResponseRequestId.Read(response);
        ResponseHeaders = response.Headers.ToDictionary(header => header.Key,
            header => (IReadOnlyList<string>)Array.AsReadOnly(header.Value.ToArray()), StringComparer.OrdinalIgnoreCase).AsReadOnly();
        Region = RegionHeader(response, "X-TerraBase-Region");
        RetryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } retryAt ? TimeSpan.FromTicks(Math.Max(0, (retryAt - DateTimeOffset.UtcNow).Ticks)) : null);
    }

    private static string? RegionHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.ToArray() is [var value]
            && TerraBaseRegion.IsValid(value) ? value : null;

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

public sealed class TerraBaseValidationException : TerraBaseException
{
    internal TerraBaseValidationException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public class TerraBaseAuthenticationException : TerraBaseException
{
    internal TerraBaseAuthenticationException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public sealed class TerraBaseKeyRevokedException : TerraBaseAuthenticationException
{
    internal TerraBaseKeyRevokedException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner) { }
}

public sealed class TerraBaseKeyExpiredException : TerraBaseAuthenticationException
{
    internal TerraBaseKeyExpiredException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner) { }
}

public sealed class TerraBaseForbiddenException : TerraBaseException
{
    internal TerraBaseForbiddenException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public sealed class TerraBaseNotFoundException : TerraBaseException
{
    internal TerraBaseNotFoundException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public sealed class TerraBaseConflictException : TerraBaseException
{
    internal TerraBaseConflictException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public sealed class TerraBasePreconditionFailedException : TerraBaseException
{
    internal TerraBasePreconditionFailedException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public sealed class TerraBaseUnavailableException : TerraBaseException
{
    internal TerraBaseUnavailableException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner)
    {
    }
}

public sealed class TerraBaseCursorExpiredException : TerraBaseException
{
    internal TerraBaseCursorExpiredException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner) { }
}

public sealed class TerraBaseRateLimitedException : TerraBaseException
{
    internal TerraBaseRateLimitedException(HttpStatusCode statusCode, string message, string? errorCode, int? operationIndex, Exception? inner = null)
        : base(statusCode, message, errorCode, operationIndex, inner) { }
}

public sealed record TerraBaseValidationDetail(string Path, string Keyword);
