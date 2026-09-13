// Snippet runner: executes every docs/snippets/*.ts against a real sandbox
// branch by default. --sandbox=stub selects the deterministic local harness;
// remote mode never falls back to the stub.
import { readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { pathToFileURL, fileURLToPath } from "node:url";
import { createStubSandbox } from "./stub-sandbox.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const snippetsDir = join(root, "docs", "snippets");

function requestedMode() {
  const requested = process.argv
    .filter((arg) => arg === "--sandbox=stub" || arg === "--sandbox=remote")
    .map((arg) => arg.slice("--sandbox=".length));
  if (new Set(requested).size > 1) {
    throw new Error("choose exactly one snippet mode: --sandbox=stub or --sandbox=remote");
  }
  return requested[0] ?? "remote";
}

// Decode the indexed `terrascale.sync.v1.msgpack` envelope (protocol 1.0).
// This intentionally duplicates only the wire boundary needed by the runner;
// projected values remain canonical JSON bytes and are never treated as the
// retired `{t,id,body}` JSON frame shape.
class MsgpackCursor {
  constructor(bytes) {
    this.bytes = bytes;
    this.offset = 0;
    this.depth = 0;
  }

  take(length) {
    const end = this.offset + length;
    if (end > this.bytes.length) throw new Error("truncated MessagePack response");
    const value = this.bytes.subarray(this.offset, end);
    this.offset = end;
    return value;
  }

  u8() { return this.take(1)[0]; }
  text(length) { return new TextDecoder().decode(this.take(length)); }
  view(bytes) { return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength); }

  integer64(value) {
    return value;
  }

  array(length) {
    if (length > this.bytes.length - this.offset) throw new Error("truncated MessagePack array");
    return Array.from({ length }, () => this.value());
  }

  value() {
    if (this.depth >= 64) throw new Error("MessagePack nesting limit exceeded");
    this.depth += 1;
    try { return this.readValue(); } finally { this.depth -= 1; }
  }

  readValue() {
    const tag = this.u8();
    if (tag <= 0x7f) return tag;
    if (tag >= 0xe0) return tag - 0x100;
    if ((tag & 0xe0) === 0xa0) return this.text(tag & 0x1f);
    if ((tag & 0xf0) === 0x90) return this.array(tag & 0x0f);
    if ((tag & 0xf0) === 0x80) return this.map(tag & 0x0f);
    switch (tag) {
      case 0xc0: return null;
      case 0xc2: return false;
      case 0xc3: return true;
      case 0xcc: return this.u8();
      case 0xcd: return this.view(this.take(2)).getUint16(0);
      case 0xce: return this.view(this.take(4)).getUint32(0);
      case 0xcf: return this.integer64(this.view(this.take(8)).getBigUint64(0));
      case 0xd0: return this.view(this.take(1)).getInt8(0);
      case 0xd1: return this.view(this.take(2)).getInt16(0);
      case 0xd2: return this.view(this.take(4)).getInt32(0);
      case 0xd3: return this.integer64(this.view(this.take(8)).getBigInt64(0));
      case 0xca: return this.view(this.take(4)).getFloat32(0);
      case 0xcb: return this.view(this.take(8)).getFloat64(0);
      case 0xd9: return this.text(this.u8());
      case 0xda: return this.text(this.view(this.take(2)).getUint16(0));
      case 0xdb: return this.text(this.view(this.take(4)).getUint32(0));
      case 0xc4: return this.take(this.u8());
      case 0xc5: return this.take(this.view(this.take(2)).getUint16(0));
      case 0xc6: return this.take(this.view(this.take(4)).getUint32(0));
      case 0xdc: return this.array(this.view(this.take(2)).getUint16(0));
      case 0xdd: return this.array(this.view(this.take(4)).getUint32(0));
      case 0xde: return this.map(this.view(this.take(2)).getUint16(0));
      case 0xdf: return this.map(this.view(this.take(4)).getUint32(0));
      default: throw new Error(`unsupported MessagePack tag 0x${tag.toString(16)}`);
    }
  }

  map() { throw new Error("MessagePack maps are forbidden by the indexed reactive protocol"); }
}

function decodeMessagePack(bytes) {
  const cursor = new MsgpackCursor(bytes);
  const value = cursor.value();
  if (cursor.offset !== bytes.length) throw new Error("trailing bytes after MessagePack envelope");
  return value;
}

