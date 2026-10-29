import { array, boolean, malformed, object, string } from "./decode.js";
import { requireId } from "../ids.js";
import { assertJsonObject, stringifyExactJson } from "./json.js";
import { requireServiceUrl } from "./transport.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { FetchLike } from "../config.js" */
/** @import { JsonObject, JsonValue } from "./json.js" */
/** @typedef {{ owner: boolean, public: boolean, roles: readonly string[] }} FileAccessPolicy */
/** @typedef {{ owner: string | null, read: FileAccessPolicy, write: FileAccessPolicy }} FileAccessRules */
/** @typedef {{ id: string, name: string, region: string, rules: FileAccessRules }} FileBucketInfo */
/** @typedef {{ id: string, name: string, contentType: string, size: number, sha256: string, metadata: JsonObject, status: string, uploadExpiresAt: string }} StoredFile */
/** @typedef {{ id: string, url: string, expiresAt: string, headers: Readonly<Record<string, string>> }} SignedFileUrl */
/** @typedef {{ name: string, contentType: string, size: number, sha256: string, metadata: JsonObject }} FileUploadRequest */
/** @typedef {{ signal?: AbortSignal | undefined }} FileRequestOptions */
/** @typedef {{ after?: string | undefined, signal?: AbortSignal | undefined }} FileListOptions */
/** @typedef {{ metadata: JsonObject, contentType?: string | undefined, signal?: AbortSignal | undefined }} FileUploadOptions */
const maxFileBytes = 100 * 1024 * 1024;

/** @param {JsonValue | undefined} value @returns {FileAccessPolicy} */
function policy(value) {
  const v = object(value, "file access policy");
  return { owner: boolean(v.owner, "owner grant"), public: boolean(v.public, "public grant"), roles: array(v.roles, "roles").map(v => string(v, "role")) };
}
/** @param {JsonValue | undefined} value @returns {FileAccessRules} */
function rules(value) {
  const v = object(value, "file access rules");
  return { owner: v.owner === null ? null : string(v.owner, "owner pointer"), read: policy(v.read), write: policy(v.write) };
}
/** @param {JsonValue | undefined} value @returns {FileBucketInfo} */
function bucketInfo(value) {
  const v = object(value, "file bucket");
  return { id: requireId(v.id), name: string(v.name, "bucket name"), region: string(v.region, "bucket region"), rules: rules(v.rules) };
}
/** @param {JsonValue | undefined} value @returns {StoredFile} */
function storedFile(value) {
  const v = object(value, "file");
  if (typeof v.size !== "number" || !Number.isSafeInteger(v.size) || v.size < 1 || v.size > maxFileBytes) malformed("file size");
  return { id: requireId(v.id), name: string(v.name, "file name"), contentType: string(v.contentType, "content type"), size: v.size,
    sha256: string(v.sha256, "checksum"), metadata: object(v.metadata, "file metadata"), status: string(v.status, "file status"), uploadExpiresAt: string(v.uploadExpiresAt, "upload expiry") };
}
/** @param {JsonValue | undefined} value @returns {SignedFileUrl} */
function signed(value) {
  const v = object(value, "signed file URL");
  const url = string(v.url, "signed URL");
  requireServiceUrl(url, "Signed file URL");
  /** @type {Record<string, string>} */
  const headers = {};
  for (const [key, val] of Object.entries(object(v.headers, "signed headers"))) {
    // Object-store capabilities must never carry the project's authorization or cookies.
    if (!["content-type", "content-length", "x-amz-checksum-sha256"].includes(key.toLowerCase())) malformed("signed upload header");
    headers[key] = string(val, "signed header value");
  }
  return { id: requireId(v.id), url, expiresAt: string(v.expiresAt, "signed URL expiry"), headers };
}
/** @param {FileListOptions} options @returns {URLSearchParams} */
function pageQuery(options) {
  const query = new URLSearchParams();
  if (options.after !== undefined) query.set("after", requireId(options.after));
  return query;
}

