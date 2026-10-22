/** @typedef {"getProjectUsage" | "getProjectAnalytics" | "getProjectReplication" | "getDatabaseUsage" | "getDatabaseAnalytics" | "getDatabaseReplication" | "createIndex" | "listIndexes" | "dropIndex" | "getAutoIndexPolicy" | "setAutoIndexPolicy" | "listIndexRecommendations" | "applyIndexRecommendation" | "getDatabaseSchema" | "inferCollectionSchema" | "declareCollectionSchema" | "listQueryShapes" | "createSavedQuery" | "listSavedQueries" | "getSavedQuery" | "updateSavedQuery" | "deleteSavedQuery"} ManagementOperation */
const saved = "/v1/projects/{project}/saved-queries";
/** @type {readonly import("./routes.js").V1Route[]} */
export const managementV1Routes = Object.freeze([
  route("getProjectUsage", "GET", "/v1/projects/{project}/usage"),
  route("getProjectAnalytics", "GET", "/v1/projects/{project}/analytics"),
  route("getProjectReplication", "GET", "/v1/projects/{project}/replication"),
  route("getDatabaseUsage", "GET", "/v1/projects/{project}/databases/{database}/usage"),
  route("getDatabaseAnalytics", "GET", "/v1/projects/{project}/databases/{database}/analytics"),
  route("getDatabaseReplication", "GET", "/v1/projects/{project}/databases/{database}/replication"),
  route("createIndex", "POST", "/v1/projects/{project}/databases/{database}/indexes", 201),
  route("listIndexes", "GET", "/v1/projects/{project}/databases/{database}/indexes", 200),
  route("dropIndex", "DELETE", "/v1/projects/{project}/databases/{database}/indexes/{index}", 204),
  route("getAutoIndexPolicy", "GET", "/v1/projects/{project}/databases/{database}/auto-index/{collection}", 200),
  route("setAutoIndexPolicy", "PUT", "/v1/projects/{project}/databases/{database}/auto-index/{collection}", 200),
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
  const write = method !== "GET";
  return Object.freeze({ operation, method, pathTemplate,
    permission: write ? "databases:write" : "databases:read",
    requestMediaTypes: Object.freeze(method === "POST" || method === "PUT" ? ["application/json"] : []),
    responseMediaType: status === 204 ? "" : "application/json", successStatuses: Object.freeze(operation === "applyIndexRecommendation" ? [200, 201] : [status]),
    idempotencyKey: "none", replaySafe: !write, contract: "docs/terrabase/api.md", contractText: Object.freeze([]),
  });
}
