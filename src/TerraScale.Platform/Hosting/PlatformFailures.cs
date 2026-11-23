namespace TerraScale.Platform.Hosting;

public sealed record PlatformInvalidRequest(string Message, string Code = "invalid_request");

public sealed record PlatformUnauthorized(string? Message = null, string Code = "unauthorized");

public sealed record PlatformForbidden(
    string Message,
    string Code = "forbidden",
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record PlatformNotFound(string? Message = null, string Code = "not_found");

public sealed record PlatformConflict(string Message, string Code = "conflict");

public sealed record PlatformPayloadTooLarge(string Message, string Code = "payload_too_large");

public sealed record PlatformQuotaExceeded(string Message, string Code = "quota_exceeded");

public sealed record PlatformUnavailable(
    string Message,
    string Code = "unavailable",
    IReadOnlyDictionary<string, string>? Details = null);
