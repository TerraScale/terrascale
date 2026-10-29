/** @typedef {"previewCollectionSchema" | "getShapeManifest" | "exportDatabase" | "executeSql" | "putCollectionTtl" | "getCollectionTtl" | "deleteCollectionTtl" | "healthz" | "readyz" | "backupz" | "replicationHealth" | "listPlatformDatabases" | "listPlatformCollections" | "listPlatformDocuments" | "getPlatformDocument" | "createImport" | "getImport" | "uploadImportSource" | "updateImport" | "startImport" | "cancelImport" | "resumeImport" | "createBackup" | "listBackups" | "restoreBackup" | "getBackupPolicy" | "setBackupPolicy" | "getRestoreWindow" | "planPointInTime" | "restorePointInTime" | "getBackupDestination" | "setBackupDestination" | "deleteBackupDestination" | "getProjectUsage" | "getProjectAnalytics" | "getProjectReplication" | "getDatabaseUsage" | "getDatabaseAnalytics" | "getDatabaseReplication" | "createIndex" | "listIndexes" | "dropIndex" | "listIndexRecommendations" | "applyIndexRecommendation" | "getDatabaseSchema" | "inferCollectionSchema" | "declareCollectionSchema" | "listQueryShapes" | "createSavedQuery" | "listSavedQueries" | "getSavedQuery" | "updateSavedQuery" | "deleteSavedQuery" | "createChangeWebhook" | "listChangeWebhooks" | "deleteChangeWebhook" | "listChangeWebhookDeliveries"} ManagementOperation */
const webhooks = "/v1/projects/{project}/databases/{database}/collections/{collection}/webhooks";
const saved = "/v1/projects/{project}/saved-queries";
const ttl = "/v1/projects/{project}/databases/{database}/collections/{collection}/ttl";
/** @type {readonly import("./routes.js").V1Route[]} */
export const managementV1Routes = Object.freeze([
  Object.freeze({ operation: "previewCollectionSchema", method: "POST", pathTemplate: "/v1/projects/{project}/databases/{database}/collections/{collection}/schema/preview",
    permission: "documents:read", additionalPermissions: Object.freeze(["documents:write"]), requestMediaTypes: Object.freeze(["application/json"]),
    responseMediaType: "application/json", successStatuses: Object.freeze([200]), idempotencyKey: "none", replaySafe: true,
    contract: "contracts/schema-preview/openapi.json", contractText: Object.freeze([]) }),
  route("createChangeWebhook", "POST", webhooks, 201),
  route("listChangeWebhooks", "GET", webhooks),
  route("deleteChangeWebhook", "DELETE", `${webhooks}/{webhook}`, 204),
  route("listChangeWebhookDeliveries", "GET", `${webhooks}/{webhook}/deliveries`),
  route("getShapeManifest", "GET", "/v1/projects/{project}/databases/{database}/collections/{collection}/shape/manifest"),
  route("exportDatabase", "GET", "/v1/projects/{project}/databases/{database}/export"),
  // Reads need documents:read; writes and DDL also need documents:write, checked per statement.
  route("executeSql", "POST", "/v1/projects/{project}/databases/{database}/sql/execute"),
  route("putCollectionTtl", "PUT", ttl),
  route("getCollectionTtl", "GET", ttl),
  route("deleteCollectionTtl", "DELETE", ttl),
  route("healthz", "GET", "/healthz"),
  route("readyz", "GET", "/readyz"),
  route("backupz", "GET", "/backupz"),
  route("replicationHealth", "GET", "/health/replication"),
  route("listPlatformDatabases", "GET", "/v1/platform/databases"),
  route("listPlatformCollections", "GET", "/v1/platform/databases/{database}/collections"),
  route("listPlatformDocuments", "GET", "/v1/platform/databases/{database}/collections/{collection}/documents"),
  route("getPlatformDocument", "GET", "/v1/platform/databases/{database}/collections/{collection}/documents/by-key"),
  route("createImport", "POST", "/v1/projects/{project}/databases/{database}/import", 201),
  route("getImport", "GET", "/v1/projects/{project}/databases/{database}/import", 200),
  route("uploadImportSource", "PUT", "/v1/projects/{project}/databases/{database}/import/source", 200),
  route("updateImport", "PATCH", "/v1/projects/{project}/databases/{database}/import", 202),
  route("startImport", "POST", "/v1/projects/{project}/databases/{database}/import/start", 202),
  route("cancelImport", "POST", "/v1/projects/{project}/databases/{database}/import/cancel", 202),
  route("resumeImport", "POST", "/v1/projects/{project}/databases/{database}/import/resume", 202),
  route("createBackup", "POST", "/v1/projects/{project}/databases/{database}/backups", 201),
  route("listBackups", "GET", "/v1/projects/{project}/databases/{database}/backups", 200),
  route("restoreBackup", "POST", "/v1/projects/{project}/databases/{database}/backups/{backup}/restore", 201),
  route("getBackupPolicy", "GET", "/v1/projects/{project}/databases/{database}/backup-policy", 200),
  route("setBackupPolicy", "PUT", "/v1/projects/{project}/databases/{database}/backup-policy", 200),
  route("getRestoreWindow", "GET", "/v1/projects/{project}/databases/{database}/point-in-time", 200),
  route("planPointInTime", "POST", "/v1/projects/{project}/databases/{database}/point-in-time/plan", 200),
  route("restorePointInTime", "POST", "/v1/projects/{project}/databases/{database}/point-in-time/restore", 201),
  route("getBackupDestination", "GET", "/v1/projects/{project}/backup-destination", 200),
  route("setBackupDestination", "PUT", "/v1/projects/{project}/backup-destination", 200),
  route("deleteBackupDestination", "DELETE", "/v1/projects/{project}/backup-destination", 204),
  route("getProjectUsage", "GET", "/v1/projects/{project}/usage"),
  route("getProjectAnalytics", "GET", "/v1/projects/{project}/analytics"),
  route("getProjectReplication", "GET", "/v1/projects/{project}/replication"),
  route("getDatabaseUsage", "GET", "/v1/projects/{project}/databases/{database}/usage"),
  route("getDatabaseAnalytics", "GET", "/v1/projects/{project}/databases/{database}/analytics"),
  route("getDatabaseReplication", "GET", "/v1/projects/{project}/databases/{database}/replication"),
  route("createIndex", "POST", "/v1/projects/{project}/databases/{database}/indexes", 201),
  route("listIndexes", "GET", "/v1/projects/{project}/databases/{database}/indexes", 200),
  route("dropIndex", "DELETE", "/v1/projects/{project}/databases/{database}/indexes/{index}", 204),
  route("listIndexRecommendations", "GET", "/v1/projects/{project}/databases/{database}/index-recommendations", 200),
  route("applyIndexRecommendation", "POST", "/v1/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply", 201),
  route("getDatabaseSchema", "GET", "/v1/projects/{project}/databases/{database}/schema"),
  route("inferCollectionSchema", "GET", "/v1/projects/{project}/databases/{database}/schema/{collection}"),
  route("declareCollectionSchema", "PUT", "/v1/projects/{project}/databases/{database}/schema/{collection}"),
  route("listQueryShapes", "GET", "/v1/projects/{project}/databases/{database}/query-shapes"),
  route("createSavedQuery", "POST", saved, 201),
  route("listSavedQueries", "GET", saved),
  route("getSavedQuery", "GET", `${saved}/{query}`),
  route("updateSavedQuery", "PUT", `${saved}/{query}`),
  route("deleteSavedQuery", "DELETE", `${saved}/{query}`, 204),
]);

