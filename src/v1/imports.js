import { requireId } from "../ids.js";
import { requireName } from "./ids.js";
import { flag, list, nullable, numeric, record, text } from "./management-decode.js";
import { members, mutationOptions } from "./management-validation.js";
import { stringifyExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
// Producer 841dd0d99, Features/Import/ImportModels.cs (also docs/terrabase/import-ndjson.md).
const importOptions = record({ exclude: nullable(list(text)), collection: nullable(text) });
const column = record({ name: text, sourceType: text, mapping: text, nullable: flag, excluded: flag });
const constraint = record({ kind: text, name: nullable(text), columns: list(text), references: nullable(text), enforced: flag });
const issue = record({ severity: text, code: text, column: nullable(text), row: nullable(numeric), message: text });
const table = record({ source: text, collection: text, excluded: flag, rows: numeric, primaryKey: list(text),
  columns: list(column), constraints: list(constraint), issues: list(issue) });
const preview = record({ id: requireId, tables: list(table), skipped: list(record({ kind: text, name: text })), skippedTruncated: flag, blocking: numeric, warnings: numeric });
const progress = record({ tablesDone: numeric, tablesTotal: numeric, rowsWritten: numeric, rowsTotal: numeric, currentTable: nullable(text) });
const validation = record({ passed: flag, tables: list(record({ source: text, collection: text, sourceRows: numeric, documents: numeric,
  sourceChecksum: text, documentChecksum: text, match: flag })) });
const error = record({ code: text, message: text, retryable: flag, table: nullable(text), row: nullable(numeric), column: nullable(text) });
const job = record({ id: requireId, project: requireId, database: text, region: text, state: text, cancelRequested: flag, options: importOptions,
  source: record({ format: text, bytes: numeric, sha256: text, receivedBytes: numeric, staged: flag, retainedUntil: nullable(text) }),
  preview: nullable(preview), confirmedPreviewId: nullable(requireId), progress: nullable(progress), validation: nullable(validation), error: nullable(error),
  createdAt: text, updatedAt: text, startedAt: nullable(text), finishedAt: nullable(text) });
/** @typedef {ReturnType<typeof job>} V1ImportJob */
/** @typedef {{ readonly exclude?: readonly string[] | null; readonly collection?: string | null }} V1ImportOptions */
/** @typedef {{ readonly format: "postgres_plain" | "sqlite" | "ndjson"; readonly sourceBytes: number; readonly sourceSha256: string; readonly options?: V1ImportOptions | null }} V1CreateImportRequest */

/** ImportJobs.Normalize / ImportNames.Parse: preserve quoted identifiers and let the producer canonicalize them.
 * @param {V1ImportOptions | null | undefined} options */
function validateOptions(options) {
  if (options === undefined || options === null) return;
  members(options, ["exclude", "collection"]);
  const { exclude, collection } = options;
  if (exclude !== undefined && exclude !== null) {
    if (!Array.isArray(exclude) || exclude.length > 1000 || exclude.some(entry => typeof entry !== "string" || entry.length > 512 ||
        !/^(?:"(?:[^"]|"")+"|[^".]+)(?:\.(?:"(?:[^"]|"")+"|[^".]+)){1,2}(?![\s\S])/u.test(entry)) ||
        exclude.reduce((size, entry) => size + entry.length, 0) > 65536)
      throw new TypeError("options.exclude must contain at most 1000 schema.table or schema.table.column names totaling at most 64 KiB.");
  }
  if (collection !== undefined && collection !== null) {
    requireName("collection", collection);
    if (/^(?:sqlite_|turso_)/u.test(collection) || ["collections", "commit_offset", "terrascale-import"].includes(collection))
      throw new TypeError("options.collection must be an unreserved collection name.");
  }
}

/** One import job belongs to its destination database; preview confirmation is explicit. */
export class V1Imports {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireName("database", database) }; Object.freeze(this); }
  /** @param {V1CreateImportRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async create(request, options = {}) {
    members(request, ["format", "sourceBytes", "sourceSha256", "options"]);
    if (!["postgres_plain", "sqlite", "ndjson"].includes(request.format) || !Number.isSafeInteger(request.sourceBytes) || request.sourceBytes < 1 || request.sourceBytes > 1073741824 ||
        typeof request.sourceSha256 !== "string" || !/^[a-f0-9]{64}$/u.test(request.sourceSha256)) throw new TypeError("Invalid import format, size or SHA-256.");
    validateOptions(request.options);
    mutationOptions(options);
    return job((await this.#transport.call("createImport", this.#params, { body: stringifyExactJson(request, "import create"), signal: options.signal })).value);
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async get(options = {}) { return job((await this.#transport.call("getImport", this.#params, { signal: options.signal })).value); }
  /** @param {number | bigint} offset @param {Uint8Array} bytes @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async uploadSource(offset, bytes, options = {}) {
    if (!(typeof offset === "bigint" || typeof offset === "number" && Number.isSafeInteger(offset)) || offset < 0 || BigInt(offset) > 9223372036854775807n)
      throw new TypeError("offset must be a non-negative signed 64-bit integer.");
    if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > 4194304) throw new TypeError("A source part must contain 1-4194304 bytes.");
    mutationOptions(options);
    return job((await this.#transport.call("uploadImportSource", this.#params, {
      body: bytes.slice(), contentType: "application/octet-stream", query: new URLSearchParams({ offset: String(offset) }), signal: options.signal,
    })).value);
  }
  /** @param {{readonly options?: V1ImportOptions | null}} request @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async update(request, options = {}) {
    members(request, ["options"]);
    validateOptions(request.options);
    mutationOptions(options);
    return job((await this.#transport.call("updateImport", this.#params, { body: stringifyExactJson(request, "import update"), signal: options.signal })).value);
  }
  /** @param {{readonly previewId: string}} request @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async start(request, options = {}) {
    members(request, ["previewId"]);
    requireId(request.previewId, "Preview ID");
    mutationOptions(options);
    return job((await this.#transport.call("startImport", this.#params, { body: stringifyExactJson(request, "import start"), signal: options.signal })).value);
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async cancel(options = {}) { mutationOptions(options); return job((await this.#transport.call("cancelImport", this.#params, { signal: options.signal })).value); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ImportJob>} */
  async resume(options = {}) { mutationOptions(options); return job((await this.#transport.call("resumeImport", this.#params, { signal: options.signal })).value); }
}
