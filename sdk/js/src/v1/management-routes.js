/** @typedef {"executeSql" | "readTableShape" | "getShapeManifest" | "getShapeKey" | "connectSql" | "connectShapeHints" | "healthz" | "readyz" | "backupz" | "listPlatformDatabases" | "createBackup" | "listBackups" | "restoreBackup" | "getRestoreOperation" | "getBackupPolicy" | "setBackupPolicy" | "getRestoreWindow" | "planPointInTime" | "restorePointInTime" | "getBackupDestination" | "setBackupDestination" | "deleteBackupDestination" | "getProjectUsage" | "getProjectAnalytics" | "getDatabaseUsage" | "getDatabaseAnalytics" | "createSavedQuery" | "listSavedQueries" | "getSavedQuery" | "updateSavedQuery" | "deleteSavedQuery"} ManagementOperation */
const saved = "/v1/projects/{project}/saved-queries";
/** @type {readonly import("./routes.js").V1Route[]} */
export const managementV1Routes = Object.freeze([
  route("executeSql", "POST", "/v1/projects/{project}/databases/{database}/sql/execute"),
  route("readTableShape", "GET", "/v1/projects/{project}/databases/{database}/tables/{table}/shape"),
  route("getShapeManifest", "GET", "/v1/projects/{project}/databases/{database}/tables/{table}/shape/manifest"),
  route("getShapeKey", "GET", "/v1/projects/{project}/databases/{database}/tables/{table}/shape/keys"),
  route("connectShapeHints", "GET", "/v1/projects/{project}/databases/{database}/tables/{table}/shape/ws", 101),
  route("connectSql", "GET", "/v1/projects/{project}/databases/{database}/sql/ws", 101),
  route("healthz", "GET", "/healthz"),
  route("readyz", "GET", "/readyz"),
  route("backupz", "GET", "/backupz"),
  route("listPlatformDatabases", "GET", "/v1/platform/databases"),
  route("createBackup", "POST", "/v1/projects/{project}/databases/{database}/backups", 201),
  route("listBackups", "GET", "/v1/projects/{project}/databases/{database}/backups", 200),
  route("restoreBackup", "POST", "/v1/projects/{project}/databases/{database}/backups/{backup}/restore", 202),
  route("getRestoreOperation", "GET", "/v1/projects/{project}/databases/{database}/restores/{operation}", 200),
  route("getBackupPolicy", "GET", "/v1/projects/{project}/databases/{database}/backup-policy", 200),
  route("setBackupPolicy", "PUT", "/v1/projects/{project}/databases/{database}/backup-policy", 200),
  route("getRestoreWindow", "GET", "/v1/projects/{project}/databases/{database}/point-in-time", 200),
  route("planPointInTime", "POST", "/v1/projects/{project}/databases/{database}/point-in-time/plan", 200),
  route("restorePointInTime", "POST", "/v1/projects/{project}/databases/{database}/point-in-time/restore", 202),
  route("getBackupDestination", "GET", "/v1/projects/{project}/backup-destination", 200),
  route("setBackupDestination", "PUT", "/v1/projects/{project}/backup-destination", 200),
  route("deleteBackupDestination", "DELETE", "/v1/projects/{project}/backup-destination", 204),
  route("getProjectUsage", "GET", "/v1/projects/{project}/usage"),
  route("getProjectAnalytics", "GET", "/v1/projects/{project}/analytics"),
  route("getDatabaseUsage", "GET", "/v1/projects/{project}/databases/{database}/usage"),
  route("getDatabaseAnalytics", "GET", "/v1/projects/{project}/databases/{database}/analytics"),
  route("createSavedQuery", "POST", saved, 201),
  route("listSavedQueries", "GET", saved),
  route("getSavedQuery", "GET", `${saved}/{query}`),
  route("updateSavedQuery", "PUT", `${saved}/{query}`),
  route("deleteSavedQuery", "DELETE", `${saved}/{query}`, 204),
]);

/** @param {ManagementOperation} operation @param {import("./routes.js").V1Route["method"]} method @param {string} pathTemplate @param {number} [status] @returns {import("./routes.js").V1Route} */
function route(operation, method, pathTemplate, status = 200) {
  const write = method !== "GET" && operation !== "planPointInTime";
  const sql = operation === "executeSql" || operation === "readTableShape" || operation === "getShapeManifest" || operation === "getShapeKey" || operation === "connectSql" || operation === "connectShapeHints";
  return Object.freeze({ operation, method, pathTemplate,
    permission: sql ? "sql:read" : pathTemplate.startsWith("/v1/platform/") ? "platform-admin" : !pathTemplate.startsWith("/v1/") ? "anonymous" : write ? "databases:write" : "databases:read",
    additionalPermissions: Object.freeze([]),
    requestMediaTypes: Object.freeze(operation === "createBackup" ? [] : method === "POST" || method === "PUT" || method === "PATCH" ? ["application/json"] : []),
    responseMediaType: status === 204 || status === 101 ? "" : "application/json", successStatuses: Object.freeze(operation === "readTableShape" ? [200, 307] : operation === "readyz" || operation === "backupz" ? [200, 503] : [status]),
    idempotencyKey: "none", replaySafe: !write, contract: "docs/terrabase/api.md", contractText: Object.freeze([]),
  });
}
