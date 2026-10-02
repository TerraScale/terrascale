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

// TerraBase v1: name-based documents, transactions, queries and shapes.
export {
  createTerraBaseV1Client,
  TerraBaseV1Client,
  V1Database,
  V1Collection,
  V1Transaction,
} from "./v1/client.js";
export { V1ShapeSubscription } from "./v1/shapes.js";
export { TerraBaseV1Error } from "./v1/errors.js";
export { v1Filter } from "./v1/filter.js";
export { documentIdFor, newIdempotencyKey } from "./v1/ids.js";
export { ExactDecimal } from "./v1/json.js";

/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseV1ClientConfig */
/** @typedef {import("./v1/client.js").V1RequestOptions} V1RequestOptions */
/** @typedef {import("./v1/client.js").V1ReadOptions} V1ReadOptions */
/** @typedef {import("./v1/client.js").V1WriteOptions} V1WriteOptions */
/** @typedef {import("./v1/client.js").V1CreateOptions} V1CreateOptions */
/** @typedef {import("./v1/client.js").V1ConditionalWriteOptions} V1ConditionalWriteOptions */
/** @typedef {import("./v1/client.js").V1PatchOp} V1PatchOp */
/** @typedef {import("./v1/client.js").V1Patch} V1Patch */
/** @typedef {import("./v1/client.js").V1SortKey} V1SortKey */
/** @typedef {import("./v1/client.js").V1Query} V1Query */
/** @typedef {import("./v1/client.js").V1Count} V1Count */
/** @typedef {import("./v1/client.js").V1QueryPage} V1QueryPage */
/** @typedef {import("./v1/client.js").V1Precondition} V1Precondition */
/** @typedef {import("./v1/client.js").V1TransactionOpResult} V1TransactionOpResult */
/** @typedef {import("./v1/client.js").V1CountBounds} V1CountBounds */
/** @typedef {import("./v1/client.js").V1TransactionResult} V1TransactionResult */
/** @typedef {import("./v1/decode.js").V1Document} V1Document */
/** @typedef {import("./v1/decode.js").V1Sibling} V1Sibling */
/** @typedef {import("./v1/decode.js").V1SiblingEvent} V1SiblingEvent */
/** @typedef {import("./v1/decode.js").V1WriteResult} V1WriteResult */
/** @typedef {import("./v1/decode.js").V1Durability} V1Durability */
/** @typedef {import("./v1/filter.js").V1Filter} V1Filter */
/** @typedef {import("./v1/filter.js").V1Path} V1Path */
/** @typedef {import("./v1/filter.js").V1Scalar} V1Scalar */
/** @typedef {import("./v1/json.js").JsonValue} V1JsonValue */
/** @typedef {import("./v1/json.js").JsonObject} V1JsonObject */
/** @typedef {import("./v1/json.js").JsonInput} V1JsonInput */
/** @typedef {import("./v1/json.js").JsonInputObject} V1JsonInputObject */
/** @typedef {import("./v1/shapes.js").V1ShapeRequest} V1ShapeRequest */
/** @typedef {import("./v1/shapes.js").V1ShapeInfo} V1ShapeInfo */
/** @typedef {import("./v1/shapes.js").V1SubscribeOptions} V1SubscribeOptions */
/** @typedef {import("./v1/shapes.js").V1ShapeEvent} V1ShapeEvent */
/** @typedef {import("./v1/shapes.js").V1ShapeChange} V1ShapeChange */
/** @typedef {import("./v1/shapes.js").V1ShapeOp} V1ShapeOp */
