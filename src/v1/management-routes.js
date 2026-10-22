/** @typedef {"createSavedQuery" | "listSavedQueries" | "getSavedQuery" | "updateSavedQuery" | "deleteSavedQuery"} ManagementOperation */
const saved = "/v1/projects/{project}/saved-queries";
/** @type {readonly import("./routes.js").V1Route[]} */
export const managementV1Routes = Object.freeze([
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
    responseMediaType: status === 204 ? "" : "application/json", successStatuses: Object.freeze([status]),
    idempotencyKey: "none", replaySafe: !write, contract: "docs/terrabase/api.md", contractText: Object.freeze([]),
  });
}
