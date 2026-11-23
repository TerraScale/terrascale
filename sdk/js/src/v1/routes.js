/** @typedef {"GET" | "PUT" | "POST" | "PATCH" | "DELETE"} V1Method */
/** @typedef {"read" | "write" | "anonymous"} V1Access */
/** @typedef {[V1Method, string, (number | readonly number[])?, V1Access?, string?]} V1RouteEntry */

const project = "/v1/projects/{project}";
const database = `${project}/databases/{database}`;
const bucket = `${project}/buckets/{bucket}`;
const file = `${bucket}/files/{file}`;
const rows = `${database}/tables/{table}/rows`;
const shape = `${database}/tables/{table}/shape`;
const fn = `${database}/functions/{function}`;
const rules = `${database}/rules`;
const pointInTime = `${database}/point-in-time`;

const table = /** @satisfies {Record<string, V1RouteEntry>} */ ({
  healthz: ["GET", "/healthz", 200, "anonymous"],
  readyz: ["GET", "/readyz", [200, 503], "anonymous"],
  backupz: ["GET", "/backupz", [200, 503], "anonymous"],
  listRegions: ["GET", "/v1/regions", 200, "anonymous"],
  listPlatformDatabases: ["GET", "/v1/platform/databases"],

  createDatabase: ["POST", `${project}/databases`, 201],
  listDatabases: ["GET", `${project}/databases`],
  getDatabase: ["GET", database],
  updateDatabase: ["PATCH", database],
  deleteDatabase: ["DELETE", database, 204],
  deleteDatabaseIfId: ["DELETE", `${database}/if-id/{expectedId}`, 204],
  branchDatabase: ["POST", `${database}/branches`, 201],
  branchDatabaseIfId: ["POST", `${database}/branches/if-id/{expectedId}`, 201],
  setDatabaseTtl: ["PUT", `${database}/ttl`],
  clearDatabaseTtl: ["DELETE", `${database}/ttl`],
  getDatabaseRegions: ["GET", `${database}/regions`],

  executeSql: ["POST", `${database}/sql/execute`, 200, "read"],
  connectSql: ["GET", `${database}/sql/ws`, 101],
  exportDatabase: ["GET", `${database}/export`, 200, "read", "text/plain"],

  getRow: ["GET", `${rows}/{key}`],
  createRow: ["POST", rows, 201],
  updateRow: ["PATCH", `${rows}/{key}`],
  deleteRow: ["DELETE", `${rows}/{key}`, 204],
  batchRows: ["POST", `${database}/rows:batch`],

  readTableShape: ["GET", shape, [200, 307]],
  getShapeManifest: ["GET", `${shape}/manifest`],
  getShapeKey: ["GET", `${shape}/keys`],
  connectShapeHints: ["GET", `${shape}/ws`, 101],

  createBackup: ["POST", `${database}/backups`, 201],
  listBackups: ["GET", `${database}/backups`],
  restoreBackup: ["POST", `${database}/backups/{backup}/restore`, 202],
  getRestoreOperation: ["GET", `${database}/restores/{operation}`],
  getBackupPolicy: ["GET", `${database}/backup-policy`],
  setBackupPolicy: ["PUT", `${database}/backup-policy`],
  getRestoreWindow: ["GET", pointInTime],
  planPointInTime: ["POST", `${pointInTime}/plan`, 200, "read"],
  restorePointInTime: ["POST", `${pointInTime}/restore`, 202],
  rewindPointInTime: ["POST", `${pointInTime}/rewind`],
  getBackupDestination: ["GET", `${project}/backup-destination`],
  setBackupDestination: ["PUT", `${project}/backup-destination`],
  deleteBackupDestination: ["DELETE", `${project}/backup-destination`, 204],

  getProjectUsage: ["GET", `${project}/usage`],
  getProjectAnalytics: ["GET", `${project}/analytics`],
  getProjectMetrics: ["GET", `${project}/metrics`, 200, "read", "text/plain"],
  getDatabaseUsage: ["GET", `${database}/usage`],
  getDatabaseAnalytics: ["GET", `${database}/analytics`],
  listDatabaseLogs: ["GET", `${database}/logs`],

  listFunctions: ["GET", `${database}/functions`],
  getFunction: ["GET", fn],
  deployFunction: ["PUT", fn],
  deleteFunction: ["DELETE", fn, 204],
  invokeFunction: ["POST", `${fn}/invoke`, [200, 201, 202, 203, 204, 205, 206, 207, 208, 226], "read", ""],
  listFunctionLogs: ["GET", `${fn}/logs`],
  getFunctionStats: ["GET", `${fn}/stats`],

  getDatabaseRules: ["GET", rules],
  putDatabaseRules: ["PUT", rules],
  testDatabaseRules: ["POST", `${rules}:test`, 200, "read"],
  listDatabaseRulesVersions: ["GET", `${rules}/versions`],
  getDatabaseRulesVersion: ["GET", `${rules}/versions/{version}`],
  restoreDatabaseRulesVersion: ["POST", `${rules}/versions/{version}/restore`],

  createFileBucket: ["POST", `${project}/buckets`, 201],
  listFileBuckets: ["GET", `${project}/buckets`],
  setFileBucketRules: ["PUT", `${bucket}/rules`],
  deleteFileBucket: ["DELETE", bucket, 204],
  createFileUpload: ["POST", `${bucket}/files/upload`, 201],
  completeFileUpload: ["POST", `${file}/complete`],
  listBucketFiles: ["GET", `${bucket}/files`],
  createFileDownload: ["POST", `${file}/download`, 200, "read"],
  deleteBucketFile: ["DELETE", file, 204],
});

/** @typedef {keyof typeof table} V1Operation */
/**
 * @typedef {object} V1Route
 * @property {V1Operation} operation
 * @property {V1Method} method
 * @property {string} pathTemplate
 * @property {V1Access} access
 * @property {string} accept
 * @property {readonly number[]} successStatuses
 */

/** @type {ReadonlyMap<string, V1Route>} */
const routes = new Map(Object.entries(table).map(([operation, entry]) => {
  const [method, pathTemplate, status = 200, access = method === "GET" ? "read" : "write", accept] = /** @type {V1RouteEntry} */ (entry);
  const successStatuses = Object.freeze(typeof status === "number" ? [status] : [...status]);
  const bodyless = successStatuses.length === 1 && (successStatuses[0] === 204 || successStatuses[0] === 101);
  return [operation, Object.freeze({ operation: /** @type {V1Operation} */ (operation), method, pathTemplate, access,
    accept: accept ?? (bodyless ? "" : "application/json"), successStatuses })];
}));

/** @type {readonly V1Route[]} */
export const v1Routes = Object.freeze([...routes.values()]);

/** @param {V1Operation} operation @returns {V1Route} */
export function v1Route(operation) {
  const route = routes.get(operation);
  if (route === undefined) throw new TypeError(`Unknown v1 operation ${operation}.`);
  return route;
}

/** @param {V1Route} route @param {Readonly<Record<string, string>>} params @returns {string} */
export function v1RoutePath(route, params) {
  return route.pathTemplate.replace(/\{([a-zA-Z_]+)\}/gu, (_match, name) => {
    const value = params[name];
    if (value === undefined) throw new TypeError(`Missing path parameter ${name}.`);
    return encodeURIComponent(value);
  });
}
