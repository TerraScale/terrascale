export { createTerraBaseV1Client as createTerraBaseClient, createTerraBaseV1ClientFromEnvironment as createTerraBaseClientFromEnvironment, TerraBaseV1Client as TerraBaseClient } from "./v1/terrabase-v1.js";
export { terraScaleReleaseBlockers, TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
// Project-scoped TerraBase API: SQL over REST and WebSocket, SQL table shapes and the database catalog.
export { createTerraBaseV1Client, createTerraBaseV1ClientFromEnvironment, TerraBaseV1Client, V1Database } from "./v1/terrabase-v1.js";
export {
  TerraBaseV1Error,
  V1InvalidKeyError,
  V1KeyRevokedError,
  V1KeyExpiredError,
  V1ForbiddenScopeError,
  V1NotFoundError,
  SqlError,
  SqlErrorCode,
  V1ErrorCode,
  isTerraBaseV1Error,
} from "./v1/errors.js";
export { V1TableShape } from "./v1/table-shape.js";
/** @typedef {import("./v1/table-shape.js").V1TableRow} V1TableRow */
/** @typedef {import("./v1/table-shape.js").V1TableShapeRequest} V1TableShapeRequest */
/** @typedef {import("./v1/table-shape.js").V1TableShapeSubscribeOptions} V1TableShapeSubscribeOptions */
export { V1SqlSocket, SqlSocketClosedError } from "./v1/sql-socket.js";
export { generateId, isValidId, requireId } from "./ids.js";
/** @typedef {import("./ids.js").EntityId} EntityId */
export { ExactDecimal } from "./v1/json.js";

/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseV1ClientConfig */
/** @typedef {import("./v1/transport.js").V1TokenProvider} V1TokenProvider */
/** @typedef {import("./v1/terrabase-v1.js").V1RequestOptions} V1RequestOptions */
/** @typedef {import("./v1/json.js").JsonValue} V1JsonValue */
/** @typedef {import("./v1/json.js").JsonObject} V1JsonObject */
/** @typedef {import("./v1/json.js").JsonInput} V1JsonInput */
/** @typedef {import("./v1/json.js").JsonInputObject} V1JsonInputObject */

export { staticBaseUrlResolver } from "./v1/transport.js";
export { V1Databases } from "./v1/catalog.js";
/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseClientConfig */
/** @typedef {import("./v1/transport.js").V1EndpointContext} V1EndpointContext */
/** @typedef {import("./v1/transport.js").V1EndpointResolver} V1EndpointResolver */
/** @typedef {import("./v1/sql-execute.js").V1SqlArgument} V1SqlArgument */
/** @typedef {import("./v1/sql-execute.js").V1SqlArguments} V1SqlArguments */
/** @typedef {import("./v1/sql-execute.js").V1SqlStatement} V1SqlStatement */
/** @typedef {import("./v1/sql-execute.js").V1SqlValue} V1SqlValue */
/** @typedef {import("./v1/sql-execute.js").V1SqlColumn} V1SqlColumn */
/** @typedef {import("./v1/sql-execute.js").V1SqlExecuteResult} V1SqlExecuteResult */
/** @typedef {import("./v1/catalog.js").V1ManagedDatabase} V1ManagedDatabase */
/** @typedef {import("./v1/catalog.js").V1DatabaseCreateRequest} V1DatabaseCreateRequest */
/** @typedef {import("./v1/catalog.js").V1DatabaseBranchRequest} V1DatabaseBranchRequest */
/** @typedef {import("./v1/catalog.js").V1DatabaseListRequest} V1DatabaseListRequest */
/** @typedef {import("./v1/catalog.js").V1DatabasePage} V1DatabasePage */

export { V1ProjectUsageClient, V1DatabaseUsageClient } from "./v1/usage.js";
/** @typedef {import("./v1/usage.js").V1DatabaseUsage} V1DatabaseUsage */
/** @typedef {import("./v1/usage.js").V1ProjectUsage} V1ProjectUsage */
/** @typedef {import("./v1/usage.js").V1Analytics} V1Analytics */
/** @typedef {import("./v1/usage.js").V1UsageRequest} V1UsageRequest */
/** @typedef {import("./v1/usage.js").V1AnalyticsRequest} V1AnalyticsRequest */

export { V1Backups, V1BackupDestinationClient } from "./v1/backups.js";
/** @typedef {import("./v1/backups.js").V1Backup} V1Backup */
/** @typedef {import("./v1/backups.js").V1BackupPage} V1BackupPage */
/** @typedef {import("./v1/backups.js").V1BackupPolicy} V1BackupPolicy */
/** @typedef {import("./v1/backups.js").V1RestoreWindow} V1RestoreWindow */
/** @typedef {import("./v1/backups.js").V1PointInTimePlan} V1PointInTimePlan */
/** @typedef {import("./v1/backups.js").V1RestoredDatabase} V1RestoredDatabase */
/** @typedef {import("./v1/backups.js").V1BackupDestination} V1BackupDestination */
/** @typedef {import("./v1/backups.js").V1BackupPolicyRequest} V1BackupPolicyRequest */
/** @typedef {import("./v1/backups.js").V1PointInTimeRequest} V1PointInTimeRequest */
/** @typedef {import("./v1/backups.js").V1PointInTimeRestoreRequest} V1PointInTimeRestoreRequest */
/** @typedef {import("./v1/backups.js").V1BackupDestinationRequest} V1BackupDestinationRequest */

export { V1Functions } from "./v1/functions.js";
/** @typedef {import("./v1/functions.js").V1Function} V1Function */
/** @typedef {import("./v1/functions.js").V1FunctionLog} V1FunctionLog */
/** @typedef {import("./v1/functions.js").V1FunctionDeployRequest} V1FunctionDeployRequest */
/** @typedef {import("./v1/functions.js").V1FunctionInvokeRequest} V1FunctionInvokeRequest */
/** @typedef {import("./v1/functions.js").V1FunctionResponse} V1FunctionResponse */

export { createTerraBaseSystemClient, V1Health, V1Platform, V1Regions } from "./v1/system.js";
/** @typedef {import("./v1/system.js").TerraBaseSystemClientConfig} TerraBaseSystemClientConfig */
/** @typedef {import("./v1/system.js").V1ProbeStatus} V1ProbeStatus */
/** @typedef {import("./v1/system.js").V1PlatformDatabases} V1PlatformDatabases */

export { V1FileStorage, V1FileBucket } from "./v1/file-storage.js";
/** @typedef {import("./v1/file-storage.js").FileAccessPolicy} FileAccessPolicy */
/** @typedef {import("./v1/file-storage.js").FileAccessRules} FileAccessRules */
/** @typedef {import("./v1/file-storage.js").FileBucketInfo} FileBucketInfo */
/** @typedef {import("./v1/file-storage.js").StoredFile} StoredFile */
/** @typedef {import("./v1/file-storage.js").SignedFileUrl} SignedFileUrl */
/** @typedef {import("./v1/file-storage.js").FileUploadRequest} FileUploadRequest */
/** @typedef {import("./v1/file-storage.js").FileUploadOptions} FileUploadOptions */
/** @typedef {import("./v1/file-storage.js").FileListOptions} FileListOptions */

/** @typedef {import("./v1/terrabase-v1.js").V1AgentDatabaseOptions} V1AgentDatabaseOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1CreateDatabaseOptions} V1CreateDatabaseOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1DatabasePlacement} V1DatabasePlacement */
/** @typedef {import("./v1/terrabase-v1.js").V1ForkDatabaseOptions} V1ForkDatabaseOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1ScopedKeyRequest} V1ScopedKeyRequest */
/** @typedef {import("./v1/terrabase-v1.js").V1ScopedKey} V1ScopedKey */
