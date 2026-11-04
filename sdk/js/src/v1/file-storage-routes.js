/** @typedef {"createFileBucket" | "listFileBuckets" | "setFileBucketRules" | "deleteFileBucket" | "createFileUpload" | "completeFileUpload" | "listBucketFiles" | "createFileDownload" | "deleteBucketFile"} FileStorageOperation */
const buckets = "/v1/projects/{project}/buckets";
const bucket = `${buckets}/{bucket}`;
const files = `${bucket}/files`;
const file = `${files}/{file}`;
/** @type {readonly import("./routes.js").V1Route[]} */
export const fileStorageRoutes = Object.freeze(/** @type {import("./routes.js").V1Route[]} */ ([
  { operation: "createFileBucket", method: "POST", pathTemplate: buckets, permission: "sql:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
  { operation: "listFileBuckets", method: "GET", pathTemplate: buckets, permission: "sql:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "setFileBucketRules", method: "PUT", pathTemplate: `${bucket}/rules`, permission: "sql:write", requestMediaTypes: ["application/json"], successStatuses: [200] },
  { operation: "deleteFileBucket", method: "DELETE", pathTemplate: bucket, permission: "sql:write", requestMediaTypes: [], successStatuses: [204] },
  { operation: "createFileUpload", method: "POST", pathTemplate: `${files}/upload`, permission: "sql:write", requestMediaTypes: ["application/json"], successStatuses: [201] },
  { operation: "completeFileUpload", method: "POST", pathTemplate: `${file}/complete`, permission: "sql:write", requestMediaTypes: [], successStatuses: [200] },
  { operation: "listBucketFiles", method: "GET", pathTemplate: files, permission: "sql:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "createFileDownload", method: "POST", pathTemplate: `${file}/download`, permission: "sql:read", requestMediaTypes: [], successStatuses: [200] },
  { operation: "deleteBucketFile", method: "DELETE", pathTemplate: file, permission: "sql:write", requestMediaTypes: [], successStatuses: [204] },
].map(route => Object.freeze({ ...route, responseMediaType: route.successStatuses.includes(204) ? "" : "application/json", idempotencyKey: "none", replaySafe: route.method === "GET", contract: "docs/file-buckets.md", contractText: [] }))));
