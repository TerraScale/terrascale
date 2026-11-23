namespace TerraScale.Results;

/// <summary>The requested resource was not found.</summary>
public sealed record NotFound(string? Message = null, string Code = "not_found");

/// <summary>The operation conflicted with the current state.</summary>
public sealed record Conflict(string Message, string Code = "conflict");

/// <summary>One field-level validation violation.</summary>
public sealed record ValidationDetail(string Path, string Keyword);

/// <summary>The input failed validation.</summary>
public sealed record Validation(string Message, string Code = "invalid_request", IReadOnlyList<ValidationDetail>? Details = null);

/// <summary>A required precondition failed.</summary>
public sealed record PreconditionFailed(string Message, string Code = "precondition_failed");

/// <summary>The caller is not authenticated.</summary>
public sealed record Unauthorized(string? Message = null, string Code = "unauthorized");

/// <summary>The authenticated caller lacks permission.</summary>
public sealed record Forbidden(string Message, string Code = "forbidden", IReadOnlyDictionary<string, string>? Details = null);

/// <summary>A plan or usage quota was exceeded.</summary>
public sealed record QuotaExceeded(string Message, string Code = "quota_exceeded");

/// <summary>The caller was rate-limited.</summary>
public sealed record RateLimited(string Message, TimeSpan? RetryAfter = null, string Code = "rate_limited");

/// <summary>A required service or dependency is temporarily unavailable.</summary>
public sealed record Unavailable(string Message, string Code = "unavailable", IReadOnlyDictionary<string, string>? Details = null);

/// <summary>The request payload exceeded the allowed size.</summary>
public sealed record PayloadTooLarge(string Message, string Code = "payload_too_large");

/// <summary>The request media type is not supported.</summary>
public sealed record UnsupportedMediaType(string Message, string Code = "unsupported_media_type");

/// <summary>The operation timed out.</summary>
public sealed record RequestTimeout(string Message, string Code = "request_timeout");

/// <summary>The requested resource is permanently gone.</summary>
public sealed record Gone(string Message, string Code = "gone");
