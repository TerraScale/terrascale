namespace TerraBase.Errors;


public sealed record NotFound(string? Message = null, string Code = "not_found");

public sealed record Conflict(string Message, string Code = "conflict", HistoryGap? Gap = null);

public sealed record HistoryGap(DateTimeOffset From, DateTimeOffset To);

public sealed record Validation(string Message, string Code = "invalid_request", IReadOnlyList<ValidationDetail>? Details = null);

public sealed record ValidationDetail(string Path, string Keyword);

public sealed record PreconditionFailed(string Message, string Code = "precondition_failed");

public sealed record Unauthorized(string? Message = null, string Code = "unauthorized");

public sealed record Forbidden(string Message, string Code = "forbidden");

public sealed record QuotaExceeded(string Message, string Code = "quota_exceeded");

public sealed record Unavailable(string Message, string Code = "unavailable");

public sealed record PayloadTooLarge(string Message, string Code = "payload_too_large");

public sealed record UnsupportedMediaType(string Message, string Code = "unsupported_media_type");

public sealed record RequestTimeout(string Message, string Code = "request_timeout");

public sealed record Gone(string Message, string Code = "gone");

public sealed record OutcomeUnknown(string Message, string Region, string Machine, string Code = "write_outcome_unknown");

public sealed record Done;

public readonly union Failure(NotFound, Conflict, Validation, PreconditionFailed, Unauthorized, Forbidden, QuotaExceeded, Unavailable, PayloadTooLarge, UnsupportedMediaType, RequestTimeout, Gone, OutcomeUnknown);

public readonly union Valid<T>(T, Validation);

public readonly union Found<T>(T, NotFound);