const resetReasons = new Map([[1, "checkpoint-expired"], [2, "retention-advanced"], [3, "generation-changed"], [4, "checkpoint-not-current"], [5, "view-diverged"], [6, "oversized"], [7, "invalid-operations"], [8, "invalid-checkpoint"], [9, "backpressure"]]);
function bytesToBase64(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}
function canonicalEnvelope(bytes) {
  const value = decodeMessagePack(bytes);
  if (!Array.isArray(value) || value.length !== 7) throw new Error("pending canonical reactive contract: envelope must be an indexed array of seven fields");
  const [major, minor, features, tag, correlation, causation, body] = value;
  // u64 fields decode as BigInt or as a compact safe integer depending on
  // the producer's width; both spell the same non-negative u64.
  const asU64 = (field) => typeof field === "bigint"
    ? field
    : Number.isSafeInteger(field) && field >= 0 ? BigInt(field) : undefined;
  const [correlationU64, causationU64] = [asU64(correlation), asU64(causation)];
  if (major !== 1 || minor !== 0 || features !== 0 || !Number.isSafeInteger(tag) || correlationU64 === undefined || causationU64 === undefined) {
    throw new Error("pending canonical reactive contract: expected protocol 1.0 indexed envelope");
  }
  const messages = { 3: "snapshot", 4: "delta", 5: "reset", 6: "heartbeat", 8: "authorization_changed", 9: "region_route_change", 10: "error", 11: "backpressure" };
  const kind = messages[tag];
  if (!kind || !Array.isArray(body)) throw new Error(`pending canonical reactive contract: unsupported server tag ${String(tag)}`);
  return { major, minor, features, tag, correlation: correlationU64, causation: causationU64, kind, body };
}

function canonicalFields(fields) {
  if (!Array.isArray(fields)) throw new Error("pending canonical reactive contract: fields must be indexed arrays");
  return fields.map((field) => {
    if (!Array.isArray(field) || field.length !== 2 || typeof field[0] !== "string" || !(field[1] instanceof Uint8Array)) throw new Error("pending canonical reactive contract: invalid projected field");
    return { path: field[0], value: field[1] };
  });
}

function canonicalMessage(envelope) {
  const body = envelope.body;
  const asU64 = (field) => typeof field === "bigint"
    ? field
    : Number.isSafeInteger(field) && field >= 0 ? BigInt(field) : undefined;
  if (envelope.kind === "snapshot") {
    if (body.length !== 2 || typeof body[0] !== "string" || !Array.isArray(body[1])) throw new Error("pending canonical reactive contract: invalid snapshot");
    const snapshot = body[1];
    const generations = [asU64(snapshot[0]), asU64(snapshot[1]), asU64(snapshot[2])];
    if (snapshot.length !== 6 || generations.some((field) => field === undefined) || !Array.isArray(snapshot[3])) throw new Error("pending canonical reactive contract: invalid snapshot body");
    return { kind: "snapshot", subscription: body[0], snapshot: { view_generation: generations[0], authorization_generation: generations[1], revision: generations[2], items: snapshot[3].map((item) => ({ identity: item[0], fields: canonicalFields(item[1]) })), digest: snapshot[4], checkpoint: snapshot[5] } };
  }
  if (envelope.kind === "delta") {
    const revisions = [asU64(body[1]), asU64(body[2]), asU64(body[3])];
    if (body.length !== 7 || typeof body[0] !== "string" || revisions.some((field) => field === undefined) || !Array.isArray(body[4])) throw new Error("pending canonical reactive contract: invalid delta");
    body[1] = revisions[0];
    body[2] = revisions[1];
    body[3] = revisions[2];
    return { kind: "delta", subscription: body[0], view_generation: body[1], from_revision: body[2], to_revision: body[3], operations: body[4], digest: body[5], checkpoint: body[6] };
  }
  if (envelope.kind === "reset") {
    if (body.length !== 3 || typeof body[0] !== "string" || !resetReasons.has(body[1])) throw new Error("pending canonical reactive contract: invalid reset");
    return { kind: "reset", subscription: body[0], reason: resetReasons.get(body[1]), snapshot: body[2] };
  }
  return { kind: envelope.kind };
}

