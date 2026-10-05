namespace TerraScale.TerraBase.Errors;

// Expected failures are values, not exceptions (docs/error-handling.md). A feature returns a union of its
// success types and the failures below that it can produce; callers handle every case in a switch, which
// the compiler checks is exhaustive. Messages describe the rule that failed and never echo client input.

/// <summary>The addressed resource, or one of its parents, does not exist or is hidden from the caller.</summary>
/// <param name="Message">What is missing, when saying so reveals nothing.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record NotFound(string? Message = null, string Code = "not_found");

/// <summary>The request conflicts with the current state, such as a name that is already taken.</summary>
/// <param name="Message">The rule that failed.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record Conflict(string Message, string Code = "conflict");

/// <summary>The request is malformed or breaks an input rule.</summary>
/// <param name="Message">The rule that failed.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record Validation(string Message, string Code = "invalid_request");

/// <summary>The caller is not authenticated.</summary>
/// <param name="Message">The rule that failed.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record Unauthorized(string? Message = null, string Code = "unauthorized");

/// <summary>The caller is authenticated but may not do this.</summary>
/// <param name="Message">The rule that failed.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record Forbidden(string Message, string Code = "forbidden");

/// <summary>A limit or capacity is exhausted; the caller may retry later or within its quota.</summary>
/// <param name="Message">The limit that was hit.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record QuotaExceeded(string Message, string Code = "quota_exceeded");

/// <summary>The service cannot serve the request now, for example until an operator fixes its configuration.</summary>
/// <param name="Message">What is unavailable.</param>
/// <param name="Code">Stable machine-readable code.</param>
public sealed record Unavailable(string Message, string Code = "unavailable");

/// <summary>Success without a value, such as a deletion.</summary>
public sealed record Done;

/// <summary>Every expected failure; <see cref="ErrorResponse.From"/> maps each to its HTTP response.</summary>
public readonly union Failure(NotFound, Conflict, Validation, Unauthorized, Forbidden, QuotaExceeded, Unavailable);

/// <summary>A value read from a request, or the input rule it breaks.</summary>
/// <typeparam name="T">The value type.</typeparam>
public readonly union Valid<T>(T, Validation);

/// <summary>A resource, or that it does not exist.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public readonly union Found<T>(T, NotFound);
