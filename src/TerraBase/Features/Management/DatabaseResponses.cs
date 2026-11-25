using TerraBase.Features.Backup;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

public readonly union CreateDatabaseResponse(Created<DatabaseBody>, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

public readonly union EnsureDatabaseResponse(DatabaseBody, Created<DatabaseBody>, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

public readonly union BranchDatabaseResponse(Created<DatabaseBody>, Accepted<RestoreOperationBody>, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable, DatabaseIdMismatch);

public readonly union UpdateDatabaseResponse(DatabaseBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

public readonly union DeleteDatabaseResponse(Completed, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, DatabaseIdMismatch, BackendUnavailable);
