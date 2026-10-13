// Current TerraBase routes at e30ffdc; readShape is retained from release 6cb9745 only.
/** @typedef {"getDocument" | "putDocument" | "createDocument" | "patchDocument" | "deleteDocument" | "commitTransaction" | "queryDocuments" | "createCollection" | "getCollection" | "listCollections" | "dropCollection" | "readShape" | "runSql" | "checkSql" | "explainSql" | "streamDatabaseChanges" | "listDocuments" | "getCollectionSchema" | "putCollectionSchema" | "deleteCollectionSchema" | "createDatabase" | "listDatabases" | "getDatabase" | "updateDatabase" | "deleteDatabase" | "branchDatabase"} V1Operation */
/**
 * @typedef {object} V1Route
 * @property {V1Operation} operation
 * @property {"GET" | "PUT" | "POST" | "PATCH" | "DELETE"} method
 * @property {string} pathTemplate
 * @property {"documents:read" | "documents:write" | "shapes:read" | "databases:read" | "databases:write"} permission
 * @property {readonly string[]} requestMediaTypes
 * @property {string} responseMediaType
 * @property {readonly number[]} successStatuses
 * @property {"none"} idempotencyKey The producer rejects Idempotency-Key.
 * @property {boolean} replaySafe Informational only; requests are never automatically replayed.
 * @property {string} contract
 * @property {readonly string[]} contractText
 */
const database = "/v1/projects/{project}/databases/{database}";
const collections = `${database}/collections`;
const collection = `${collections}/{collection}`;
const documents = `${collection}/documents`;
const databases = "/v1/projects/{project}/databases";
/** @type {readonly V1Route[]} */
export const v1Routes = Object.freeze(/** @type {V1Route[]} */ ([
  { operation: "getDocument", method: "GET", pathTemplate: `${documents}/by-key`, permission: "documents:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "putDocument", method: "PUT", pathTemplate: `${documents}/by-key`, permission: "documents:write", requestMediaTypes: ["application/json"], successStatuses: [200, 201] },
  { operation: "createDocument", method: "POST", pathTemplate: documents, permission: "documents:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
  { operation: "patchDocument", method: "PATCH", pathTemplate: `${documents}/by-key`, permission: "documents:write", requestMediaTypes: ["application/merge-patch+json", "application/vnd.terrabase.ops+json"], successStatuses: [200] },
  { operation: "deleteDocument", method: "DELETE", pathTemplate: `${documents}/by-key`, permission: "documents:write", requestMediaTypes: [], successStatuses: [200] },
  { operation: "commitTransaction", method: "POST", pathTemplate: `${database}/transactions`, permission: "documents:write", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "queryDocuments", method: "POST", pathTemplate: `${collection}/query`, permission: "documents:read", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "createCollection", method: "POST", pathTemplate: collections, permission: "documents:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
  { operation: "getCollection", method: "GET", pathTemplate: collection, permission: "documents:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "listCollections", method: "GET", pathTemplate: collections, permission: "documents:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "dropCollection", method: "DELETE", pathTemplate: collection, permission: "documents:write", requestMediaTypes: [], successStatuses: [200] },
  { operation: "readShape", method: "GET", pathTemplate: `${collection}/shape`, permission: "shapes:read", requestMediaTypes: [], successStatuses: [200, 307] },
  { operation: "runSql", method: "POST", pathTemplate: `${database}/sql`, permission: "databases:read", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "checkSql", method: "POST", pathTemplate: `${database}/sql/check`, permission: "databases:read", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "explainSql", method: "POST", pathTemplate: `${database}/sql/explain`, permission: "databases:read", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "streamDatabaseChanges", method: "GET", pathTemplate: `${database}/changes`, permission: "documents:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "listDocuments", method: "GET", pathTemplate: documents, permission: "documents:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "getCollectionSchema", method: "GET", pathTemplate: `${collection}/schema`, permission: "documents:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "putCollectionSchema", method: "PUT", pathTemplate: `${collection}/schema`, permission: "documents:write", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "deleteCollectionSchema", method: "DELETE", pathTemplate: `${collection}/schema`, permission: "documents:write", requestMediaTypes: [], successStatuses: [200] },
  { operation: "createDatabase", method: "POST", pathTemplate: databases, permission: "databases:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
  { operation: "listDatabases", method: "GET", pathTemplate: databases, permission: "databases:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "getDatabase", method: "GET", pathTemplate: database, permission: "databases:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "updateDatabase", method: "PATCH", pathTemplate: database, permission: "databases:write", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "deleteDatabase", method: "DELETE", pathTemplate: database, permission: "databases:write", requestMediaTypes: [], successStatuses: [204] },
  { operation: "branchDatabase", method: "POST", pathTemplate: `${database}/branches`, permission: "databases:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
].map(route => ({
  ...route, responseMediaType: route.operation === "deleteDatabase" ? "" : route.operation === "streamDatabaseChanges" ? "text/event-stream" : "application/json", idempotencyKey: "none",
  replaySafe: !route.permission.endsWith(":write"),
  contract: route.operation === "readShape" ? "docs/shape-reads.md" :
    ["runSql", "checkSql", "explainSql", "streamDatabaseChanges"].includes(route.operation) ? "docs/terrabase/api.md" : "docs/decisions/0004-document-api-v1.md",
  contractText: [],
}))).map(route => Object.freeze({
  ...route, requestMediaTypes: Object.freeze(route.requestMediaTypes),
  successStatuses: Object.freeze(route.successStatuses), contractText: Object.freeze(route.contractText),
})));
/** @param {V1Operation} operation @returns {V1Route} */
export function v1Route(operation) {
  const route = v1Routes.find(candidate => candidate.operation === operation);
  if (route === undefined) throw new TypeError(`Unknown v1 operation ${operation}.`);
  return route;
}
/** @param {V1Route} route @param {Readonly<Record<string, string>>} params @returns {string} */
export function v1RoutePath(route, params) {
  return route.pathTemplate.replace(/\{([a-z_]+)\}/gu, (_match, name) => {
    const value = params[name];
    if (value === undefined) throw new TypeError(`Missing path parameter ${name}.`);
    return encodeURIComponent(value);
  });
}