/** Project file buckets, authenticated by the same token as documents. */
export class V1FileStorage {
  /** @type {V1Transport} */ #transport;
  /** @type {FetchLike} */ #fetch;
  /** @param {V1Transport} transport @param {FetchLike} fetcher */
  constructor(transport, fetcher) { this.#transport = transport; this.#fetch = fetcher; Object.freeze(this); }
  /** @param {string} name @param {FileAccessRules} accessRules @param {FileRequestOptions} [options] @returns {Promise<FileBucketInfo>} */
  async createBucket(name, accessRules, options = {}) {
    return bucketInfo((await this.#transport.call("createFileBucket", {}, { body: stringifyExactJson({ name, rules: accessRules }, "file bucket"), contentType: "application/json", signal: options.signal })).value);
  }
  /** @param {FileListOptions} [options] @returns {Promise<{items: FileBucketInfo[], next: string | null}>} */
  async listBuckets(options = {}) {
    const v = object((await this.#transport.call("listFileBuckets", {}, { query: pageQuery(options), signal: options.signal })).value, "bucket page");
    return { items: array(v.items, "bucket items").map(bucketInfo), next: v.next === null ? null : requireId(v.next) };
  }
  /** @param {string} id @returns {V1FileBucket} */
  bucket(id) { return new V1FileBucket(this.#transport, this.#fetch, requireId(id)); }
}

/** Immutable files in one project bucket. */
export class V1FileBucket {
  /** @type {V1Transport} */ #transport;
  /** @type {FetchLike} */ #fetch;
  /** @type {string} */ #bucket;
  /** @param {V1Transport} transport @param {FetchLike} fetcher @param {string} bucket */
  constructor(transport, fetcher, bucket) { this.#transport = transport; this.#fetch = fetcher; this.#bucket = requireId(bucket); Object.freeze(this); }
  /** @param {FileAccessRules} accessRules @param {FileRequestOptions} [options] @returns {Promise<FileBucketInfo>} */
  async setRules(accessRules, options = {}) {
    return bucketInfo((await this.#transport.call("setFileBucketRules", { bucket: this.#bucket }, { body: stringifyExactJson({ rules: accessRules }, "file bucket rules"), contentType: "application/json", signal: options.signal })).value);
  }
  /** @param {FileRequestOptions} [options] @returns {Promise<void>} */
  async delete(options = {}) { await this.#transport.call("deleteFileBucket", { bucket: this.#bucket }, { signal: options.signal }); }
  /** @param {FileUploadRequest} request @param {FileRequestOptions} [options] @returns {Promise<SignedFileUrl>} */
  async createUpload(request, options = {}) {
    assertJsonObject(request.metadata, "file metadata");
    if (!Number.isSafeInteger(request.size) || request.size < 1 || request.size > maxFileBytes) throw new TypeError("Files must contain 1–104857600 bytes.");
    return signed((await this.#transport.call("createFileUpload", { bucket: this.#bucket }, { body: stringifyExactJson(request, "file upload"), contentType: "application/json", signal: options.signal })).value);
  }
  /** @param {string} id @param {FileRequestOptions} [options] @returns {Promise<StoredFile>} */
  async completeUpload(id, options = {}) {
    return storedFile((await this.#transport.call("completeFileUpload", { bucket: this.#bucket, file: requireId(id) }, { signal: options.signal })).value);
  }
  /** @param {FileListOptions} [options] @returns {Promise<{items: StoredFile[], next: string | null}>} */
  async list(options = {}) {
    const v = object((await this.#transport.call("listBucketFiles", { bucket: this.#bucket }, { query: pageQuery(options), signal: options.signal })).value, "file page");
    return { items: array(v.items, "file items").map(storedFile), next: v.next === null ? null : requireId(v.next) };
  }
  /** @param {string} id @param {FileRequestOptions} [options] @returns {Promise<SignedFileUrl>} */
  async downloadUrl(id, options = {}) {
    return signed((await this.#transport.call("createFileDownload", { bucket: this.#bucket, file: requireId(id) }, { signal: options.signal })).value);
  }
  /** @param {string} id @param {FileRequestOptions} [options] @returns {Promise<void>} */
  async deleteFile(id, options = {}) { await this.#transport.call("deleteBucketFile", { bucket: this.#bucket, file: requireId(id) }, { signal: options.signal }); }
  /** Hashes a private byte snapshot, uploads it without project credentials, and verifies completion. No retries.
   * @param {string} name @param {Uint8Array | Blob} input @param {FileUploadOptions} options @returns {Promise<StoredFile>} */
  async upload(name, input, options) {
    const size = input instanceof Blob ? input.size : input.byteLength;
    if (size < 1 || size > maxFileBytes) throw new TypeError("Files must contain 1–104857600 bytes.");
    const bytes = input instanceof Blob ? new Uint8Array(await input.arrayBuffer()) : new Uint8Array(input);
    options.signal?.throwIfAborted();
    const hash = new Uint8Array(await globalThis.crypto.subtle.digest("SHA-256", bytes));
    const sha256 = btoa(String.fromCharCode(...hash));
    const capability = await this.createUpload({ name, contentType: options.contentType ?? "application/octet-stream", size, sha256, metadata: options.metadata }, options);
    const headers = new Headers(capability.headers);
    // Fetch computes Content-Length from the body; browsers prohibit setting it explicitly.
    headers.delete("Content-Length");
    /** @type {RequestInit & { duplex?: "half" }} */
    const init = { method: "PUT", headers, body: bytes, credentials: "omit", redirect: "error", signal: options.signal ?? null };
    if (typeof process !== "undefined" && process.release?.name === "node") {
      // A one-shot stream prevents Node fetch from replaying the signed PUT on HTTP 421.
      headers.set("Content-Length", String(size));
      init.body = new ReadableStream({ start(controller) { controller.enqueue(bytes); controller.close(); } });
      init.duplex = "half";
    }
    const response = await this.#fetch(capability.url, init);
    void response.body?.cancel().catch(() => {});
    if (!response.ok) throw new Error(`Object upload failed (${response.status}).`);
    return this.completeUpload(capability.id, options);
  }
  /** Fetches file bytes without sending the project token to the object store.
   * @param {string} id @param {FileRequestOptions} [options] @returns {Promise<Blob>} */
  async download(id, options = {}) {
    const capability = await this.downloadUrl(id, options);
    const response = await this.#fetch(capability.url, { method: "GET", credentials: "omit", redirect: "error", signal: options.signal ?? null });
    if (!response.ok) throw new Error(`Object download failed (${response.status}).`);
    if (response.body === null) throw new Error("Object download returned no body.");
    const reader = response.body.getReader();
    /** @type {Uint8Array<ArrayBuffer>[]} */ const chunks = [];
    let length = 0;
    try {
      for (;;) {
        options.signal?.throwIfAborted();
        const part = await reader.read();
        if (part.done) break;
        length += part.value.byteLength;
        if (length > maxFileBytes) throw new Error("Object download exceeds the file size limit.");
        chunks.push(new Uint8Array(part.value));
      }
    } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
    return new Blob(chunks, { type: "application/octet-stream" });
  }
}
