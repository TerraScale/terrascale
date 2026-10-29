import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { V1Transport } from "../src/v1/transport.js";
import { decryptSealedShapePage, readShapePage } from "../src/v1/shape-reader.js";
import { shapeVectors, vectors } from "./support/shape-vectors.js";

/** @typedef {Uint8Array<ArrayBuffer>} Bytes */
/** @typedef {{ handle: string, offset: string, epoch: string, keyId: string }} Position */
const { subtle } = globalThis.crypto;
const PROJECT = shapeVectors.fixture.scope.project_id;
const ORIGIN = "https://origin.example.invalid";
const TOKEN = "fixture.payload.signature";
const HANDLE = shapeVectors.fixture.handle;
const OTHER_HANDLE = "h1-0000000000000002";
const KID = "01ARZ3NDEKTSV4RRFFQ69G5FC0";
const OTHER_KID = "01ARZ3NDEKTSV4RRFFQ69G5FC1";
const SALT = new Uint8Array(32).fill(7);
const LAST_FRAME_FLAG = 0x80000000;
const tbs1 = vectors("tbs1_vectors")[0];
const PROJECT_DATA_KEY = /** @type {Bytes} */ (Uint8Array.from(Buffer.from(tbs1.project_data_key_hex, "hex")));
const PLAINTEXT = tbs1.plaintext_utf8;
const params = { database: "demo", collection: "notes" };
const otherParams = { database: "demo", collection: "other" };

/** Every key the fake origin can serve, keyed by `[handle, epoch, key_id]`. */
const registry = new Map();

/** @param {string} value @returns {Bytes} */
const utf8 = value => new Uint8Array(new TextEncoder().encode(value));
/** @param {bigint | number | string} value @returns {Bytes} */
function uint64(value) {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(value));
  return out;
}
/** @param {...Uint8Array} parts @returns {Bytes} */
function concat(...parts) {
  const out = new Uint8Array(parts.reduce((total, part) => total + part.byteLength, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.byteLength;
  }
  return out;
}
/** @param {Bytes} ikm @param {Bytes} salt @param {Bytes} info @returns {Promise<Bytes>} */
async function hkdf(ikm, salt, info) {
  const base = await subtle.importKey("raw", ikm, "HKDF", false, ["deriveBits"]);
  return new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info }, base, 256));
}
/**
 * Reference epoch step: `K_e = HKDF(K_d, zero salt, "TBS1 epoch" NUL handle NUL epoch)`.
 * @param {string} handle @param {string} epoch @returns {Promise<Bytes>}
 */
function epochKey(handle, epoch) {
  return hkdf(PROJECT_DATA_KEY, new Uint8Array(32), utf8(`TBS1 epoch\u0000${handle}\u0000${epoch}`));
}
/**
 * Seals one last-frame TBS1 object with WebCrypto, following the header and AAD layout the reader checks.
 * @param {{ key: Bytes, handle: string, offset: string, epoch: string, keyId: string, plaintext: Bytes }} options
 * @returns {Promise<Bytes>}
 */
async function seal({ key, handle, offset, epoch, keyId, plaintext }) {
  const header = concat(utf8("TBS1"), uint64(epoch), utf8(keyId), SALT);
  const objectKey = await subtle.importKey(
    "raw",
    await hkdf(key, SALT, utf8(`TBS1 object\u0000${handle}\u0000${offset}`)),
    "AES-GCM",
    false,
    ["encrypt"],
  );
  const lengthWord = new Uint8Array(4);
  new DataView(lengthWord.buffer).setUint32(0, plaintext.byteLength | LAST_FRAME_FLAG);
  const sealed = new Uint8Array(await subtle.encrypt(
    { name: "AES-GCM", iv: concat(new Uint8Array(4), uint64(0)), additionalData: concat(header, uint64(0), lengthWord), tagLength: 128 },
    objectKey,
    plaintext,
  ));
  return concat(header, lengthWord, sealed);
}
/**
 * Seals the fixture page for another epoch, key id or handle, and registers the key the origin serves for it.
 * @param {{ epoch?: string, keyId?: string, handle?: string }} [options]
 * @returns {Promise<{ bytes: Bytes, position: Position }>}
 */
