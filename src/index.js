export { createTerraBaseClient, TerraBaseClient } from "./database.js";
export {
  terraBaseDiscoveryOrigin,
  parseTerraBaseRoutingDocument,
  TerraBaseDiscoveryError,
  TerraBaseDiscoveryRouter,
} from "./discovery.js";
export * from "./schema.js";
export * from "./sql.js";
export {
  terraScaleReleaseBlockers,
  TerraScaleCapabilityUnavailableError,
} from "./management-contracts.js";
export {
  createDatabaseQuery,
  DatabaseSubscriptionView,
  DatabaseSubscriptionError,
} from "./database-view.js";

/** @typedef {import("./database.js").TerraBaseClientConfig} TerraBaseClientConfig */
/** @typedef {import("./database.js").PreparedDatabaseCommit} PreparedDatabaseCommit */
/** @typedef {import("./database.js").DatabaseRequestOptions} DatabaseRequestOptions */

/** @typedef {import("./database-types.js").CanonicalUuid} CanonicalUuid */
/** @typedef {import("./database-types.js").DatabaseScope} DatabaseScope */
/** @typedef {import("./database-types.js").CollectionBinding} CollectionBinding */
/** @typedef {import("./database-types.js").DatabaseValue} DatabaseValue */
/** @typedef {import("./database-types.js").DatabaseField} DatabaseField */
/** @typedef {import("./database-types.js").DatabaseObserved} DatabaseObserved */
/** @typedef {import("./database-types.js").DatabaseEvent} DatabaseEvent */
/** @typedef {import("./database-types.js").DatabaseAuthority} DatabaseAuthority */
/** @typedef {import("./database-types.js").DatabaseCondition} DatabaseCondition */
/** @typedef {import("./database-types.js").DatabaseWriteValue} DatabaseWriteValue */
/** @typedef {import("./database-types.js").DatabaseWrite} DatabaseWrite */
/** @typedef {import("./database-types.js").DatabaseTransactionId} DatabaseTransactionId */
/** @typedef {import("./database-types.js").DatabaseCommit} DatabaseCommit */
/** @typedef {import("./database-types.js").DatabaseSibling} DatabaseSibling */
/** @typedef {import("./database-types.js").DatabasePoint} DatabasePoint */
/** @typedef {import("./database-types.js").DatabaseQueryValue} DatabaseQueryValue */
/** @typedef {import("./database-types.js").DatabaseQueryPredicate} DatabaseQueryPredicate */
/** @typedef {import("./database-types.js").DatabaseQuery} DatabaseQuery */
/** @typedef {import("./database-types.js").DatabaseQueryRow} DatabaseQueryRow */
/** @typedef {import("./database-types.js").DatabaseQueryPage} DatabaseQueryPage */
/** @typedef {import("./database-types.js").DatabaseCommitReceipt} DatabaseCommitReceipt */
/** @typedef {import("./database-types.js").DatabaseTransactionIdentity} DatabaseTransactionIdentity */
/** @typedef {import("./database-types.js").DatabaseTransactionStatus} DatabaseTransactionStatus */
/**
 * @template T
 * @typedef {import("./database-types.js").DatabaseResult<T>} DatabaseResult
 */
/** @typedef {import("./database-types.js").DatabaseSubscriptionRequest} DatabaseSubscriptionRequest */
/** @typedef {import("./database-types.js").DatabaseSubscriptionEffect} DatabaseSubscriptionEffect */
/** @typedef {import("./database-types.js").DatabaseSubscriptionGroup} DatabaseSubscriptionGroup */
/** @typedef {import("./database-types.js").DatabaseSubscriptionResponse} DatabaseSubscriptionResponse */

/** @typedef {import("./discovery.js").TerraBaseRoutingCapability} TerraBaseRoutingCapability */
/** @typedef {import("./discovery.js").TerraBaseRoutingCapabilities} TerraBaseRoutingCapabilities */
/** @typedef {import("./discovery.js").TerraBaseRoutingEndpoint} TerraBaseRoutingEndpoint */
/** @typedef {import("./discovery.js").TerraBaseRoutingDocument} TerraBaseRoutingDocument */

/** @typedef {import("./management-contracts.js").TerraScaleBlockedCapability} TerraScaleBlockedCapability */

/**
 * @template TValue
 * @typedef {import("./result.js").TerraScaleResult<TValue>} TerraScaleResult
 */
/** @typedef {import("./result.js").TerraScaleError} TerraScaleError */
/** @typedef {import("./result.js").TerraScaleFailure} TerraScaleFailure */
/**
 * @template TValue
 * @typedef {import("./result.js").TerraScaleSuccess<TValue>} TerraScaleSuccess
 */

/** @typedef {import("./database-view.js").DatabaseQueryOptions} DatabaseQueryOptions */
/** @typedef {import("./database-view.js").DatabaseViewBinding} DatabaseViewBinding */
/** @typedef {import("./database-view.js").DatabaseViewPosition} DatabaseViewPosition */
/** @typedef {import("./database-view.js").DatabaseViewSnapshot} DatabaseViewSnapshot */
/** @typedef {import("./database-view.js").DatabaseQueryCallbacks} DatabaseQueryCallbacks */
/** @typedef {import("./database-view.js").DatabaseQuerySource} DatabaseQuerySource */
