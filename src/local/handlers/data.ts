import {
  emptyResponse,
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalJsonObject,
  type LocalRequestContext,
} from "../context.js";
import type { TerraScaleSqliteStore } from "../store.js";

type Store = Pick<TerraScaleSqliteStore, "get" | "list" | "put" | "delete">;
type Entity = LocalJsonObject & { readonly id: string; readonly projectId: string };

type StoredDocument = {
  readonly value: unknown;
  readonly version: number;
  readonly revision: string;
  readonly updatedAt: string;
};

const documentKeyPrefix = "sdk-local/documents";

function documentScope(database: string, collection: string): string {
  return `${database}/${collection}`;
}

function documentEntry(
  store: Store,
  database: string,
  collection: string,
  id: string,
): StoredDocument | undefined {
  return store.get<StoredDocument>(
    documentKeyPrefix,
    documentScope(database, collection),
    id,
  );
}

function normalizeRevision(value: string): string {
  return value.replace(/^W\//u, "").replace(/^"|"$/gu, "");
}

function putDocumentEntry(
  store: Store,
  database: string,
  collection: string,
  id: string,
  value: unknown,
  now: string,
): StoredDocument {
  const current = documentEntry(store, database, collection, id);
  const version = (current?.version ?? 0) + 1;
  const stored: StoredDocument = {
    value,
    version,
    revision: `local-${version}`,
    updatedAt: now,
  };
  const scope = documentScope(database, collection);
  store.put(documentKeyPrefix, scope, id, stored);
  const ids = new Set(
    store.get<readonly string[]>(`${documentKeyPrefix}-index`, scope, "ids") ?? [],
  );
  ids.add(id);
  store.put(`${documentKeyPrefix}-index`, scope, "ids", [...ids]);
  return stored;
}

function removeDocumentEntry(
  store: Store,
  database: string,
  collection: string,
  id: string,
): boolean {
  const scope = documentScope(database, collection);
  const removed = store.delete(documentKeyPrefix, scope, id);
  if (removed) {
    const ids = new Set(
      store.get<readonly string[]>(`${documentKeyPrefix}-index`, scope, "ids") ?? [],
    );
    ids.delete(id);
    store.put(`${documentKeyPrefix}-index`, scope, "ids", [...ids]);
  }
  return removed;
}

function mergePatch(target: unknown, patch: unknown): unknown {
  if (patch === null || typeof patch !== "object" || Array.isArray(patch)) return patch;
  const base =
    target !== null && typeof target === "object" && !Array.isArray(target)
      ? { ...(target as Record<string, unknown>) }
      : {};
  for (const [key, value] of Object.entries(patch as Record<string, unknown>)) {
    if (value === null) delete base[key];
    else base[key] = mergePatch(base[key], value);
  }
  return base;
}

const keyPrefix = "sdk-local/data/";

/**
 * Implements the public data routes for the SDK's development transport.
 * The handler deliberately uses only the persistence methods exposed by the
 * local store. This keeps the route independent from the concrete SQLite
 * driver and makes all indexes survive a process restart.
 */
export async function handleLocalData(context: LocalRequestContext): Promise<Response | undefined> {
  const documents = matchLocalRoute(
    context,
    "GET",
    "/api/v1/databases/{name}/collections/{collection}/documents",
  );
  if (documents !== undefined) {
    return listDocuments(context, documents.name!, documents.collection!);
  }

  const document = matchLocalRoute(
    context,
    context.method,
    "/api/v1/databases/{name}/collections/{collection}/documents/{id}",
  );
  if (document !== undefined) {
    const locator = {
      database: document.name!,
      collection: document.collection!,
      id: document.id!,
    };
    if (context.method === "DELETE") return deleteDocument(context, locator);
    if (context.method === "PUT") return writeDocument(context, locator, context.body);
    if (context.method === "PATCH") {
      return writeDocument(context, locator, context.body, { merge: true });
    }
    if (context.method === "GET") return readDocument(context, locator);
  }

  const history = matchLocalRoute(
    context,
    "GET",
    "/api/v1/databases/{name}/collections/{collection}/documents/{id}/history",
  );
  if (history !== undefined) {
    return documentHistory(context, {
      database: history.name!,
      collection: history.collection!,
      id: history.id!,
    });
  }

  const sql = matchLocalRoute(context, "POST", "/api/v1/databases/{name}/sql");
  if (sql !== undefined) {
    return jsonResponse({ columns: [], rows: [], rowCount: 0 });
  }

  const databaseList = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases") as Readonly<Record<string, string>> | undefined;
  if (databaseList !== undefined) {
    return jsonResponse(await listDatabases(context, databaseList.projectId!));
  }
  const databaseCreate = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/databases") as Readonly<Record<string, string>> | undefined;
  if (databaseCreate !== undefined) {
    return jsonResponse(stripInternal(await createDatabase(context, databaseCreate.projectId!)), 201);
  }
  const databaseGet = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}") as Readonly<Record<string, string>> | undefined;
  if (databaseGet !== undefined) {
    return databaseResponse(await readDatabase(context, databaseGet.projectId!, databaseGet.databaseId!));
  }
  const databaseDelete = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/databases/{databaseId}") as Readonly<Record<string, string>> | undefined;
  if (databaseDelete !== undefined) {
    return deleteDatabase(context, databaseDelete.projectId!, databaseDelete.databaseId!);
  }

  const capability = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/database-branches/capability") as Readonly<Record<string, string>> | undefined;
  if (capability !== undefined) {
    return jsonResponse({
      projectId: capability.projectId!,
      admissionState: "unavailable",
      physicalCloneAvailable: false,
      promotionAvailable: false,
      mergeAvailable: false,
      detail: "Physical database branching is not available.",
    });
  }
  const clone = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/database-branches") as Readonly<Record<string, string>> | undefined;
  if (clone !== undefined) {
    return jsonResponse(
      { code: "database_branch_unavailable", message: "Physical database branching is unavailable." },
      503,
    );
  }

  return undefined;
}

