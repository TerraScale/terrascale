import { createAuthPersistenceClient, type AuthPersistenceClient } from "./auth-persistence.js";
import { createTerraScaleClient, type TerraScaleClient } from "./factory.js";
import { createTerraScaleManagementClient, type TerraScaleManagementClient } from "./management.js";
import type { FetchLike } from "./config.js";
import { type TerraScaleBase, type TerraScaleRequestMetric, type TerraScaleRouteName } from "./http.js";

const sampleIds = { database: "primary", collection: "users", documentId: "ada-1" } as const;
// Placeholder names are producer-owned OpenAPI path parameter names. The
// SDK's method argument names are intentionally free to remain ergonomic.
const pathTemplateSegments = new Map<string, string>([
  [sampleIds.database, "{name}"],
  [sampleIds.collection, "{collection}"],
  [sampleIds.documentId, "{id}"],
]).set("SFO", "{regionCode}").set("email", "{field}").set("export-1", "{job_id}").set("import-1", "{id}").set("delivery-1", "{delivery_id}").set("manifest.json", "{member}").set("operation-1", "{operation_id}").set("auth-user", "{model}");

/** Canonical producer operations not implemented by this SDK slice. */
export const expectedContractGapRouteNames = [
  "databases.authorizationLease",
  "databases.rollbackSchema",
  "databases.websocket",
  "health.health",
] as const;

/** Operations exercised by the real reactive TCP journey. */
export const reactiveTransportRouteNames = [
  "reactive.attachView",
  "reactive.viewLog",
  "reactive.snapshot",
  "reactive.viewWebsocket",
] as const;

export type ContractGapExpectation = {
  readonly code: "contract_gap";
  readonly routeName: (typeof expectedContractGapRouteNames)[number];
};

export const contractGapExpectations: readonly ContractGapExpectation[] =
  expectedContractGapRouteNames.map((routeName) => ({ code: "contract_gap", routeName }));

export type CapturedSdkRoute = {
  readonly base: TerraScaleBase;
  readonly client: string;
  readonly method: string;
  readonly operation: string;
  readonly pathTemplate: string;
  readonly routeName: TerraScaleRouteName;
  readonly sourceContract: {
    readonly body: { readonly kind: "void" } | { readonly kind: "json"; readonly shape: unknown };
    readonly headers: readonly { readonly name: string; readonly value: string }[];
    readonly query: readonly { readonly name: string; readonly value: string }[];
  };
};

type CaptureOperation = {
  readonly client: string;
  readonly invoke: (client: TerraScaleClient, management: TerraScaleManagementClient, auth: AuthPersistenceClient) => Promise<unknown>;
  readonly operation: string;
  readonly routeName: TerraScaleRouteName;
};

export async function capturePublicClientRoutes(): Promise<readonly CapturedSdkRoute[]> {
  const routes = await Promise.all(captureOperations.map((operation) => captureRoute(operation)));
  return routes.sort((left, right) => `${left.pathTemplate} ${left.method}`.localeCompare(`${right.pathTemplate} ${right.method}`));
}

export async function exercisePublicClientRoutes(fetch: FetchLike, onResult?: (routeName: TerraScaleRouteName, result: unknown) => void, routeNames?: readonly TerraScaleRouteName[]): Promise<readonly CapturedSdkRoute[]> {
  const routes: CapturedSdkRoute[] = [];
  for (const operation of captureOperations) {
    if (routeNames !== undefined && !routeNames.includes(operation.routeName)) continue;
    routes.push(await captureRoute(operation, fetch, true, onResult));
  }
  return routes;
}

