/** @typedef {"setDatabaseTtl" | "clearDatabaseTtl" | "moveDatabaseHome"} AgentOperation */
/** @type {readonly import("./routes.js").V1Route[]} */
export const agentRoutes = Object.freeze(/** @type {[AgentOperation, "PUT" | "DELETE" | "POST", string[], string, string][]} */ ([
  ["setDatabaseTtl", "PUT", ["application/json"], "ttl", "sdk/SQL-API.md#databases-for-agents"],
  ["clearDatabaseTtl", "DELETE", [], "ttl", "sdk/SQL-API.md#databases-for-agents"],
  ["moveDatabaseHome", "POST", ["application/json"], "move-home", "sdk/SQL-API.md#placement-where-a-database-lives"],
]).map(([operation, method, requestMediaTypes, suffix, contract]) => Object.freeze({
  operation, method, requestMediaTypes,
  pathTemplate: `/v1/projects/{project}/databases/{database}/${suffix}`,
  permission: "databases:write", responseMediaType: "application/json", successStatuses: [200],
  idempotencyKey: "none", replaySafe: false, contract, contractText: [],
})));
