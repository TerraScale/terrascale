import { agentRoutes } from "./agent-routes.js";
import { fileStorageRoutes } from "./file-storage-routes.js";
import { managementV1Routes } from "./management-routes.js";
// Current TerraBase database catalog routes.
/** @typedef {import("./agent-routes.js").AgentOperation | import("./file-storage-routes.js").FileStorageOperation | import("./management-routes.js").ManagementOperation | "createDatabase" | "listDatabases" | "getDatabase" | "updateDatabase" | "deleteDatabase" | "branchDatabase"} V1Operation */
/**
 * @typedef {object} V1Route
 * @property {V1Operation} operation
 * @property {"GET" | "PUT" | "POST" | "PATCH" | "DELETE"} method
 * @property {string} pathTemplate
 * @property {"documents:read" | "documents:write" | "databases:read" | "databases:write" | "anonymous" | "platform-admin"} permission
 * @property {readonly string[]} [additionalPermissions]
 * @property {readonly string[]} requestMediaTypes
 * @property {string} responseMediaType
 * @property {readonly number[]} successStatuses
 * @property {"none"} idempotencyKey The producer rejects Idempotency-Key.
 * @property {boolean} replaySafe Informational; the transport resends only reads and read-only queries, never mutations.
 * @property {string} contract
 * @property {readonly string[]} contractText
 */
const database = "/v1/projects/{project}/databases/{database}";
const databases = "/v1/projects/{project}/databases";
/** @type {readonly V1Route[]} */
export const v1Routes = Object.freeze(/** @type {V1Route[]} */ ([
  { operation: "createDatabase", method: "POST", pathTemplate: databases, permission: "databases:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
  { operation: "listDatabases", method: "GET", pathTemplate: databases, permission: "databases:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "getDatabase", method: "GET", pathTemplate: database, permission: "databases:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "updateDatabase", method: "PATCH", pathTemplate: database, permission: "databases:write", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "deleteDatabase", method: "DELETE", pathTemplate: database, permission: "databases:write", requestMediaTypes: [], successStatuses: [204] },
  { operation: "branchDatabase", method: "POST", pathTemplate: `${database}/branches`, permission: "databases:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
].map(route => ({
  ...route, responseMediaType: route.operation === "deleteDatabase" ? "" : "application/json", idempotencyKey: "none",
  replaySafe: !route.permission.endsWith(":write"), contract: "docs/terrabase/api.md", contractText: [],
}))).map(route => Object.freeze({
  ...route, requestMediaTypes: Object.freeze(route.requestMediaTypes),
  successStatuses: Object.freeze(route.successStatuses), contractText: Object.freeze(route.contractText),
})));
/** @param {V1Operation} operation @returns {V1Route} */
export function v1Route(operation) {
  const route = [...v1Routes, ...managementV1Routes, ...fileStorageRoutes, ...agentRoutes].find(candidate => candidate.operation === operation);
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
