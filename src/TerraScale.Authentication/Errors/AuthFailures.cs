namespace TerraScale.Authentication.Errors;

public sealed record AuthNotFound(string? Message = null, string Code = "not_found");

public sealed record AuthConflict(string Message, string Code = "conflict");

public sealed record AuthValidationDetail(string Path, string Keyword);

public sealed record AuthValidation(string Message, string Code = "invalid_request", IReadOnlyList<AuthValidationDetail>? Details = null);

public sealed record AuthPreconditionFailed(string Message, string Code = "precondition_failed");

public sealed record AuthUnauthorized(string? Message = null, string Code = "unauthorized");

public sealed record AuthForbidden(string Message, string Code = "forbidden", IReadOnlyDictionary<string, string>? Details = null);

public sealed record AuthQuotaExceeded(string Message, string Code = "quota_exceeded");

public sealed record AuthUnavailable(string Message, string Code = "unavailable", IReadOnlyDictionary<string, string>? Details = null);

public sealed record AuthPayloadTooLarge(string Message, string Code = "payload_too_large");

public sealed record AuthUnsupportedMediaType(string Message, string Code = "unsupported_media_type");

public sealed record AuthRequestTimeout(string Message, string Code = "request_timeout");

public sealed record AuthGone(string Message, string Code = "gone");

public sealed record AuthCompleted;