export { canonicalEnvelope, canonicalMessage };

const VIEW_CAPABILITY_HEADER = "x-terrascale-capability";
const MAX_REACTIVE_URL_LENGTH = 4096;

function normalizeSandboxOrigin(raw) {
  if (typeof raw !== "string" || raw.trim().length === 0 || raw.length > MAX_REACTIVE_URL_LENGTH) {
    throw new Error("remote sandbox URL is invalid");
  }
  let parsed;
  try {
    parsed = new URL(raw);
  } catch (error) {
    throw new Error("remote sandbox URL is invalid", { cause: error });
  }
  if (!["http:", "https:"].includes(parsed.protocol) || parsed.origin === "null" || parsed.username || parsed.password) {
    throw new Error("remote sandbox URL must be an HTTP(S) origin without credentials");
  }
  return parsed.origin;
}

function resolveSandboxUrl(raw, sandboxOrigin, kind = "reactive") {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > MAX_REACTIVE_URL_LENGTH) {
    throw new Error(`remote ${kind} URL is invalid`);
  }
  const origin = normalizeSandboxOrigin(sandboxOrigin);
  let parsed;
  try {
    parsed = new URL(raw, origin);
  } catch (error) {
    throw new Error(`remote ${kind} URL is invalid`, { cause: error });
  }
  if (parsed.origin !== origin || parsed.username || parsed.password || parsed.hash) {
    throw new Error(`remote ${kind} URL must use the configured sandbox origin`);
  }
  return parsed.toString();
}

export { normalizeSandboxOrigin, resolveSandboxUrl, VIEW_CAPABILITY_HEADER };

