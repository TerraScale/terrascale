/** @typedef {"executeSql" | "exportDatabase" | "readTableShape" | "getShapeManifest" | "getShapeKey" | "connectSql" | "connectShapeHints" | "healthz" | "readyz" | "backupz" | "listPlatformDatabases" | "createBackup" | "listBackups" | "restoreBackup" | "getRestoreOperation" | "getBackupPolicy" | "setBackupPolicy" | "getRestoreWindow" | "planPointInTime" | "restorePointInTime" | "rewindPointInTime" | "getBackupDestination" | "setBackupDestination" | "deleteBackupDestination" | "getProjectUsage" | "getProjectAnalytics" | "getDatabaseUsage" | "getDatabaseAnalytics" | "listDatabaseLogs" | "getProjectMetrics" | "listRegions" | "listFunctions" | "deployFunction" | "getFunction" | "deleteFunction" | "invokeFunction" | "listFunctionLogs"} ManagementOperation */
/** @type {readonly import("./routes.js").V1Route[]} */
export const managementV1Routes = Object.freeze([
  route("executeSql", "POST", "/v1/projects/{project}/databases/{database}/sql/execute"),
  route("exportDatabase", "GET", "/v1/projects/{project}/databases/{database}/export"),
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
  route("rewindPointInTime", "POST", "/v1/projects/{project}/databases/{database}/point-in-time/rewind", 200),
  route("getBackupDestination", "GET", "/v1/projects/{project}/backup-destination", 200),
  route("setBackupDestination", "PUT", "/v1/projects/{project}/backup-destination", 200),
  route("deleteBackupDestination", "DELETE", "/v1/projects/{project}/backup-destination", 204),
  route("getProjectUsage", "GET", "/v1/projects/{project}/usage"),
  route("getProjectAnalytics", "GET", "/v1/projects/{project}/analytics"),
  route("getDatabaseUsage", "GET", "/v1/projects/{project}/databases/{database}/usage"),
  route("getDatabaseAnalytics", "GET", "/v1/projects/{project}/databases/{database}/analytics"),
  route("listDatabaseLogs", "GET", "/v1/projects/{project}/databases/{database}/logs"),
  route("getProjectMetrics", "GET", "/v1/projects/{project}/metrics"),
  route("listRegions", "GET", "/v1/regions"),
  route("listFunctions", "GET", "/v1/projects/{project}/databases/{database}/functions", 200,
    { permission: "sql:read", additionalPermissions: ["sql:write"] }),
  route("deployFunction", "PUT", "/v1/projects/{project}/databases/{database}/functions/{function}", 200,
    { permission: "sql:write", additionalPermissions: ["sql:read"] }),
  route("getFunction", "GET", "/v1/projects/{project}/databases/{database}/functions/{function}", 200,
    { permission: "sql:read", additionalPermissions: ["sql:write"] }),
  route("deleteFunction", "DELETE", "/v1/projects/{project}/databases/{database}/functions/{function}", 204,
    { permission: "sql:write", additionalPermissions: ["sql:read"] }),
  route("invokeFunction", "POST", "/v1/projects/{project}/databases/{database}/functions/{function}/invoke", 200,
    { permission: "sql:read", requestMediaTypes: ["application/json"], responseMediaType: "",
      successStatuses: [200, 201, 202, 203, 204, 205, 206, 207, 208, 226] }),
  route("listFunctionLogs", "GET", "/v1/projects/{project}/databases/{database}/functions/{function}/logs", 200,
    { permission: "sql:read", additionalPermissions: ["sql:write"] }),
]);

/** @param {ManagementOperation} operation @param {import("./routes.js").V1Route["method"]} method @param {string} pathTemplate @param {number} [status] @param {Partial<import("./routes.js").V1Route>} [overrides] @returns {import("./routes.js").V1Route} */
function route(operation, method, pathTemplate, status = 200, overrides = {}) {
  const write = method !== "GET" && operation !== "planPointInTime";
  const anonymous = operation === "listRegions";
  const sql = operation === "executeSql" || operation === "exportDatabase" || operation === "readTableShape" || operation === "getShapeManifest" || operation === "getShapeKey" || operation === "connectSql" || operation === "connectShapeHints";
  const permission = sql ? "sql:read" : pathTemplate.startsWith("/v1/platform/") ? "platform-admin" : anonymous || !pathTemplate.startsWith("/v1/") ? "anonymous" : write ? "databases:write" : "databases:read";
  const requestMediaTypes = operation === "createBackup" ? [] : method === "POST" || method === "PUT" || method === "PATCH" ? ["application/json"] : [];
  const responseMediaType = status === 204 || status === 101 ? "" : operation === "exportDatabase" || operation === "getProjectMetrics" ? "text/plain" : "application/json";
  return Object.freeze({ operation, method, pathTemplate,
    permission: overrides.permission ?? permission,
    additionalPermissions: Object.freeze([...(overrides.additionalPermissions ?? [])]),
    requestMediaTypes: Object.freeze(overrides.requestMediaTypes ?? requestMediaTypes),
    responseMediaType: overrides.responseMediaType ?? responseMediaType,
    successStatuses: Object.freeze(overrides.successStatuses ?? (operation === "readTableShape" ? [200, 307] : operation === "readyz" || operation === "backupz" ? [200, 503] : [status])),
    idempotencyKey: "none", replaySafe: !write, contract: "docs/terrabase/api.md", contractText: Object.freeze([]),
  });
}
