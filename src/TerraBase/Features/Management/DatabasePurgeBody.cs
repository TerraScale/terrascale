using TerraBase.Errors;

namespace TerraBase.Features.Management;

public sealed record DatabasePurgeBody(string Id, string State, IReadOnlyList<string> Outstanding);

public readonly union PurgeDatabaseResult(DatabasePurgeBody, NotFound, Forbidden, Conflict, Unavailable);
