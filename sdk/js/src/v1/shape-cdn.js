import { array, object, string } from "./decode.js";
import { parseExactJson } from "./json.js";
import { isTerraBaseV1Error } from "./errors.js";
import { decodeSqlValue } from "./sql-execute.js";

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
const offsetPattern = /^(?:snap\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)|0|[1-9]\d*)$/u;

export class ShapeCdn {
  /** @type {import("./json.js").JsonObject[]} */
  #objects = [];
  /** @type {Map<string, Promise<CryptoKey>>} */
  #keys = new Map();
  #epoch = -1;
  /** @param {import("./transport.js").V1Transport} transport @param {Readonly<Record<string, string>>} params @param {Readonly<Record<string, string>>} filter */
  constructor(transport, params, filter) {
    this.transport = transport;
    this.params = params;
    this.filter = filter;
  }

  /** @param {string} handle @param {import("./json.js").JsonObject} manifest */
  offer(handle, manifest) {
    if (manifest.handle !== handle) throw new TypeError("The shape hint changed handle.");
    const entries = array(manifest.objects, "shape hint objects");
    if (entries.length > 8) throw new TypeError("Too many shape hint objects.");
    this.#objects = entries.map(value => object(value, "shape hint object"));
    const epoch = Math.floor(Date.now() / 600_000);
    if (epoch !== this.#epoch) this.#keys.clear();
    this.#epoch = epoch;
  }

  /** @param {string} handle @param {string} offset @param {AbortSignal} signal */
  async read(handle, offset, signal) {
    try {
      const epoch = Math.floor(Date.now() / 600_000);
      if (epoch !== this.#epoch) {
        this.#objects = [];
        this.#keys.clear();
        this.#epoch = epoch;
      }
      let entry = this.#objects[0];
      if (entry?.offset !== offset || Date.parse(string(entry.expires_at, "shape expiry")) <= Date.now()) {
        this.#objects = [];
        const manifest = object((await this.transport.call("getShapeManifest", this.params, {
          query: new URLSearchParams({ ...this.filter, handle, offset }), signal,
        })).value, "shape manifest");
        if (manifest.handle !== handle) throw new TypeError("The shape manifest changed handle.");
        const entries = array(manifest.objects, "shape objects");
        if (entries.length > 8) throw new TypeError("Too many shape objects.");
        this.#objects = entries.map(value => object(value, "shape object"));
        entry = this.#objects[0];
      }
      if (entry === undefined) return undefined;
      this.#objects.shift();
      const next = string(entry.next_offset, "shape next offset");
      const expires = Date.parse(string(entry.expires_at, "shape expiry"));
      if (entry.offset !== offset || entry.binding !== `${handle}\0${offset}` || !offsetPattern.test(next) || next === offset
        || !Number.isFinite(expires) || expires <= Date.now() || typeof entry.size !== "number" || !/^[a-f0-9]{64}$/u.test(string(entry.sha256, "shape digest")))
        throw new TypeError("Invalid sealed shape binding.");
      const bytes = await this.transport.readShapeObject(string(entry.url, "shape URL"), entry.size, signal);
      const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
      if (Array.from(digest, byte => byte.toString(16).padStart(2, "0")).join("") !== entry.sha256)
        throw new TypeError("Invalid sealed shape digest.");
      const header = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      if (decoder.decode(bytes.subarray(0, 4)) !== "TBS1" || header.getBigUint64(4).toString() !== entry.epoch
        || decoder.decode(bytes.subarray(12, 38)) !== entry.key_id)
        throw new TypeError("Invalid sealed shape header.");
      const keyId = string(entry.key_id, "shape key id");
      const sealedEpoch = string(entry.epoch, "shape epoch");
      const identity = `${handle}:${sealedEpoch}:${keyId}`;
      let key = this.#keys.get(identity);
      if (key === undefined) {
        if (this.#keys.size >= 8) this.#keys.clear();
        key = this.#key(handle, sealedEpoch, keyId, signal);
        this.#keys.set(identity, key);
      }
      const plain = await open(bytes, await key, handle, offset);
      const page = object(parseExactJson(decoder.decode(plain)), "sealed shape page");
      if (page.handle !== handle || page.offset !== offset || page.next_offset !== next || typeof page.up_to_date !== "boolean")
        throw new TypeError("Invalid sealed shape page.");
      for (const change of array(page.changes, "sealed shape changes")) {
        for (const value of array(object(change, "sealed shape change").ops, "sealed shape ops")) {
          const op = object(value, "sealed shape op");
          string(op.key, "sealed shape key");
          if (op.move !== undefined && op.move !== "in" && op.move !== "out") throw new TypeError("Invalid sealed shape move.");
          if (op.move === "out" || (op.move === undefined && op.op === "delete")) continue;
          if (op.move !== "in" && op.op !== "insert" && op.op !== "update") throw new TypeError("Invalid sealed shape operation.");
          for (const field of Object.values(object(op.row, "sealed shape row"))) decodeSqlValue(field);
        }
      }
      signal.throwIfAborted();
      return { status: 200, headers: new Headers(), value: page };
    } catch (error) {
      this.#objects = [];
      this.#keys.clear();
      signal.throwIfAborted();
      if (isTerraBaseV1Error(error) && (error.status === 401 || error.status === 403 || error.code === "must_refetch")) throw error;
      return undefined;
    }
  }

  /** @param {string} handle @param {string} epoch @param {string} keyId @param {AbortSignal} signal */
  async #key(handle, epoch, keyId, signal) {
    const response = object((await this.transport.call("getShapeKey", this.params, {
      query: new URLSearchParams({ ...this.filter, handle, epoch, key_id: keyId }), signal,
    })).value, "shape key");
    const encoded = string(response.key, "shape key bytes");
    if (response.handle !== handle || response.epoch !== epoch || response.key_id !== keyId || !/^[A-Za-z0-9_-]{43}$/u.test(encoded))
      throw new TypeError("Invalid shape key binding.");
    const bytes = Uint8Array.from(atob(encoded.replace(/-/gu, "+").replace(/_/gu, "/")), character => character.charCodeAt(0));
    try {
      if (bytes.length !== 32) throw new TypeError("Invalid shape key length.");
      return await crypto.subtle.importKey("raw", bytes, "HKDF", false, ["deriveKey"]);
    } finally {
      bytes.fill(0);
    }
  }
}

/** @param {Uint8Array<ArrayBuffer>} bytes @param {CryptoKey} epochKey @param {string} handle @param {string} offset */
async function open(bytes, epochKey, handle, offset) {
  const key = await crypto.subtle.deriveKey({ name: "HKDF", hash: "SHA-256", salt: bytes.subarray(38, 70),
    info: encoder.encode(`TBS1 object\0${handle}\0${offset}`) }, epochKey, { name: "AES-GCM", length: 256 }, false, ["decrypt"]);
  const header = bytes.subarray(0, 70);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const associated = new Uint8Array(82);
  associated.set(header);
  const aad = new DataView(associated.buffer);
  const nonce = new Uint8Array(12);
  const nonceView = new DataView(nonce.buffer);
  const plain = new Uint8Array(Math.min(8 * 1024 * 1024, bytes.length - 90));
  let length = 0;
  for (let at = 70, index = 0; index <= 8; index++) {
    if (at + 20 > bytes.length) throw new TypeError("Truncated sealed shape frame.");
    const word = view.getUint32(at);
    const size = word & 0x7fffffff;
    const last = (word & 0x80000000) !== 0;
    if (size > 1024 * 1024 || (last && size === 1024 * 1024) || (!last && size !== 1024 * 1024) || at + 20 + size > bytes.length || length + size > plain.length)
      throw new TypeError("Invalid sealed shape frame length.");
    nonceView.setBigUint64(4, BigInt(index));
    aad.setBigUint64(70, BigInt(index));
    aad.setUint32(78, word);
    const frame = await crypto.subtle.decrypt({ name: "AES-GCM", iv: nonce, additionalData: associated, tagLength: 128 }, key, bytes.subarray(at + 4, at + 20 + size));
    plain.set(new Uint8Array(frame), length);
    length += size;
    at += 20 + size;
    if (last) {
      if (at !== bytes.length) throw new TypeError("Bytes after the sealed shape's last frame.");
      return plain.subarray(0, length);
    }
  }
  throw new TypeError("The sealed shape has no final frame.");
}
