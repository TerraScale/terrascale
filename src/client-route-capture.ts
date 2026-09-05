import { createTerraScaleClient, type TerraScaleClient } from "./factory.js";
import type { FetchLike } from "./config.js";
import { type TerraScaleBase, type TerraScaleRequestMetric, type TerraScaleRouteName } from "./http.js";

const sampleIds = { database: "primary", collection: "users", documentId: "ada-1" } as const;
// Placeholder names are producer-owned OpenAPI path parameter names. The
// SDK's method argument names are intentionally free to remain ergonomic.
const pathTemplateSegments = new Map<string, string>([
  [sampleIds.database, "{name}"],
  [sampleIds.collection, "{collection}"],
  [sampleIds.documentId, "{id}"],
]).set("SFO", "{regionCode}");

/** Canonical producer operations not implemented by this SDK slice. */
export const expectedContractGapRouteNames = [
  "databases.listDatabases",
  "databases.createDatabase",
  "databases.deleteDatabase",
  "databases.getDatabase",
  "databases.listBackups",
  "databases.restoreBackup",
  "databases.listBranches",
  "databases.createBranch",
  "databases.listCollections",
  "databases.documentHistory",
  "databases.batchDocuments",
  "databases.listIndexes",
  "databases.createIndex",
  "databases.websocket",
  "databases.restoreDatabase",
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
  readonly invoke: (client: TerraScaleClient) => Promise<unknown>;
  readonly operation: string;
  readonly routeName: TerraScaleRouteName;
};

export async function capturePublicClientRoutes(): Promise<readonly CapturedSdkRoute[]> {
  const routes = await Promise.all(captureOperations.map((operation) => captureRoute(operation)));
  return routes.sort((left, right) => `${left.pathTemplate} ${left.method}`.localeCompare(`${right.pathTemplate} ${right.method}`));
}

export async function exercisePublicClientRoutes(fetch: FetchLike): Promise<readonly CapturedSdkRoute[]> {
  const routes: CapturedSdkRoute[] = [];
  for (const operation of captureOperations) routes.push(await captureRoute(operation, fetch, true));
  return routes;
}

async function captureRoute(operation: CaptureOperation, routeFetch?: FetchLike, assertRouted = false): Promise<CapturedSdkRoute> {
  let capturedRequest: Request | undefined;
  let capturedMetric: TerraScaleRequestMetric | undefined;
  const captureFetch: FetchLike = async (input) => {
    const request = input instanceof Request ? input : new Request(input);
    capturedRequest = request.clone();
    return routeFetch === undefined
      ? new Response("{}", { status: 200, headers: { "content-type": "application/json" } })
      : routeFetch(request);
  };
  const client = createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    fetch: captureFetch,
    requestMetricRecorder: (metric: TerraScaleRequestMetric): void => { capturedMetric = metric; },
  });
  const result = await operation.invoke(client);
  if (assertRouted && isRoutingFailure(result)) {
    throw new Error(`Local route exercise failed for ${operation.client}.${operation.operation}: ${result.error.code} (${result.error.kind}).`);
  }
  if (capturedRequest === undefined) throw new Error(`Route capture for ${operation.client}.${operation.operation} made no request.`);
  if (capturedMetric === undefined) throw new Error(`Route capture for ${operation.client}.${operation.operation} recorded no route metric.`);
  return {
    base: capturedMetric.base,
    client: operation.client,
    method: capturedRequest.method,
    operation: operation.operation,
    pathTemplate: pathToTemplate(new URL(capturedRequest.url).pathname),
    // Legacy dashboard wrappers use labels that predate the producer manifest.
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
    client: "regions", operation: "regions", routeName: "regions.regions",
    invoke: (client) => client.regions.list(),
  },
];
