// The TerraBase v1 routes this client calls, as frozen in the terrabase
// contracts docs/api-v1.md, docs/query-v1.md and docs/shapes-v1.md. The client
// builds every URL from this table, and sdk-v1-route-manifest.json is
// generated from it (scripts/generate-v1-route-manifest.js).

/**
 * @typedef {"getDocument" | "putDocument" | "createDocument" | "patchDocument" | "deleteDocument" | "commitTransaction" | "queryDocuments" | "createShape" | "readShape" | "streamShape"} V1Operation
 */

/**
 * @typedef {object} V1Route
 * @property {V1Operation} operation
 * @property {"GET" | "PUT" | "POST" | "PATCH" | "DELETE"} method
 * @property {string} pathTemplate
 * @property {"read" | "write" | "shape"} permission
 * @property {readonly string[]} requestMediaTypes
 * @property {string} responseMediaType
 * @property {readonly number[]} successStatuses
 * @property {"none" | "optional"} idempotencyKey
 * @property {boolean} replaySafe Retried after transport errors and transient 503s without an Idempotency-Key
 *   (reads, queries and the shape gatekeeper, which stores nothing).
 * @property {string} contract The contract document declaring the route.
 * @property {readonly string[]} contractText Literal declarations of the route in that document.
 */

const documents = "/v1/databases/{db}/collections/{collection}/documents";

/** @type {readonly V1Route[]} */
export const v1Routes = Object.freeze(
  /** @type {V1Route[]} */ ([
    { operation: "getDocument", method: "GET", pathTemplate: `${documents}/{id}`, permission: "read", requestMediaTypes: [], responseMediaType: "application/json", successStatuses: [200], idempotencyKey: "none", replaySafe: true, contract: "api-v1.md", contractText: ["Base path: `/v1/databases/{db}/collections/{collection}/documents`", "`GET …/documents/{id}`"] },
    { operation: "putDocument", method: "PUT", pathTemplate: `${documents}/{id}`, permission: "write", requestMediaTypes: ["application/json"], responseMediaType: "application/json", successStatuses: [200, 201], idempotencyKey: "optional", replaySafe: false, contract: "api-v1.md", contractText: ["Base path: `/v1/databases/{db}/collections/{collection}/documents`", "`PUT …/documents/{id}`"] },
    { operation: "createDocument", method: "POST", pathTemplate: documents, permission: "write", requestMediaTypes: ["application/json"], responseMediaType: "application/json", successStatuses: [201], idempotencyKey: "optional", replaySafe: false, contract: "api-v1.md", contractText: ["Base path: `/v1/databases/{db}/collections/{collection}/documents`", "`POST …/documents`"] },
    { operation: "patchDocument", method: "PATCH", pathTemplate: `${documents}/{id}`, permission: "write", requestMediaTypes: ["application/merge-patch+json", "application/vnd.terrabase.ops+json"], responseMediaType: "application/json", successStatuses: [200], idempotencyKey: "optional", replaySafe: false, contract: "api-v1.md", contractText: ["Base path: `/v1/databases/{db}/collections/{collection}/documents`", "`PATCH …/documents/{id}`"] },
    { operation: "deleteDocument", method: "DELETE", pathTemplate: `${documents}/{id}`, permission: "write", requestMediaTypes: [], responseMediaType: "application/json", successStatuses: [200], idempotencyKey: "optional", replaySafe: false, contract: "api-v1.md", contractText: ["Base path: `/v1/databases/{db}/collections/{collection}/documents`", "`DELETE …/documents/{id}`"] },
    { operation: "commitTransaction", method: "POST", pathTemplate: "/v1/databases/{db}/transactions", permission: "write", requestMediaTypes: ["application/json"], responseMediaType: "application/json", successStatuses: [200], idempotencyKey: "optional", replaySafe: false, contract: "api-v1.md", contractText: ["`POST /v1/databases/{db}/transactions`"] },
    { operation: "queryDocuments", method: "POST", pathTemplate: "/v1/databases/{db}/collections/{collection}/query", permission: "read", requestMediaTypes: ["application/json"], responseMediaType: "application/json", successStatuses: [200], idempotencyKey: "none", replaySafe: true, contract: "query-v1.md", contractText: ["`POST /v1/databases/{db}/collections/{collection}/query`"] },
    { operation: "createShape", method: "POST", pathTemplate: "/v1/databases/{db}/shapes", permission: "shape", requestMediaTypes: ["application/json"], responseMediaType: "application/json", successStatuses: [201], idempotencyKey: "none", replaySafe: true, contract: "shapes-v1.md", contractText: ["`POST /v1/databases/{db}/shapes`", "gatekeeper stores nothing, so clients may retry it like a read"] },
    { operation: "readShape", method: "GET", pathTemplate: "/v1/shapes/{shape_id}", permission: "shape", requestMediaTypes: [], responseMediaType: "application/json", successStatuses: [200, 307], idempotencyKey: "none", replaySafe: true, contract: "shapes-v1.md", contractText: ["`GET /v1/shapes/{shape_id}?handle=…&offset=…[&live=true&cursor=…]`"] },
    { operation: "streamShape", method: "GET", pathTemplate: "/v1/shapes/{shape_id}/sse", permission: "shape", requestMediaTypes: [], responseMediaType: "text/event-stream", successStatuses: [200], idempotencyKey: "none", replaySafe: true, contract: "shapes-v1.md", contractText: ["`GET /v1/shapes/{shape_id}/sse?handle=…&offset=…`"] },
  ]).map((route) => Object.freeze({
    ...route,
    requestMediaTypes: Object.freeze([...route.requestMediaTypes]),
    successStatuses: Object.freeze([...route.successStatuses]),
    contractText: Object.freeze([...route.contractText]),
  })),
);

/**
 * @param {V1Operation} operation
 * @returns {V1Route}
 */
export function v1Route(operation) {
  const route = v1Routes.find((candidate) => candidate.operation === operation);
  if (route === undefined) throw new TypeError(`Unknown v1 operation ${operation}.`);
  return route;
}

/**
 * @param {V1Route} route
 * @param {Readonly<Record<string, string>>} params Already validated path parameters.
 * @returns {string}
 */
export function v1RoutePath(route, params) {
  return route.pathTemplate.replace(/\{([a-z_]+)\}/gu, (_match, name) => {
    const value = params[name];
    if (value === undefined) throw new TypeError(`Missing path parameter ${name}.`);
    return encodeURIComponent(value);
  });
}
