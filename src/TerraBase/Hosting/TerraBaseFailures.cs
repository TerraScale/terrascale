namespace TerraBase.Hosting;

public sealed record Completed;

public sealed record InvalidInput(string Message, string Code = "invalid_request");

public sealed record CredentialRejected(string Message, string Code = "unauthorized");

public sealed record CallerForbidden(string Message, string Code = "forbidden", IReadOnlyDictionary<string, string>? Details = null);

public sealed record ResourceNotFound(string Message, string Code = "not_found");

public sealed record StateConflict(string Message, string Code = "conflict");

public sealed record DatabaseIdMismatch(string Message = "the database's id is not the expected one", string Code = "database_id_mismatch");

public sealed record BackendUnavailable(string Message, string Code = "unavailable");
