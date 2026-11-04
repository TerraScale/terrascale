/** @typedef {"setDatabaseTtl" | "clearDatabaseTtl"} AgentOperation */
/** @type {readonly import("./routes.js").V1Route[]} */
export const agentRoutes = Object.freeze(/** @type {[AgentOperation, "PUT" | "DELETE", string[]][]} */ ([
  ["setDatabaseTtl", "PUT", ["application/json"]],
  ["clearDatabaseTtl", "DELETE", []],
]).map(([operation, method, requestMediaTypes]) => Object.freeze({
  operation, method, requestMediaTypes,
  pathTemplate: "/v1/projects/{project}/databases/{database}/ttl",
  permission: "databases:write", responseMediaType: "application/json", successStatuses: [200],
  idempotencyKey: "none", replaySafe: false, contract: "sdk/SQL-API.md#databases-for-agents", contractText: [],
})));
