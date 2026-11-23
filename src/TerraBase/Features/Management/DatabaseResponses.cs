using TerraBase.Features.Backup;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

public readonly union CreateDatabaseResponse(Created<DatabaseBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable);

public readonly union EnsureDatabaseResponse(DatabaseBody, Created<DatabaseBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable);

public readonly union BranchDatabaseResponse(Created<DatabaseBody>, Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, QuotaExceeded, Unavailable, PreconditionFailed);

public readonly union UpdateDatabaseResponse(DatabaseBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable);

public readonly union DeleteDatabaseResponse(Done, Validation, Unauthorized, Forbidden, NotFound, PreconditionFailed, Unavailable);