type DocumentLocator = {
  readonly database: string;
  readonly collection: string;
  readonly id: string;
};

function listDocuments(context: LocalRequestContext, database: string, collection: string): Response {
  const params = context.url.searchParams;
  const parsedLimit = Number.parseInt(params.get("limit") ?? "100", 10);
  const limit = Math.min(
    Math.max(Number.isSafeInteger(parsedLimit) ? parsedLimit : 100, 1),
    1000,
  );
  const after = params.get("after");
  const scope = documentScope(database, collection);
  const ids = [
    ...(context.store.get<readonly string[]>(
      `${documentKeyPrefix}-index`,
      scope,
      "ids",
    ) ?? []),
  ].sort();
  const documents = [];
  for (const id of ids) {
    if (after !== null && after !== "" && id <= after) continue;
    const stored = documentEntry(context.store, database, collection, id);
    if (stored === undefined) continue;
    documents.push({ id, version: stored.version, value: stored.value });
    if (documents.length >= limit) break;
  }
  return jsonResponse({ documents });
}

function readDocument(context: LocalRequestContext, locator: DocumentLocator): Response {
  const stored = documentEntry(context.store, locator.database, locator.collection, locator.id);
  if (stored === undefined) {
    return problemResponse(404, "document_not_found", "The document was not found.");
  }
  return jsonResponse(stored.value, 200, { etag: `"${stored.revision}"` });
}

function writeDocument(
  context: LocalRequestContext,
  locator: DocumentLocator,
  body: unknown,
  options: { readonly merge?: boolean } = {},
): Response {
  if (body === undefined) {
    return problemResponse(400, "invalid_request_body", "A JSON document value is required.");
  }
  const current = documentEntry(context.store, locator.database, locator.collection, locator.id);
  const ifMatch = context.request.headers.get("if-match");
  if (ifMatch !== null && (current === undefined || normalizeRevision(ifMatch) !== current.revision)) {
    return problemResponse(
      412,
      "document_precondition_failed",
      "The document changed since the supplied revision.",
    );
  }
  const ifNoneMatch = context.request.headers.get("if-none-match")?.trim();
  if (ifNoneMatch === "*" && current !== undefined) {
    return problemResponse(
      412,
      "document_precondition_failed",
      "The document already exists.",
    );
  }
  const value =
    options.merge === true
      ? mergePatch(current?.value ?? {}, body)
      : body;
  const stored = putDocumentEntry(
    context.store,
    locator.database,
    locator.collection,
    locator.id,
    value,
    context.now,
  );
  return jsonResponse({ revision: stored.revision }, 200, {
    etag: `"${stored.revision}"`,
  });
}

function deleteDocument(context: LocalRequestContext, locator: DocumentLocator): Response {
  const removed = removeDocumentEntry(
    context.store,
    locator.database,
    locator.collection,
    locator.id,
  );
  if (!removed) {
    return problemResponse(404, "document_not_found", "The document was not found.");
  }
  return emptyResponse(204);
}

function documentHistory(context: LocalRequestContext, locator: DocumentLocator): Response {
  const stored = documentEntry(context.store, locator.database, locator.collection, locator.id);
  if (stored === undefined) {
    return problemResponse(404, "document_not_found", "The document was not found.");
  }
  return jsonResponse({ history: [{ revision: stored.revision, deleted: false }] });
}

