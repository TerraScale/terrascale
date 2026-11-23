namespace TerraScale.Authentication.Errors;

public sealed record OutcomeUnknown(string Message, string Region, string Machine, string Code = "write_outcome_unknown");

public readonly union Failure(NotFound, Conflict, Validation, PreconditionFailed, Unauthorized, Forbidden, QuotaExceeded, Unavailable, PayloadTooLarge, UnsupportedMediaType, RequestTimeout, Gone, OutcomeUnknown);

public readonly union Valid<T>(T, Validation);

public readonly union Found<T>(T, NotFound);
