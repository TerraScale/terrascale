export { createTerraBaseClient, TerraBaseClient } from "./database.js";
export type {
  TerraBaseClientConfig,
  PreparedDatabaseCommit,
  DatabaseRequestOptions,
} from "./database.js";
export type * from "./database-types.js";
export {
  terraBaseDiscoveryOrigin,
  parseTerraBaseRoutingDocument,
  TerraBaseDiscoveryError,
  TerraBaseDiscoveryRouter,
} from "./discovery.js";
export type {
  TerraBaseRoutingCapability,
  TerraBaseRoutingCapabilities,
  TerraBaseRoutingEndpoint,
  TerraBaseRoutingDocument,
} from "./discovery.js";
export * from "./schema.js";
export * from "./sql.js";
export {
  terraScaleReleaseBlockers,
  TerraScaleCapabilityUnavailableError,
} from "./management-contracts.js";
export type { TerraScaleBlockedCapability } from "./management-contracts.js";
export type {
  TerraScaleResult,
  TerraScaleError,
  TerraScaleFailure,
  TerraScaleSuccess,
} from "./result.js";
export {
  createDatabaseQuery,
  DatabaseSubscriptionView,
  DatabaseSubscriptionError,
} from "./database-view.js";
export type {
  DatabaseQueryOptions,
  DatabaseViewBinding,
  DatabaseViewPosition,
  DatabaseViewSnapshot,
  DatabaseQueryCallbacks,
  DatabaseQuerySource,
} from "./database-view.js";