async function listDatabases(context: LocalRequestContext, projectId: string): Promise<readonly Entity[]> {
  const ids = await read<string[]>(context.store, `${keyPrefix}database-index/${projectId}`) ?? [];
  const values = await Promise.all(ids.map((id) => read<Entity>(context.store, `${keyPrefix}database/${projectId}/${id}`)));
  return paginate(values.filter((value): value is Entity => value !== undefined).map(stripInternal), context);
}

async function createDatabase(context: LocalRequestContext, projectId: string): Promise<Entity> {
  const body = objectBody(context);
  const name = typeof body.name === "string" && body.name.trim() ? body.name.trim() : "Database";
  const id = createUlid();
  const value: Entity = {
    databaseId: id, projectId, id, name, regions: ["GRU"], backupRegion: "GRU",
    createdAt: context.now, version: 1,
  };
  await putEntity(context.store, `${keyPrefix}database/${projectId}/${id}`, value, `${keyPrefix}database-index/${projectId}`);
  return value;
}

async function readDatabase(context: LocalRequestContext, projectId: string, databaseId: string): Promise<Entity | undefined> {
  return read<Entity>(context.store, `${keyPrefix}database/${projectId}/${databaseId}`);
}

function databaseResponse(value: Entity | undefined): Response {
  return value === undefined ? problemResponse(404, "database_not_found", "Database was not found.") : jsonResponse(stripInternal(value));
}

async function deleteDatabase(context: LocalRequestContext, projectId: string, databaseId: string): Promise<Response> {
  const value = await readDatabase(context, projectId, databaseId);
  if (value === undefined) return problemResponse(404, "database_not_found", "Database was not found.");
  if (context.request.headers.get("x-terrascale-delete-confirmation") !== databaseId) {
    return problemResponse(400, "delete_confirmation_required", "Database deletion requires confirmation.");
  }
  await remove(context.store, `${keyPrefix}database/${projectId}/${databaseId}`, `${keyPrefix}database-index/${projectId}`, databaseId);
  return jsonResponse({ databaseId, projectId, name: value.name, deleted: true, physicalDatabasesDeleted: 1 });
}

function stripInternal(value: Entity): Entity { const { id: _id, ...rest } = value; return rest as Entity; }
function paginate<T>(items: readonly T[], context: LocalRequestContext): readonly T[] { const skip = nonnegativeInt(context.url.searchParams.get("skip")); const take = boundedTake(context.url.searchParams.get("take")); return items.slice(skip, skip + take); }
function nonnegativeInt(value: string | null): number { const parsed = Number(value ?? 0); return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : 0; }
function boundedTake(value: string | null): number { const parsed = Number(value ?? 100); return Number.isSafeInteger(parsed) && parsed > 0 ? Math.min(parsed, 1000) : 100; }

async function read<T>(store: Store, key: string): Promise<T | undefined> {
  const parts = storageParts(key);
  return store.get<T>(parts[0], parts[1], parts[2]);
}
async function write<T>(store: Store, key: string, value: T): Promise<void> {
  const parts = storageParts(key);
  store.put(parts[0], parts[1], parts[2], value);
}
async function remove(store: Store, key: string, indexKey?: string, id?: string): Promise<void> {
  const parts = storageParts(key);
  store.delete(parts[0], parts[1], parts[2]);
  if (indexKey !== undefined && id !== undefined) {
    const ids = await read<string[]>(store, indexKey) ?? [];
    await write(store, indexKey, ids.filter((item) => item !== id));
  }
}
async function putEntity(store: Store, key: string, value: Entity, indexKey?: string): Promise<void> { await write(store, key, value); if (indexKey !== undefined) { const id = value.id; const ids = await read<string[]>(store, indexKey) ?? []; if (!ids.includes(id)) await write(store, indexKey, [...ids, id]); } }
function storageParts(key: string): [string, string, string] {
  const parts = key.slice(keyPrefix.length).split("/");
  if (parts[0]?.endsWith("-index")) return [`sdk-local-${parts[0]}`, parts[1] ?? "global", "index"];
  return [`sdk-local-${parts[0] ?? "record"}`, parts[1] ?? "global", parts[2] ?? "record"];
}
function createUlid(): string { const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; const bytes = new Uint8Array(16); globalThis.crypto?.getRandomValues(bytes); let value = BigInt(Date.now()) << 80n; for (let index = 0; index < 10; index++) value |= BigInt(bytes[index] ?? Math.floor(Math.random() * 256)) << BigInt((9 - index) * 8); let output = ""; for (let index = 25; index >= 0; index--) { output += alphabet[Number(value >> BigInt(index * 5) & 31n)] ?? "0"; } return output; }