export function createRemoteSandbox() {
  const configuredBase = process.env.TERRASCALE_SANDBOX_URL;
  const key = process.env.TERRASCALE_SANDBOX_API_KEY;
  const database = process.env.TERRASCALE_SANDBOX_DATABASE_ID;
  if (!configuredBase || !key || !database) {
    throw new Error(
      "launch gate not passed: the real cloud sandbox is unavailable. Set " +
        "TERRASCALE_SANDBOX_URL + TERRASCALE_SANDBOX_API_KEY + TERRASCALE_SANDBOX_DATABASE_ID " +
        "for a cloud dev branch pre-filled with the reader's key (D-56); " +
        "the local stub is never substituted automatically.",
    );
  }
  const base = normalizeSandboxOrigin(configuredBase);

  let session;
  let preferredRegion;
  let writeNumber = 0;
  // Idempotency keys must never repeat across runs: a replay returns the
  // original outcome without recreating a document an earlier run deleted.
  const runTag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  // Reactive delivery (log/snapshot) never crosses the API origin: the edge
  // refuses it by design. Production serves it from the sync origin; a local
  // composition serves it from the member that holds the view, which the
  // regions catalog publishes per region.
  let regionHosts;
  const syncOriginFor = async (region) => {
    if (regionHosts === undefined) {
      const configured = process.env.TERRASCALE_SYNC_URL?.trim();
      if (configured) {
        regionHosts = null;
        return normalizeSandboxOrigin(configured);
      }
      const response = await fetch(`${base}/api/v1/regions`, { headers });
      if (!response.ok) throw new Error(`remote regions catalog failed: ${response.status}`);
      const catalog = await response.json();
      regionHosts = catalog?.hosts && typeof catalog.hosts === "object" ? catalog.hosts : null;
    }
    const host = regionHosts?.[region];
    if (typeof host === "string" && host.length > 0) return normalizeSandboxOrigin(host);
    // A member-direct sandbox (no edge in front) serves delivery itself.
    return base;
  };
  const headers = {
    accept: "application/json",
    "content-type": "application/json",
    authorization: `Bearer ${key}`,
  };
  const pathFor = (collection, id = "") =>
    `/api/v1/databases/${encodeURIComponent(database)}/collections/${encodeURIComponent(collection)}/documents${id ? `/${encodeURIComponent(id)}` : ""}`;
  const request = async (path, init = {}, accepted = []) => {
    const response = await fetch(`${base}${path}`, {
      ...init,
      headers: {
        ...headers,
        ...(session ? { "x-terrascale-session": session } : {}),
        ...(preferredRegion ? { "x-terrascale-preferred-region": preferredRegion } : {}),
        ...(init.headers ?? {}),
      },
    });
    const nextSession = response.headers.get("x-terrascale-session");
    if (nextSession) session = nextSession;
    const nextRegion = response.headers.get("x-terrascale-serving-region")
      ?? response.headers.get("x-terrascale-region");
    if (nextRegion) preferredRegion = nextRegion;
    if (!response.ok && !accepted.includes(response.status)) {
      const detail = await response.text();
      throw new Error(`remote ${init.method ?? "GET"} ${path} failed: ${response.status}${detail ? `: ${detail.slice(0, 500)}` : ""}`);
    }
    if (response.status === 204) return { body: null, response };
    const text = await response.text();
    if (!text) return { body: null, response };
    let body;
    try { body = JSON.parse(text); } catch { throw new Error(`remote ${init.method ?? "GET"} ${path} returned invalid JSON`); }
    return { body, response };
  };

  const reactiveRequest = async (body) => {
    const path = "/api/v1/reactive/views";
    const response = await fetch(`${base}${path}`, {
      method: "POST",
      headers: {
        ...headers,
        ...(session ? { "x-terrascale-session": session } : {}),
        ...(preferredRegion ? { "x-terrascale-preferred-region": preferredRegion } : {}),
      },
      body: JSON.stringify(body),
    });
    const nextSession = response.headers.get("x-terrascale-session");
    if (nextSession) session = nextSession;
    const nextRegion = response.headers.get("x-terrascale-serving-region")
      ?? response.headers.get("x-terrascale-region");
    if (nextRegion) preferredRegion = nextRegion;
    if (!response.ok) {
      const detail = await response.text();
      throw new Error(`remote POST ${path} failed: ${response.status}${detail ? `: ${detail.slice(0, 500)}` : ""}`);
    }
    let value;
    try { value = await response.json(); } catch { throw new Error("pending canonical reactive contract: attach response was not JSON"); }
    return { value, response };
  };

  const subscriptions = new Set();
  const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
  const installSnapshot = (state, message) => {
    const snapshot = message.snapshot;
    state.items = snapshot.items.map((item) => ({ identity: item.identity, fields: item.fields }));
    state.revision = snapshot.revision;
    for (const listener of state.listeners) listener();
  };
  const consume = (state, bytes) => {
    const envelope = canonicalEnvelope(bytes);
    const message = canonicalMessage(envelope);
    if (message.kind === "snapshot") { installSnapshot(state, message); return true; }
    if (message.kind === "delta") {
      if (message.subscription !== state.subscription) throw new Error("pending canonical reactive contract: subscription identity mismatch");
      for (const operation of message.operations) {
        if (!Array.isArray(operation) || operation.length < 2) throw new Error("pending canonical reactive contract: invalid indexed result operation");
        const [tag, identity] = operation;
        if (typeof identity !== "string") throw new Error("pending canonical reactive contract: result identity is not text");
        const index = state.items.findIndex((item) => item.identity === identity);
        if (tag === 1) state.items.splice(operation[2], 0, { identity, fields: canonicalFields(operation[3]) });
        else if (tag === 2) { if (index < 0) throw new Error("pending canonical reactive contract: update identity missing"); state.items[index] = { identity, fields: canonicalFields(operation[2]) }; }
        else if (tag === 3) { if (index < 0) throw new Error("pending canonical reactive contract: patch identity missing"); }
        else if (tag === 4) { if (index >= 0) state.items.splice(index, 1); }
        else if (tag === 5) { if (index < 0) throw new Error("pending canonical reactive contract: move identity missing"); const [item] = state.items.splice(index, 1); state.items.splice(operation[2], 0, item); }
        else throw new Error("pending canonical reactive contract: unknown result operation tag");
      }
      state.revision = message.to_revision;
      for (const listener of state.listeners) listener();
      return true;
    }
    if (message.kind === "reset") { throw new Error("pending canonical reactive contract: reset requires a fresh snapshot"); }
    return false;
  };
  const waitForData = async (state) => {
    const deadline = Date.now() + 10_000;
    while (!state.closed && Date.now() < deadline) {
      const url = new URL(state.logUrl);
      url.searchParams.set("offset", String(state.revision));
      // Reactive delivery is a capability-gated CDN request; the capability
      // authorizes the view and the bearer only satisfies member admission
      // where no proof-signing edge exists.
      const response = await fetch(url, { headers: { accept: "application/msgpack", authorization: `Bearer ${key}`, [VIEW_CAPABILITY_HEADER]: state.capability } });
      if (!response.ok) throw new Error(`remote GET ${url.pathname} failed: ${response.status}`);
      if (response.status !== 204 && response.status !== 304) {
        if (consume(state, new Uint8Array(await response.arrayBuffer()))) return;
      }
      await delay(100);
    }
    throw new Error("remote reactive log produced no delta within 10 seconds");
  };

  const sandbox = {
    mode: "remote",
    databaseId: database,
    async put({ databaseId: requestedDatabaseId, collection, doc }) {
      if (requestedDatabaseId !== database) throw new Error("remote databaseId does not match TERRASCALE_SANDBOX_DATABASE_ID");
      if (!doc || typeof doc.id !== "string" || !doc.id) throw new Error("doc must have a string id");
      const active = [...subscriptions].filter((state) => state.collection === collection);
      const { body } = await request(pathFor(collection, doc.id), {
        method: "PUT",
        headers: { "idempotency-key": `docs-snippet-${runTag}-${++writeNumber}-${collection}-${doc.id}` },
        body: JSON.stringify(doc),
      });
      await Promise.all(active.map((state) => waitForData(state)));
      return { ...doc, _rev: body?.revision ?? body?.rev ?? "remote" };
    },
    async get({ databaseId: requestedDatabaseId, collection, id }) {
      if (requestedDatabaseId !== database) throw new Error("remote databaseId does not match TERRASCALE_SANDBOX_DATABASE_ID");
      const { body, response } = await request(pathFor(collection, id), {}, [404]);
      return response.status === 404 ? null : body;
    },
    async remove({ databaseId: requestedDatabaseId, collection, id }) {
      if (requestedDatabaseId !== database) throw new Error("remote databaseId does not match TERRASCALE_SANDBOX_DATABASE_ID");
      const { response } = await request(pathFor(collection, id), { method: "DELETE" }, [404]);
      return response.status !== 404;
    },
    async query({ databaseId: requestedDatabaseId, collection, query }) {
      if (requestedDatabaseId !== database) throw new Error("remote databaseId does not match TERRASCALE_SANDBOX_DATABASE_ID");
      if (!query || query.op !== "=") throw new Error("remote snippet supports equality SQL only");
      const escapedCollection = collection.replaceAll('"', '""');
      const escapedField = query.field.replaceAll('"', '""');
      const { body } = await request(`/api/v1/databases/${encodeURIComponent(database)}/sql`, {
        method: "POST",
        body: JSON.stringify({
          sql: `SELECT * FROM "${escapedCollection}" WHERE "${escapedField}" = :snippet_value`,
          parameters: { snippet_value: query.value },
        }),
      });
      if (!Array.isArray(body?.rows)) throw new Error("remote SQL response did not contain rows");
      return body.rows.map((row) => {
        const value = row?.value;
        return value && typeof value === "object" ? { ...value, id: row.id ?? value.id } : { id: row.id, value };
      });
    },
    async subscribe({ databaseId: requestedDatabaseId, subscription, query, parameters }) {
      if (requestedDatabaseId !== database) throw new Error("remote databaseId does not match TERRASCALE_SANDBOX_DATABASE_ID");
      const { value } = await reactiveRequest({ databaseId: database, subscription, statement: query, parameters: bytesToBase64(parameters), checkpoint: null, transport: "http" });
      const { view, generation, snapshot_chunks: snapshots, log: rawLogUrl, capability, capability_header: capabilityHeader } = value ?? {};
      if (typeof view !== "string" || !Number.isInteger(Number(generation)) || typeof capability !== "string" || typeof capabilityHeader !== "string" || capabilityHeader.toLowerCase() !== VIEW_CAPABILITY_HEADER || typeof rawLogUrl !== "string" || !Array.isArray(snapshots)) {
        throw new Error("pending canonical reactive contract: attach response is missing canonical view metadata");
      }
      const syncOrigin = await syncOriginFor(preferredRegion ?? "");
      const logUrl = resolveSandboxUrl(rawLogUrl, syncOrigin, "reactive log");
      const snapshotUrls = snapshots.map((snapshotUrl) => resolveSandboxUrl(snapshotUrl, syncOrigin, "reactive snapshot"));
      const state = { collection: query.match(/FROM\s+([A-Za-z_][A-Za-z0-9_]*)/iu)?.[1], subscription: view, capability, logUrl, revision: Number(value.offset ?? 0), items: [], listeners: new Set(), closed: false };
      subscriptions.add(state);
      for (const snapshotUrl of snapshotUrls) {
        // The capability authorizes the view; the bearer only satisfies
        // member admission where no proof-signing edge exists.
        const response = await fetch(snapshotUrl, { headers: { accept: "application/msgpack", authorization: `Bearer ${key}`, [VIEW_CAPABILITY_HEADER]: capability } });
        if (!response.ok) throw new Error(`remote snapshot failed: ${response.status}`);
        consume(state, new Uint8Array(await response.arrayBuffer()));
      }
      return {
        view: {
          subscribe(listener) { state.listeners.add(listener); return () => state.listeners.delete(listener); },
          getVisibleItems() { return state.items; },
        },
        close() { state.closed = true; subscriptions.delete(state); },
      };
    },
  };

  const result = (value, status = 200) => ({ ok: true, value, status });
  sandbox.client = {
    documents: {
      async put(databaseId, collection, id, value, options = {}) {
        const body = await sandbox.put({ databaseId, collection, doc: { ...(value ?? {}), id } });
        return result({ revision: body._rev }, 200);
      },
      async get(databaseId, collection, id) {
        const value = await sandbox.get({ databaseId, collection, id });
        return result(value, value === null ? 404 : 200);
      },
      async patch(databaseId, collection, id, patch) {
        const current = await sandbox.get({ databaseId, collection, id });
        if (current === null) return { ok: false, error: { code: "not_found" }, status: 404 };
        const body = await sandbox.put({ databaseId, collection, doc: { ...current, ...(patch ?? {}), id } });
        return result({ revision: body._rev }, 200);
      },
      async delete(databaseId, collection, id) {
        const deleted = await sandbox.remove({ databaseId, collection, id });
        return deleted ? result(undefined, 204) : { ok: false, error: { code: "not_found" }, status: 404 };
      },
    },
    sql: {
      async execute(databaseId, requestBody) {
        const escaped = requestBody.sql;
        const { body } = await request(`/api/v1/databases/${encodeURIComponent(databaseId)}/sql`, {
          method: "POST",
          body: JSON.stringify({ sql: escaped, parameters: requestBody.parameters }),
        });
        return result(body, 200);
      },
    },
    sync: { subscribe: (input) => sandbox.subscribe(input) },
  };
  sandbox.sync = sandbox.client.sync;
  return sandbox;
}