async function captureRoute(operation: CaptureOperation, routeFetch?: FetchLike, assertRouted = false, onResult?: (routeName: TerraScaleRouteName, result: unknown) => void): Promise<CapturedSdkRoute> {
  let capturedRequest: Request | undefined;
  let capturedMetric: TerraScaleRequestMetric | undefined;
  const captureFetch: FetchLike = async (input) => {
    const request = input instanceof Request ? input : new Request(input);
    capturedRequest = request.clone();
    return routeFetch === undefined
      ? new Response("{}", { status: 200, headers: { "content-type": "application/json" } })
      : routeFetch(request);
  };
  const config = {
    apiBaseUrl: "https://api.terrascale.test",
    fetch: captureFetch,
    maxRetryAttempts: 0,
    requestMetricRecorder: (metric: TerraScaleRequestMetric): void => { capturedMetric = metric; },
  };
  const client = createTerraScaleClient(config);
  const management = createTerraScaleManagementClient(config);
  const auth = createAuthPersistenceClient(config);
  const result = await operation.invoke(client, management, auth);
  onResult?.(operation.routeName, result);
  if (assertRouted && onResult === undefined && isRoutingFailure(result)) {
    throw new Error(`Local route exercise failed for ${operation.client}.${operation.operation}: ${result.error.code} (${result.error.kind}).`);
  }
  if (capturedRequest === undefined) throw new Error(`Route capture for ${operation.client}.${operation.operation} made no request.`);
  if (capturedMetric === undefined) throw new Error(`Route capture for ${operation.client}.${operation.operation} recorded no route metric.`);
  if (capturedMetric.routeName !== operation.routeName) throw new Error(`Route metric mismatch for ${operation.operation}.`);
  return {
    base: capturedMetric.base,
    client: operation.client,
    method: capturedRequest.method,
    operation: operation.operation,
    pathTemplate: pathToTemplate(new URL(capturedRequest.url).pathname).replace(/\{name\}(?=\/auth\/)/u, "{database}"),
    routeName: operation.routeName,
    sourceContract: await capturedSourceContract(capturedRequest),
  };
}

function isRoutingFailure(value: unknown): value is { readonly error: { readonly code: string; readonly kind: string }; readonly ok: false } {
  if (typeof value !== "object" || value === null || !("ok" in value) || value.ok !== false) return false;
  if (!("error" in value) || typeof value.error !== "object" || value.error === null) return true;
  const error = value.error as { readonly code?: unknown; readonly kind?: unknown };
  return error.code === "route_not_found" || error.kind === "invalid-response" || error.kind === "server" || error.kind === "transport";
}

async function capturedSourceContract(request: Request): Promise<CapturedSdkRoute["sourceContract"]> {
  const text = await request.clone().text();
  return {
    body: text.length === 0 ? { kind: "void" } : { kind: "json", shape: JSON.parse(text) },
    headers: canonicalContractHeaders(request.headers),
    query: [...new URL(request.url).searchParams].map(([name, value]) => contractPair(name, value)).sort(compareContractPair),
  };
}

const safeContractValueNames = new Set([
  "accept", "content-type", "cursor", "idempotency-key", "latitude", "limit", "longitude", "organizationid", "range", "skip", "take", "unreadonly", "x-terrascale-delete-confirmation", "x-terrascale-project-id",
]);

export function canonicalContractHeaders(headers: Headers): readonly { readonly name: string; readonly value: string }[] {
  return [...headers].map(([name, value]) => contractPair(name, value)).sort(compareContractPair);
}

function contractPair(name: string, value: string): { readonly name: string; readonly value: string } {
  return { name, value: safeContractValueNames.has(name.toLowerCase()) ? value : "<redacted>" };
}

function compareContractPair(left: { readonly name: string; readonly value: string }, right: { readonly name: string; readonly value: string }): number {
  return left.name.localeCompare(right.name) || left.value.localeCompare(right.value);
}

function pathToTemplate(pathname: string): string {
  for (const [value, template] of pathTemplateSegments) pathname = pathname.replaceAll(value, template);
  return pathname;
}