async function sealedAt({ epoch = "100", keyId = KID, handle = HANDLE } = {}) {
  const key = await epochKey(handle, epoch);
  registry.set(JSON.stringify([handle, epoch, keyId]), key);
  const bytes = await seal({ key, handle, offset: "2", epoch, keyId, plaintext: utf8(PLAINTEXT.replaceAll(HANDLE, handle)) });
  return { bytes, position: { handle, offset: "2", epoch, keyId } };
}
/** @param {URL} url */
function keyReply(url) {
  const handle = url.searchParams.get("handle") ?? "";
  const epoch = url.searchParams.get("epoch") ?? "";
  const keyId = url.searchParams.get("key_id") ?? "";
  const key = registry.get(JSON.stringify([handle, epoch, keyId]));
  if (key === undefined) return Response.json({ error: { code: "not_found", message: "no key" } }, { status: 404 });
  return Response.json({ handle, epoch, key_id: keyId, key: Buffer.from(key).toString("base64url") });
}
function mustRefetch() {
  return Response.json({ error: { code: "must_refetch", message: "epoch and key pair is not retained", handle: HANDLE } }, { status: 409 });
}
function staleShape() {
  return Response.json({ error: { code: "must_refetch", message: "shape lineage changed", handle: HANDLE } }, { status: 409 });
}
/**
 * A transport whose fake origin counts key fetches separately from shape reads.
 * @param {{ key?: (url: URL, init: RequestInit) => Response | Promise<Response>, shape?: () => Response, token?: string }} [options]
 */
function makeTransport({ key = (url) => keyReply(url), shape = staleShape, token = TOKEN } = {}) {
  /** @type {{ key: number, shape: number, keyCache: unknown[] }} */
  const calls = { key: 0, shape: 0, keyCache: [] };
  const transport = new V1Transport({
    project: PROJECT,
    token,
    baseUrl: ORIGIN,
    fetch: (url, init) => {
      const target = new URL(String(url));
      if (target.pathname.endsWith("/shape/keys")) {
        calls.key++;
        calls.keyCache.push(init?.cache);
        return Promise.resolve(key(target, init ?? {}));
      }
      calls.shape++;
      return Promise.resolve(shape());
    },
  });
  return { transport, calls };
}
/** @param {V1Transport} transport @param {Record<string, string>} scope @param {{ bytes: Bytes, position: Position }} sealed */
function open(transport, scope, { bytes, position }) {
  return decryptSealedShapePage(transport, scope, bytes, position, undefined);
}

/** @type {{ mockReturnValue: (value: number) => unknown }} */
let clock;
beforeEach(() => {
  registry.clear();
  clock = vi.spyOn(Date, "now").mockReturnValue(60000000);
});
afterEach(() => {
  vi.restoreAllMocks();
});

test.each(["terrascale-key-2026-rot012", KID.toLowerCase(), `8${KID.slice(1)}`])("noncanonical key id %s fails before fetching", async keyId => {
  const sealed = await sealedAt();
  const { transport, calls } = makeTransport();
  await expect(open(transport, params, { ...sealed, position: { ...sealed.position, keyId } })).rejects.toThrow(/canonical uppercase Crockford ULID/u);
  expect(calls.key).toBe(0);
  expect(calls.shape).toBe(0);
});

test("repeat decrypts at the current epoch share one no-store key fetch, and an aborted signal still fails before the cache", async () => {
  const sealed = await sealedAt();
  const { transport, calls } = makeTransport();
  await open(transport, params, sealed);
  await open(transport, params, sealed);
  expect(calls.key).toBe(1);
  expect(calls.keyCache).toEqual(["no-store"]);
  const controller = new AbortController();
  controller.abort();
  await expect(decryptSealedShapePage(transport, params, sealed.bytes, sealed.position, controller.signal)).rejects.toThrow();
  expect(calls.key).toBe(1);
});

test("a served higher epoch evicts the old key, and historical objects are fetched but not cached", async () => {
  const current = await sealedAt();
  const next = await sealedAt({ epoch: "101" });
  const { transport, calls } = makeTransport();
  await open(transport, params, current);
  await open(transport, params, next);
  expect(calls.key).toBe(2);
  await open(transport, params, next);
  expect(calls.key).toBe(2);
  await open(transport, params, current);
  expect(calls.key).toBe(3);
  await open(transport, params, current);
  expect(calls.key).toBe(4);
});

test("wallclock expiry alone drops an old epoch key, so the next historical decrypt refetches", async () => {
  const sealed = await sealedAt();
  const { transport, calls } = makeTransport();
  await open(transport, params, sealed);
  clock.mockReturnValue(60600000);
  await open(transport, params, sealed);
  expect(calls.key).toBe(2);
  await open(transport, params, sealed);
  expect(calls.key).toBe(3);
});

