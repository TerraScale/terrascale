namespace TerraScale.DotNet.Errors;


/// <summary>The requested resource does not exist.</summary>
public sealed record NotFound(string? Message = null, string Code = "not_found");

/// <summary>The change conflicts with the current state of the resource.</summary>
public sealed record Conflict(string Message, string Code = "conflict", HistoryGap? Gap = null);

/// <summary>The conflict's unavailable history interval.</summary>
public sealed record HistoryGap(DateTimeOffset? From, DateTimeOffset? To);

/// <summary>The request failed validation.</summary>
public sealed record Validation(string Message, string Code = "invalid_request", IReadOnlyList<ValidationDetail>? Details = null);

/// <summary>One validation failure at a request path.</summary>
public sealed record ValidationDetail(string Path, string Keyword);

/// <summary>A request precondition, such as an expected state, was not met.</summary>
public sealed record PreconditionFailed(string Message, string Code = "precondition_failed");

/// <summary>The request lacks valid authentication.</summary>
public sealed record Unauthorized(string? Message = null, string Code = "unauthorized");

/// <summary>The caller is authenticated but not allowed this operation.</summary>
public sealed record Forbidden(string Message, string Code = "forbidden", IReadOnlyDictionary<string, string>? Details = null);

/// <summary>The caller's quota or rate limit is exhausted.</summary>
public sealed record QuotaExceeded(string Message, string Code = "quota_exceeded");

/// <summary>The service is temporarily unavailable; retrying may succeed.</summary>
public sealed record Unavailable(string Message, string Code = "unavailable", IReadOnlyDictionary<string, string>? Details = null);

/// <summary>The request body exceeds the accepted size.</summary>
public sealed record PayloadTooLarge(string Message, string Code = "payload_too_large");

/// <summary>The request's content type is not supported.</summary>
public sealed record UnsupportedMediaType(string Message, string Code = "unsupported_media_type");

/// <summary>The server gave up waiting for the request.</summary>
public sealed record RequestTimeout(string Message, string Code = "request_timeout");

/// <summary>The resource is gone and will not come back.</summary>
public sealed record Gone(string Message, string Code = "gone");

/// <summary>A write was sent but its outcome is unknown; it may or may not have been applied.</summary>
public sealed record OutcomeUnknown(string Message, string Region, string Machine, string Code = "write_outcome_unknown");

/// <summary>A successful operation that returns no data.</summary>
public sealed record Done;

/// <summary>Every expected failure case of an operation.</summary>
public readonly union Failure(NotFound, Conflict, Validation, PreconditionFailed, Unauthorized, Forbidden, QuotaExceeded, Unavailable, PayloadTooLarge, UnsupportedMediaType, RequestTimeout, Gone, OutcomeUnknown);

/// <summary>Either a validated value or a validation failure.</summary>
public readonly union Valid<T>(T, Validation);

/// <summary>Either a found value or not found.</summary>
public readonly union Found<T>(T, NotFound);
