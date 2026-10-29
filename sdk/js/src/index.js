export { createTerraBaseV1Client as createTerraBaseClient, TerraBaseV1Client as TerraBaseClient } from "./v1/terrabase-v1.js";
export { terraScaleReleaseBlockers, TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
// Project-scoped TerraBase API, including current free collection shape reads.
export {
  createTerraBaseV1Client,
  TerraBaseV1Client,
  V1Database,
  V1Collection,
  V1Collections,
  V1Transaction,
} from "./v1/terrabase-v1.js";
export {
  TerraBaseV1Error,
  V1PreconditionFailedError,
  V1InvalidKeyError,
  V1KeyRevokedError,
  V1KeyExpiredError,
  V1ForbiddenScopeError,
  V1NotFoundError,
  SqlError,
  V1ErrorCode,
  isTerraBaseV1Error,
} from "./v1/errors.js";
export { v1Filter } from "./v1/filter.js";
export { V1ShapeSubscription } from "./v1/shape-subscription.js";
export { indexedDbShapeReplica } from "./v1/shape-replica.js";
/** @typedef {import("./v1/shape-subscription.js").V1ShapeReplicaStore} V1ShapeReplicaStore */
/** @typedef {import("./v1/shape-subscription.js").V1ShapeSubscribeOptions} V1ShapeSubscribeOptions */
/** @typedef {import("./v1/shape-subscription.js").V1ShapeDocument} V1ShapeDocument */
/** @typedef {import("./v1/shape-reader.js").V1SealedShapePosition} V1SealedShapePosition */
export { generateId, isValidId, requireId } from "./ids.js";
/** @typedef {import("./ids.js").EntityId} EntityId */
export { ExactDecimal } from "./v1/json.js";

/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseV1ClientConfig */
/** @typedef {import("./v1/transport.js").V1TokenProvider} V1TokenProvider */
/** @typedef {import("./v1/terrabase-v1.js").V1RequestOptions} V1RequestOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1ReadOptions} V1ReadOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1WriteOptions} V1WriteOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1CreateOptions} V1CreateOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1ConditionalWriteOptions} V1ConditionalWriteOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1PatchOp} V1PatchOp */
/** @typedef {import("./v1/terrabase-v1.js").V1Patch} V1Patch */
/** @typedef {import("./v1/terrabase-v1.js").V1SortKey} V1SortKey */
/** @typedef {import("./v1/terrabase-v1.js").V1NearSortKey} V1NearSortKey */
/** @typedef {import("./v1/terrabase-v1.js").V1GeoNearSortKey} V1GeoNearSortKey */
/** @typedef {import("./v1/terrabase-v1.js").V1Aggregate} V1Aggregate */
/** @typedef {import("./v1/terrabase-v1.js").V1Group} V1Group */
/** @typedef {import("./v1/terrabase-v1.js").V1ScoreSortKey} V1ScoreSortKey */
/** @typedef {import("./v1/terrabase-v1.js").V1Query} V1Query */
/** @typedef {import("./v1/terrabase-v1.js").V1Count} V1Count */
/** @typedef {import("./v1/terrabase-v1.js").V1QueryPage} V1QueryPage */
/** @typedef {import("./v1/terrabase-v1.js").V1Precondition} V1Precondition */
/** @typedef {import("./v1/terrabase-v1.js").V1TransactionOpResult} V1TransactionOpResult */
/** @typedef {import("./v1/terrabase-v1.js").V1CountBounds} V1CountBounds */
/** @typedef {import("./v1/terrabase-v1.js").V1TransactionResult} V1TransactionResult */
/** @typedef {import("./v1/decode.js").V1Document} V1Document */
/** @typedef {import("./v1/decode.js").V1WriteResult} V1WriteResult */
/** @typedef {import("./v1/decode.js").V1Durability} V1Durability */
/** @typedef {import("./v1/filter.js").V1Filter} V1Filter */
/** @typedef {import("./v1/filter.js").V1Path} V1Path */
/** @typedef {import("./v1/filter.js").V1Scalar} V1Scalar */
/** @typedef {import("./v1/filter.js").V1GeoCircle} V1GeoCircle */
/** @typedef {import("./v1/filter.js").V1GeoBox} V1GeoBox */
/** @typedef {import("./v1/json.js").JsonValue} V1JsonValue */
/** @typedef {import("./v1/json.js").JsonObject} V1JsonObject */
/** @typedef {import("./v1/json.js").JsonInput} V1JsonInput */
/** @typedef {import("./v1/json.js").JsonInputObject} V1JsonInputObject */
/** @typedef {import("./v1/shapes.js").V1ShapeRequest} V1ShapeRequest */
/** @typedef {import("./v1/shapes.js").V1ShapeChange} V1ShapeChange */
/** @typedef {import("./v1/shapes.js").V1ShapeOp} V1ShapeOp */

export { staticBaseUrlResolver } from "./v1/transport.js";
export { V1Databases } from "./v1/catalog.js";
export { V1SavedQueries } from "./v1/saved-queries.js";
/** @typedef {import("./v1/saved-queries.js").V1SavedQuery} V1SavedQuery */
/** @typedef {import("./v1/saved-queries.js").V1SavedQueryRequest} V1SavedQueryRequest */
/** @typedef {import("./v1/saved-queries.js").V1SavedQueryPage} V1SavedQueryPage */
/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseClientConfig */
/** @typedef {import("./v1/transport.js").V1EndpointContext} V1EndpointContext */
/** @typedef {import("./v1/transport.js").V1EndpointResolver} V1EndpointResolver */
/** @typedef {import("./v1/terrabase-v1.js").V1CollectionInfo} V1CollectionInfo */
/** @typedef {import("./v1/terrabase-v1.js").V1CollectionPage} V1CollectionPage */
/** @typedef {import("./v1/terrabase-v1.js").V1CollectionListOptions} V1CollectionListOptions */
/** @typedef {import("./v1/shapes.js").V1ShapePage} V1ShapePage */
/** @typedef {import("./v1/sql.js").V1SqlRow} V1SqlRow */
/** @typedef {import("./v1/sql.js").V1SqlResult} V1SqlResult */
/** @typedef {import("./v1/sql.js").V1SqlIndexSuggestion} V1SqlIndexSuggestion */
/** @typedef {import("./v1/sql.js").V1SqlCheck} V1SqlCheck */
/** @typedef {import("./v1/sql-execute.js").V1SqlArgument} V1SqlArgument */
/** @typedef {import("./v1/sql-execute.js").V1SqlArguments} V1SqlArguments */
/** @typedef {import("./v1/sql-execute.js").V1SqlStatement} V1SqlStatement */
/** @typedef {import("./v1/sql-execute.js").V1SqlValue} V1SqlValue */
/** @typedef {import("./v1/sql-execute.js").V1SqlColumn} V1SqlColumn */
/** @typedef {import("./v1/sql-execute.js").V1SqlExecuteResult} V1SqlExecuteResult */
/** @typedef {import("./v1/changes.js").V1ChangeStreamRequest} V1ChangeStreamRequest */
/** @typedef {import("./v1/changes.js").V1DatabaseChange} V1DatabaseChange */
/** @typedef {import("./v1/changes.js").V1DatabaseChangeCursor} V1DatabaseChangeCursor */
/** @typedef {import("./v1/export.js").V1ExportOptions} V1ExportOptions */
/** @typedef {import("./v1/export.js").V1ExportedDocument} V1ExportedDocument */
/** @typedef {import("./v1/catalog.js").V1ManagedDatabase} V1ManagedDatabase */
/** @typedef {import("./v1/catalog.js").V1DatabaseCreateRequest} V1DatabaseCreateRequest */
/** @typedef {import("./v1/catalog.js").V1DatabaseUpdateRequest} V1DatabaseUpdateRequest */
/** @typedef {import("./v1/catalog.js").V1DatabaseBranchRequest} V1DatabaseBranchRequest */
/** @typedef {import("./v1/catalog.js").V1DatabaseListRequest} V1DatabaseListRequest */
/** @typedef {import("./v1/catalog.js").V1DatabasePage} V1DatabasePage */
/** @typedef {import("./v1/catalog.js").V1CollectionSchema} V1CollectionSchema */
/** @typedef {import("./v1/catalog.js").V1SchemaPreview} V1SchemaPreview */
/** @typedef {import("./v1/catalog.js").V1CollectionTtl} V1CollectionTtl */

export { V1DatabaseSchemaClient } from "./v1/database-schema.js";
/** @typedef {import("./v1/database-schema.js").V1InferredSchema} V1InferredSchema */
/** @typedef {import("./v1/database-schema.js").V1DatabaseSchema} V1DatabaseSchema */
/** @typedef {import("./v1/database-schema.js").V1UniqueSchema} V1UniqueSchema */
/** @typedef {import("./v1/database-schema.js").V1UniqueSchemaRequest} V1UniqueSchemaRequest */
/** @typedef {import("./v1/database-schema.js").V1QueryShapes} V1QueryShapes */

export { V1Indexes } from "./v1/indexes.js";
/** @typedef {import("./v1/indexes.js").V1Index} V1Index */
/** @typedef {import("./v1/indexes.js").V1IndexKind} V1IndexKind */
/** @typedef {import("./v1/indexes.js").V1IndexList} V1IndexList */
/** @typedef {import("./v1/indexes.js").V1IndexRecommendations} V1IndexRecommendations */
/** @typedef {import("./v1/indexes.js").V1AppliedIndexRecommendation} V1AppliedIndexRecommendation */
/** @typedef {import("./v1/indexes.js").V1CreateIndexRequest} V1CreateIndexRequest */

export { V1ProjectUsageClient, V1DatabaseUsageClient } from "./v1/usage.js";
/** @typedef {import("./v1/usage.js").V1DatabaseUsage} V1DatabaseUsage */
/** @typedef {import("./v1/usage.js").V1ProjectUsage} V1ProjectUsage */
/** @typedef {import("./v1/usage.js").V1Analytics} V1Analytics */
/** @typedef {import("./v1/usage.js").V1Replication} V1Replication */
/** @typedef {import("./v1/usage.js").V1UsageRequest} V1UsageRequest */
/** @typedef {import("./v1/usage.js").V1AnalyticsRequest} V1AnalyticsRequest */

export { V1Backups, V1BackupDestinationClient } from "./v1/backups.js";
export { V1ChangeWebhooks } from "./v1/change-webhooks.js";
/** @typedef {import("./v1/change-webhooks.js").V1ChangeWebhook} V1ChangeWebhook */
/** @typedef {import("./v1/change-webhooks.js").V1CreatedChangeWebhook} V1CreatedChangeWebhook */
/** @typedef {import("./v1/change-webhooks.js").V1ChangeWebhookDelivery} V1ChangeWebhookDelivery */
/** @typedef {import("./v1/change-webhooks.js").V1ChangeWebhookListRequest} V1ChangeWebhookListRequest */
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

export { V1Imports } from "./v1/imports.js";
/** @typedef {import("./v1/imports.js").V1ImportJob} V1ImportJob */
/** @typedef {import("./v1/imports.js").V1ImportOptions} V1ImportOptions */
/** @typedef {import("./v1/imports.js").V1CreateImportRequest} V1CreateImportRequest */

export { createTerraBaseSystemClient, V1Health, V1Platform } from "./v1/system.js";
/** @typedef {import("./v1/system.js").TerraBaseSystemClientConfig} TerraBaseSystemClientConfig */
/** @typedef {import("./v1/system.js").V1ProbeStatus} V1ProbeStatus */
/** @typedef {import("./v1/system.js").V1ReplicationHealth} V1ReplicationHealth */
/** @typedef {import("./v1/system.js").V1PlatformDatabases} V1PlatformDatabases */
/** @typedef {import("./v1/shape-manifest.js").V1ShapeManifest} V1ShapeManifest */
/** @typedef {import("./v1/shape-manifest.js").V1ShapeManifestRequest} V1ShapeManifestRequest */
/** @typedef {import("./v1/shape-reader.js").V1ShapeKeyRequest} V1ShapeKeyRequest */
/** @typedef {import("./v1/shape-reader.js").V1ShapeKey} V1ShapeKey */

export { V1FileStorage, V1FileBucket } from "./v1/file-storage.js";
/** @typedef {import("./v1/file-storage.js").FileAccessPolicy} FileAccessPolicy */
/** @typedef {import("./v1/file-storage.js").FileAccessRules} FileAccessRules */
/** @typedef {import("./v1/file-storage.js").FileBucketInfo} FileBucketInfo */
/** @typedef {import("./v1/file-storage.js").StoredFile} StoredFile */
/** @typedef {import("./v1/file-storage.js").SignedFileUrl} SignedFileUrl */
/** @typedef {import("./v1/file-storage.js").FileUploadRequest} FileUploadRequest */
/** @typedef {import("./v1/file-storage.js").FileUploadOptions} FileUploadOptions */
/** @typedef {import("./v1/file-storage.js").FileListOptions} FileListOptions */
