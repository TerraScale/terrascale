export { createTerraBaseV1Client as createTerraBaseClient, TerraBaseV1Client as TerraBaseClient } from "./v1/terrabase-v1.js";
export { terraScaleReleaseBlockers, TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
// TerraBase v1: name-based documents, transactions, queries and shapes.
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
  V1ForbiddenScopeError,
  V1NotFoundError,
  V1ErrorCode,
  isTerraBaseV1Error,
} from "./v1/errors.js";
export { v1Filter } from "./v1/filter.js";
export { documentIdFor } from "./v1/ids.js";
export { ExactDecimal } from "./v1/json.js";

/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseV1ClientConfig */
/** @typedef {import("./v1/terrabase-v1.js").V1RequestOptions} V1RequestOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1ReadOptions} V1ReadOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1WriteOptions} V1WriteOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1CreateOptions} V1CreateOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1ConditionalWriteOptions} V1ConditionalWriteOptions */
/** @typedef {import("./v1/terrabase-v1.js").V1PatchOp} V1PatchOp */
/** @typedef {import("./v1/terrabase-v1.js").V1Patch} V1Patch */
/** @typedef {import("./v1/terrabase-v1.js").V1SortKey} V1SortKey */
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
/** @typedef {import("./v1/json.js").JsonValue} V1JsonValue */
/** @typedef {import("./v1/json.js").JsonObject} V1JsonObject */
/** @typedef {import("./v1/json.js").JsonInput} V1JsonInput */
/** @typedef {import("./v1/json.js").JsonInputObject} V1JsonInputObject */
/** @typedef {import("./v1/shapes.js").V1ShapeRequest} V1ShapeRequest */
/** @typedef {import("./v1/shapes.js").V1ShapeChange} V1ShapeChange */
/** @typedef {import("./v1/shapes.js").V1ShapeOp} V1ShapeOp */

export { staticBaseUrlResolver } from "./v1/transport.js";
/** @typedef {import("./v1/transport.js").TerraBaseV1ClientConfig} TerraBaseClientConfig */
/** @typedef {import("./v1/transport.js").V1EndpointContext} V1EndpointContext */
/** @typedef {import("./v1/transport.js").V1EndpointResolver} V1EndpointResolver */
/** @typedef {import("./v1/terrabase-v1.js").V1CollectionInfo} V1CollectionInfo */
/** @typedef {import("./v1/terrabase-v1.js").V1CollectionPage} V1CollectionPage */
/** @typedef {import("./v1/terrabase-v1.js").V1CollectionListOptions} V1CollectionListOptions */
/** @typedef {import("./v1/shapes.js").V1ShapePage} V1ShapePage */