const captureOperations: readonly CaptureOperation[] = [
  {
    client: "databases", operation: "insertUnique", routeName: "databases.insertUnique",
    invoke: (_client, _management, auth) => auth.insertUnique(sampleIds.database, { model: "user", record: { id: sampleIds.documentId } }, { idempotencyKey: "auth-insert-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "compareAndSwap", routeName: "databases.compareAndSwap",
    invoke: (_client, _management, auth) => auth.compareAndSwap(sampleIds.database, { model: "user", id: sampleIds.documentId, expected: { active: false }, update: { active: true } }, { idempotencyKey: "auth-cas-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "consumeOne", routeName: "databases.consumeOne",
    invoke: (_client, _management, auth) => auth.consumeOne(sampleIds.database, { model: "verification", id: sampleIds.documentId, expected: {} }, { idempotencyKey: "auth-consume-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "index", routeName: "databases.index",
    invoke: (_client, _management, auth) => auth.index(sampleIds.database, { model: "user", fields: ["email"] }, { idempotencyKey: "auth-index-1" }),
  },
  {
    client: "databases", operation: "findOne", routeName: "databases.findOne",
    invoke: (_client, _management, auth) => auth.findOne(sampleIds.database, "auth-user", sampleIds.documentId),
  },
  {
    client: "databases", operation: "findPage", routeName: "databases.findPage",
    invoke: (_client, _management, auth) => auth.findPage(sampleIds.database, { model: "user", predicates: [{ field: "active", value: true }], offset: 0, limit: 25 }, { idempotencyKey: "auth-page-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "count", routeName: "databases.count",
    invoke: (_client, _management, auth) => auth.count(sampleIds.database, { model: "user", predicates: [{ field: "active", value: true }] }),
  },
  {
    client: "databases", operation: "updateOne", routeName: "databases.updateOne",
    invoke: (_client, _management, auth) => auth.updateOne(sampleIds.database, { model: "user", predicates: [{ field: "id", value: sampleIds.documentId }], update: { active: true } }, { idempotencyKey: "auth-update-one-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "updateMany", routeName: "databases.updateMany",
    invoke: (_client, _management, auth) => auth.updateMany(sampleIds.database, { model: "user", predicates: [{ field: "active", value: false }], update: { active: true } }, { idempotencyKey: "auth-update-many-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "deleteOne", routeName: "databases.deleteOne",
    invoke: (_client, _management, auth) => auth.deleteOne(sampleIds.database, { model: "user", predicates: [{ field: "id", value: sampleIds.documentId }] }, { idempotencyKey: "auth-delete-one-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "deleteMany", routeName: "databases.deleteMany",
    invoke: (_client, _management, auth) => auth.deleteMany(sampleIds.database, { model: "user", predicates: [{ field: "active", value: false }] }, { idempotencyKey: "auth-delete-many-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "incrementOne", routeName: "databases.incrementOne",
    invoke: (_client, _management, auth) => auth.incrementOne(sampleIds.database, { model: "user", predicates: [{ field: "id", value: sampleIds.documentId }], increment: { attempts: 1 }, set: { active: true } }, { idempotencyKey: "auth-increment-one-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "consumeMatching", routeName: "databases.consumeMatching",
    invoke: (_client, _management, auth) => auth.consumeMatching(sampleIds.database, { model: "verification", predicates: [{ field: "id", value: sampleIds.documentId }] }, { idempotencyKey: "auth-consume-matching-1", transactionId: "tx-1" }),
  },
  {
    client: "databases", operation: "beginTransaction", routeName: "databases.beginTransaction",
    invoke: (_client, _management, auth) => auth.beginTransaction(sampleIds.database, { transactionId: "tx-1" }, { idempotencyKey: "auth-begin-1" }),
  },
  {
    client: "databases", operation: "commitTransaction", routeName: "databases.commitTransaction",
    invoke: (_client, _management, auth) => auth.commitTransaction(sampleIds.database, { transactionId: "tx-1" }, { idempotencyKey: "auth-commit-1" }),
  },
  {
    client: "databases", operation: "rollbackTransaction", routeName: "databases.rollbackTransaction",
    invoke: (_client, _management, auth) => auth.rollbackTransaction(sampleIds.database, { transactionId: "tx-1" }, { idempotencyKey: "auth-rollback-1" }),
  },

  {
    client: "databases", operation: "listExports", routeName: "databases.listExports",
    invoke: (_client, management) => management.exports.list(sampleIds.database, { limit: 25, after: "export-previous" }),
  },
  {
    client: "databases", operation: "cancelExport", routeName: "databases.cancelExport",
    invoke: (_client, management) => management.exports.cancel(sampleIds.database, "export-1"),
  },
  {
    client: "databases", operation: "getProvisioningOperation", routeName: "databases.getProvisioningOperation",
    invoke: (_client, management) => management.operations.get("operation-1"),
  },
  {
    client: "databases", operation: "deletionStatus", routeName: "databases.deletionStatus",
    invoke: (_client, management) => management.databases.deletionStatus(sampleIds.database, "operation-1"),
  },
  {
    client: "databases", operation: "listOperations", routeName: "databases.listOperations",
    invoke: (_client, management) => management.operations.list({ limit: 25, after: "operation-previous" }),
  },
  {
    client: "databases", operation: "developmentFault", routeName: "databases.developmentFault",
    invoke: (_client, management) => management.development.setFault(sampleIds.database, { confirmBranch: sampleIds.database, fault: "latency", durationMs: 1000, maxOperations: 1, delayMs: 25 }),
  },
  {
    client: "databases", operation: "clearDevelopmentFault", routeName: "databases.clearDevelopmentFault",
    invoke: (_client, management) => management.development.clearFault(sampleIds.database),
  },
  {
    client: "databases", operation: "developmentLogs", routeName: "databases.developmentLogs",
    invoke: (_client, management) => management.development.logs(sampleIds.database, { limit: 25 }),
  },
  {
    client: "databases", operation: "rotateSecret", routeName: "databases.rotateSecret",
    invoke: (_client, management) => management.webhooks.rotateSecret(sampleIds.database, sampleIds.documentId, { overlap_seconds: 300 }),
  },
  {
    client: "databases", operation: "downloadExport", routeName: "databases.downloadExport",
    invoke: (_client, management) => management.exports.download(sampleIds.database, "export-1", "manifest.json"),
  },
  {
    client: "databases", operation: "deleteIndex", routeName: "databases.deleteIndex",
    invoke: (_client, management) => management.collections.deleteIndex(sampleIds.database, sampleIds.collection, "email"),
  },
  {
    client: "databases", operation: "indexRecommendations", routeName: "databases.indexRecommendations",
    invoke: (_client, management) => management.collections.recommendations(sampleIds.database, sampleIds.collection, { limit: 25 }),
  },
  {
    client: "databases", operation: "databaseUsage", routeName: "databases.databaseUsage",
    invoke: (_client, management) => management.usage.getDatabase(sampleIds.database),
  },
  {
    client: "databases", operation: "getSchema", routeName: "databases.getSchema",
    invoke: (_client, management) => management.schemas.get(sampleIds.database, sampleIds.collection),
  },
  {
    client: "databases", operation: "putSchema", routeName: "databases.putSchema",
    invoke: (_client, management) => management.schemas.put(sampleIds.database, sampleIds.collection, { name: sampleIds.collection, fields: [], historyRetentionDays: 30, mergePolicy: "remove-wins" }, { idempotencyKey: "schema-apply-1", ifMatch: '"1"' }),
  },
  {
    client: "databases", operation: "listWebhooks", routeName: "databases.listWebhooks",
    invoke: (_client, management) => management.webhooks.list(sampleIds.database),
  },
  {
    client: "databases", operation: "createWebhook", routeName: "databases.createWebhook",
    invoke: (_client, management) => management.webhooks.create(sampleIds.database, { endpoint: "https://hooks.example.test/events" }, { idempotencyKey: "webhook-create-1" }),
  },
  {
    client: "databases", operation: "deleteWebhook", routeName: "databases.deleteWebhook",
    invoke: (_client, management) => management.webhooks.delete(sampleIds.database, sampleIds.documentId),
  },
  {
    client: "databases", operation: "listDeliveries", routeName: "databases.listDeliveries",
    invoke: (_client, management) => management.webhooks.deliveries(sampleIds.database, sampleIds.documentId),
  },
  {
    client: "databases", operation: "replayWebhook", routeName: "databases.replayWebhook",
    invoke: (_client, management) => management.webhooks.replay(sampleIds.database, sampleIds.documentId, "delivery-1"),
  },
  {
    client: "databases", operation: "createExport", routeName: "databases.createExport",
    invoke: (_client, management) => management.exports.create(sampleIds.database, { idempotency_key: "export-create-1" }),
  },
  {
    client: "databases", operation: "getExport", routeName: "databases.getExport",
    invoke: (_client, management) => management.exports.get(sampleIds.database, "export-1"),
  },
  {
    client: "databases", operation: "resumeExport", routeName: "databases.resumeExport",
    invoke: (_client, management) => management.exports.resume(sampleIds.database, "export-1", { resume_cursor: "resume-1" }),
  },
  {
    client: "databases", operation: "listImports", routeName: "databases.listImports",
    invoke: (_client, management) => management.imports.list(sampleIds.database),
  },
  {
    client: "databases", operation: "createImport", routeName: "databases.createImport",
    invoke: (_client, management) => management.imports.create(sampleIds.database, { format: "jsonl", idempotency_key: "import-1" }),
  },
  {
    client: "databases", operation: "getImport", routeName: "databases.getImport",
    invoke: (_client, management) => management.imports.get(sampleIds.database, "import-1"),
  },
  {
    client: "databases", operation: "uploadImport", routeName: "databases.uploadImport",
    invoke: (_client, management) => management.imports.upload(sampleIds.database, "import-1", new Uint8Array([0])),
  },
  {
    client: "databases", operation: "resumeImport", routeName: "databases.resumeImport",
    invoke: (_client, management) => management.imports.resume(sampleIds.database, "import-1"),
  },

  {
    client: "databases", operation: "listDatabases", routeName: "databases.listDatabases",
    invoke: (_client, management) => management.databases.list(),
  },
  {
    client: "databases", operation: "createDatabase", routeName: "databases.createDatabase",
    invoke: (_client, management) => management.databases.create({ name: sampleIds.database, placement: ["GRU"], residency: "br-only", operation_id: "create-primary-1" }),
  },
  {
    client: "databases", operation: "getDatabase", routeName: "databases.getDatabase",
    invoke: (_client, management) => management.databases.get(sampleIds.database),
  },
  {
    client: "databases", operation: "deleteDatabase", routeName: "databases.deleteDatabase",
    invoke: (_client, management) => management.databases.delete(sampleIds.database, { confirm: true, idempotencyKey: "delete-primary" }),
  },
  {
    client: "databases", operation: "restoreDatabase", routeName: "databases.restoreDatabase",
    invoke: (_client, management) => management.databases.restore(sampleIds.database),
  },
  {
    client: "databases", operation: "listBackups", routeName: "databases.listBackups",
    invoke: (_client, management) => management.backups.listBackups(sampleIds.database),
  },
  {
    client: "databases", operation: "restoreBackup", routeName: "databases.restoreBackup",
    invoke: (_client, management) => management.backups.restoreBackup(sampleIds.database, { name: "restored", operation_id: "operation-restore", at: 123 }),
  },
  {
    client: "databases", operation: "listBranches", routeName: "databases.listBranches",
    invoke: (_client, management) => management.branches.list(sampleIds.database),
  },
  {
    client: "databases", operation: "createBranch", routeName: "databases.createBranch",
    invoke: (_client, management) => management.branches.create(sampleIds.database, { name: "branch", operation_id: "operation-branch", at: 123 }),
  },
  {
    client: "databases", operation: "listCollections", routeName: "databases.listCollections",
    invoke: (_client, management) => management.collections.list(sampleIds.database),
  },
  {
    client: "databases", operation: "listIndexes", routeName: "databases.listIndexes",
    invoke: (_client, management) => management.collections.listIndexes(sampleIds.database, sampleIds.collection),
  },
  {
    client: "databases", operation: "createIndex", routeName: "databases.createIndex",
    invoke: (_client, management) => management.collections.createIndex(sampleIds.database, sampleIds.collection, { field: "email" }),
  },
  {
    client: "databases", operation: "documentHistory", routeName: "databases.documentHistory",
    invoke: (client) => client.documents.history(sampleIds.database, sampleIds.collection, sampleIds.documentId, { limit: 25, after: "cursor-1" }),
  },
  {
    client: "databases", operation: "batchDocuments", routeName: "databases.batchDocuments",
    invoke: (client) => client.documents.batch(sampleIds.database, sampleIds.collection, { operations: [{ op: "put", id: sampleIds.documentId, value: { active: true } }] }),
  },

  {
    client: "databases", operation: "listDocuments", routeName: "databases.listDocuments",
    invoke: (client) => client.documents.list(sampleIds.database, sampleIds.collection, { limit: 25, after: "cursor-1" }),
  },
  {
    client: "databases", operation: "getDocument", routeName: "databases.getDocument",
    invoke: (client) => client.documents.get(sampleIds.database, sampleIds.collection, sampleIds.documentId),
  },
  {
    client: "databases", operation: "patchDocument", routeName: "databases.patchDocument",
    invoke: (client) => client.documents.patch(sampleIds.database, sampleIds.collection, sampleIds.documentId, { active: true }),
  },
  {
    client: "databases", operation: "putDocument", routeName: "databases.putDocument",
    invoke: (client) => client.documents.put(sampleIds.database, sampleIds.collection, sampleIds.documentId, { active: true }),
  },
  {
    client: "databases", operation: "deleteDocument", routeName: "databases.deleteDocument",
    invoke: (client) => client.documents.delete(sampleIds.database, sampleIds.collection, sampleIds.documentId),
  },
  {
    client: "databases", operation: "executeSql", routeName: "databases.executeSql",
    invoke: (client) => client.sql.execute(sampleIds.database, { sql: "SELECT * FROM users WHERE active = :active", parameters: { active: true } }),
  },
  {
    client: "regions", operation: "list", routeName: "regions.list",
    invoke: (client) => client.regions.list(),
  },
];
