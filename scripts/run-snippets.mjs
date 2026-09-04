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

// The reactive API's envelope is intentionally small: maps, arrays, strings,
// and unsigned revisions. Keeping this decoder local avoids making docs depend
// on an SDK or a second transport implementation.
class MsgpackCursor {
  constructor(bytes) {
    this.bytes = bytes;
    this.offset = 0;
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

  value() {
    const tag = this.u8();
    if (tag <= 0x7f) return tag;
    if (tag >= 0xe0) return tag - 0x100;
    if ((tag & 0xe0) === 0xa0) return this.text(tag & 0x1f);
    if ((tag & 0xf0) === 0x90) return Array.from({ length: tag & 0x0f }, () => this.value());
    if ((tag & 0xf0) === 0x80) return this.map(tag & 0x0f);
    switch (tag) {
      case 0xc0: return null;
      case 0xc2: return false;
      case 0xc3: return true;
      case 0xcc: return this.u8();
      case 0xcd: return this.view(this.take(2)).getUint16(0);
      case 0xce: return this.view(this.take(4)).getUint32(0);
      case 0xcf: return Number(this.view(this.take(8)).getBigUint64(0));
      case 0xd0: return this.view(this.take(1)).getInt8(0);
      case 0xd1: return this.view(this.take(2)).getInt16(0);
      case 0xd2: return this.view(this.take(4)).getInt32(0);
      case 0xd3: return Number(this.view(this.take(8)).getBigInt64(0));
      case 0xca: return this.view(this.take(4)).getFloat32(0);
      case 0xcb: return this.view(this.take(8)).getFloat64(0);
      case 0xd9: return this.text(this.u8());
      case 0xda: return this.text(this.view(this.take(2)).getUint16(0));
      case 0xdb: return this.text(this.view(this.take(4)).getUint32(0));
      case 0xc4: return this.take(this.u8());
      case 0xc5: return this.take(this.view(this.take(2)).getUint16(0));
      case 0xc6: return this.take(this.view(this.take(4)).getUint32(0));
      case 0xdc: return Array.from({ length: this.view(this.take(2)).getUint16(0) }, () => this.value());
      case 0xdd: return Array.from({ length: this.view(this.take(4)).getUint32(0) }, () => this.value());
      case 0xde: return this.map(this.view(this.take(2)).getUint16(0));
      case 0xdf: return this.map(this.view(this.take(4)).getUint32(0));
      default: throw new Error(`unsupported MessagePack tag 0x${tag.toString(16)}`);
    }
  }

  map(length) {
    const result = {};
    for (let index = 0; index < length; index += 1) result[this.value()] = this.value();
    return result;
  }
}

function decodeMessagePack(bytes) { return new MsgpackCursor(bytes).value(); }

function createRemoteSandbox() {
  const base = process.env.TERRASCALE_SANDBOX_URL?.replace(/\/$/, "");
  const key = process.env.TERRASCALE_API_KEY;
  const database = process.env.TERRASCALE_DATABASE_ID;
  if (!base || !key || !database) {
    throw new Error(
      "remote sandbox needs TERRASCALE_SANDBOX_URL + TERRASCALE_API_KEY + " +
        "TERRASCALE_DATABASE_ID (a cloud dev branch pre-filled with the reader's key; D-56).",
    );
  }

  let session;
  let writeNumber = 0;
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
        ...(init.headers ?? {}),
      },
    });
    const nextSession = response.headers.get("x-terrascale-session");
    if (nextSession) session = nextSession;
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
    const path = `/api/v1/databases/${encodeURIComponent(database)}/reactive/poll`;
    const response = await fetch(`${base}${path}`, {
      method: "POST",
      headers: {
        ...headers,
        accept: "application/msgpack",
        ...(session ? { "x-terrascale-session": session } : {}),
      },
      body: JSON.stringify(body),
    });
    const nextSession = response.headers.get("x-terrascale-session");
    if (nextSession) session = nextSession;
    if (!response.ok) {
      const detail = await response.text();
      throw new Error(`remote POST ${path} failed: ${response.status}${detail ? `: ${detail.slice(0, 500)}` : ""}`);
    }
    return { frames: decodeMessagePack(new Uint8Array(await response.arrayBuffer())), response };
  };

  const subscriptions = new Set();
  const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
  const consume = (state, frames) => {
    let delivered = 0;
    for (const frame of Array.isArray(frames) ? frames : []) {
      if (!frame || frame.t !== "delta" || typeof frame.id !== "string") continue;
      const body = typeof frame.body === "string" ? JSON.parse(frame.body) : frame.body;
      const doc = body && typeof body === "object" ? { ...body, id: frame.id } : { id: frame.id, value: body };
      state.callback({ doc });
      delivered += 1;
    }
    return delivered;
  };
  const waitForData = async (state) => {
    const deadline = Date.now() + 10_000;
    while (!state.controller.signal.aborted && Date.now() < deadline) {
      if (state.error) throw state.error;
      const { frames } = await reactiveRequest({ op: "poll", subscription: state.id });
      if (consume(state, frames)) return;
      await delay(100);
    }
    throw new Error("remote reactive log produced no delta within 10 seconds");
  };

  return {
    mode: "remote",
    async put(collection, doc) {
      if (!doc || typeof doc.id !== "string" || !doc.id) throw new Error("doc must have a string id");
      const active = [...subscriptions].filter((state) => state.collection === collection);
      await Promise.all(active.map((state) => state.setup));
      const { body } = await request(pathFor(collection, doc.id), {
        method: "PUT",
        headers: { "idempotency-key": `docs-snippet-${++writeNumber}-${collection}-${doc.id}` },
        body: JSON.stringify(doc),
      });
      await Promise.all(active.map((state) => waitForData(state)));
      return { ...doc, _rev: body?.revision ?? body?.rev ?? "remote" };
    },
    async get(collection, id) {
      const { body, response } = await request(pathFor(collection, id), {}, [404]);
      return response.status === 404 ? null : body;
    },
    async remove(collection, id) {
      const { response } = await request(pathFor(collection, id), { method: "DELETE" }, [404]);
      return response.status !== 404;
    },
    async query(collection, query) {
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
    subscribe(collection, callback) {
      const controller = new AbortController();
      const state = { collection, callback, controller, id: null, error: null, closed: false };
      state.setup = reactiveRequest({ op: "subscribe", collection, conditions: [], projection: [] })
        .then(({ response }) => {
          const id = Number(response.headers.get("x-terrascale-subscription"));
          if (!Number.isSafeInteger(id) || id <= 0) throw new Error("remote subscribe returned no valid subscription id");
          if (state.closed) {
            void reactiveRequest({ op: "unsubscribe", subscription: id }).catch(() => {});
          } else {
            state.id = id;
          }
        });
      state.setup.catch((error) => { state.error = error; });
      subscriptions.add(state);
      return () => {
        state.closed = true;
        controller.abort();
        subscriptions.delete(state);
        if (state.id) void reactiveRequest({ op: "unsubscribe", subscription: state.id }).catch(() => {});
      };
    },
    async createTenant(key) {
      if (!key || typeof key !== "string") throw new Error("tenant key required");
      const { body } = await request("/api/v1/tenants", {
        method: "POST",
        headers: { "idempotency-key": `docs-snippet-tenant-${key}` },
        body: JSON.stringify({ key }),
      });
      const databaseId = body?.databaseId ?? body?.database_id ?? body?.id;
      if (typeof databaseId !== "string" || !databaseId) {
        throw new Error("remote createTenant response did not contain databaseId");
      }
      return { databaseId, key: body?.key ?? key };
    },
  };
}

export async function runAll(mode = requestedMode()) {
  const files = readdirSync(snippetsDir).filter((file) => file.endsWith(".ts")).sort();
  if (files.length === 0) throw new Error("no snippets found in docs/snippets/");
  if (mode !== "remote" && mode !== "stub") throw new Error(`unknown snippet mode: ${mode}`);
  const sandbox = mode === "remote" ? createRemoteSandbox() : createStubSandbox();
  if (mode === "stub") {
    const query = sandbox.query;
    sandbox.query = (collection, descriptor) => query(collection, (doc) => doc[descriptor.field] === descriptor.value);
  }
  const results = [];
  for (const file of files) {
    const mod = await import(pathToFileURL(join(snippetsDir, file)).href);
    if (mod.runnable === false) {
      console.log(`skip - ${file} (pending TerraBase v1 tenant contract)`);
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