export async function runAll(mode = requestedMode()) {
  const files = readdirSync(snippetsDir).filter((file) => file.endsWith(".ts")).sort();
  if (files.length === 0) throw new Error("no snippets found in docs/snippets/");
  if (mode !== "remote" && mode !== "stub") throw new Error(`unknown snippet mode: ${mode}`);
  const sandbox = mode === "remote" ? createRemoteSandbox() : createStubSandbox();
  const results = [];
  for (const file of files) {
    const mod = await import(pathToFileURL(join(snippetsDir, file)).href);
    if (mod.runnable === false) {
      console.log(`skip - ${file} (fixture is non-runnable; see its contract and qualification requirements)`);
      continue;
    }
    if (mode === "remote" && Array.isArray(mod.requires) && mod.requires.includes("project") && process.env.TERRASCALE_PROJECT_ID === undefined) {
      console.log(`skip - ${file} (set TERRASCALE_PROJECT_ID to qualify this management flow)`);
      continue;
    }
    if (typeof mod.run !== "function") throw new Error(`${file} must export run(sandbox)`);
    await mod.run(sandbox);
    results.push({ file, ok: true });
    console.log(`ok - ${file} (${sandbox.mode})`);
  }
  return results;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const results = await runAll();
    console.log(`${results.length} snippet(s) passed.`);
  } catch (error) {
    console.error(`FAIL: ${error.message}`);
    process.exit(1);
  }
}