test("a 409 must_refetch from readFreeShape clears the collection's keys", async () => {
  const sealed = await sealedAt();
  const { transport, calls } = makeTransport();
  await open(transport, params, sealed);
  await expect(readShapePage(transport, params, { handle: HANDLE, offset: "2" }, undefined))
    .rejects.toMatchObject({ status: 409, code: "must_refetch" });
  await open(transport, params, sealed);
  expect(calls.shape).toBe(1);
  expect(calls.key).toBe(2);
});

test("a 409 must_refetch from getShapeKey clears the collection's keys", async () => {
  const sealed = await sealedAt();
  const other = await sealedAt({ keyId: OTHER_KID });
  const { transport, calls } = makeTransport({
    key: url => url.searchParams.get("key_id") === OTHER_KID ? mustRefetch() : keyReply(url),
  });
  await open(transport, params, sealed);
  await expect(open(transport, params, other)).rejects.toMatchObject({ status: 409, code: "must_refetch" });
  await open(transport, params, sealed);
  expect(calls.key).toBe(3);
});

test("a 409 in one collection does not clear keys cached for another", async () => {
  const sealed = await sealedAt();
  const { transport, calls } = makeTransport();
  await open(transport, params, sealed);
  await open(transport, otherParams, sealed);
  expect(calls.key).toBe(2);
  await expect(readShapePage(transport, otherParams, { handle: HANDLE, offset: "2" }, undefined))
    .rejects.toMatchObject({ status: 409 });
  await open(transport, params, sealed);
  expect(calls.key).toBe(2);
  await open(transport, otherParams, sealed);
  expect(calls.key).toBe(3);
});

test("separate credentials never share epoch keys for the same database and collection", async () => {
  const sealed = await sealedAt();
  const first = makeTransport();
  const second = makeTransport({ token: "other.payload.signature" });
  await open(first.transport, params, sealed);
  await open(second.transport, params, sealed);
  await open(first.transport, params, sealed);
  await open(second.transport, params, sealed);
  expect(first.calls.key).toBe(1);
  expect(second.calls.key).toBe(1);
});

test("different key ids and handles occupy separate cache entries", async () => {
  const sealed = await sealedAt();
  const otherKid = await sealedAt({ keyId: OTHER_KID });
  const otherHandle = await sealedAt({ handle: OTHER_HANDLE });
  const { transport, calls } = makeTransport();
  await open(transport, params, sealed);
  await open(transport, params, otherKid);
  expect(calls.key).toBe(2);
  await open(transport, params, sealed);
  expect(calls.key).toBe(2);
  await open(transport, params, otherHandle);
  expect(calls.key).toBe(3);
  await open(transport, params, otherHandle);
  expect(calls.key).toBe(3);
});

test("a failed key response is not cached, so the next decrypt fetches again", async () => {
  const sealed = await sealedAt();
  let attempts = 0;
  const { transport, calls } = makeTransport({
    key: url => (++attempts === 1 ? Response.json({ error: { code: "unavailable", message: "try later" } }, { status: 503 }) : keyReply(url)),
  });
  await expect(open(transport, params, sealed)).rejects.toMatchObject({ status: 503 });
  await open(transport, params, sealed);
  expect(calls.key).toBe(2);
  await open(transport, params, sealed);
  expect(calls.key).toBe(2);
});

test("a 409 that lands while a key fetch is in flight stops that fetch from repopulating the cache", async () => {
  const sealed = await sealedAt();
  /** @type {() => void} */
  let release = () => {};
  const gate = new Promise(resolve => { release = () => resolve(undefined); });
  /** @type {() => void} */
  let enter = () => {};
  const entered = new Promise(resolve => { enter = () => resolve(undefined); });
  const { transport, calls } = makeTransport({
    key: async url => { enter(); await gate; return keyReply(url); },
  });
  const inFlight = open(transport, params, sealed);
  await entered;
  await expect(readShapePage(transport, params, { handle: HANDLE, offset: "2" }, undefined))
    .rejects.toMatchObject({ status: 409, code: "must_refetch" });
  release();
  expect((await inFlight).nextOffset).toBe("4");
  await open(transport, params, sealed);
  expect(calls.key).toBe(2);
});