/** @param {ManagementOperation} operation @param {import("./routes.js").V1Route["method"]} method @param {string} pathTemplate @param {number} [status] @returns {import("./routes.js").V1Route} */
function route(operation, method, pathTemplate, status = 200) {
  const write = method !== "GET" && operation !== "planPointInTime";
  const webhook = pathTemplate.startsWith(webhooks);
  const noBody = ["createBackup", "applyIndexRecommendation", "cancelImport", "resumeImport"].includes(operation);
  return Object.freeze({ operation, method, pathTemplate,
    permission: webhook ? write ? "documents:write" : "documents:read" : operation === "getShapeManifest" || operation === "exportDatabase" || operation === "executeSql" || operation === "getCollectionTtl" ? "documents:read" : pathTemplate === ttl ? "documents:write" : pathTemplate.startsWith("/v1/platform/") ? "platform-admin" : !pathTemplate.startsWith("/v1/") ? "anonymous" : write ? "databases:write" : "databases:read",
    additionalPermissions: Object.freeze(webhook && write ? ["documents:read"] : ["createImport", "uploadImportSource", "updateImport", "startImport", "resumeImport"].includes(operation) ? ["documents:write"] : []),
    requestMediaTypes: Object.freeze(noBody ? [] : operation === "uploadImportSource" ? ["application/octet-stream"] : method === "POST" || method === "PUT" || method === "PATCH" ? ["application/json"] : []),
    responseMediaType: status === 204 ? "" : operation === "exportDatabase" ? "application/x-ndjson" : "application/json", successStatuses: Object.freeze(operation === "readyz" || operation === "backupz" ? [200, 503] : operation === "applyIndexRecommendation" || operation === "createImport" ? [200, 201] : [status]),
    idempotencyKey: "none", replaySafe: !write, contract: webhook ? "docs/terrabase/change-webhooks.md" : "docs/terrabase/api.md", contractText: Object.freeze([]),
  });
}
