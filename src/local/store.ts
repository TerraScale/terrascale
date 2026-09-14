import { mkdirSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import { dirname, isAbsolute } from "node:path";

import { isUlid, toUlid, type Ulid } from "../ids.js";

const SCHEMA_VERSION = 1;
const ULID_STATE_KEY = "__local_ulid_state";
const ULID_TIME_MAX = 0xffffffffffff;
const ULID_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/**
 * A small synchronous JSON record store for Node local adapters.
 *
 * This module intentionally imports node:sqlite and must not be used from a
 * browser bundle. The database handle is kept private so consumers cannot
 * bypass the store's serialization and transaction invariants.
 */
export class TerraScaleSqliteStore {
  readonly databasePath: string;

  #database: DatabaseSync;
  #closed = false;
  #lastUlidTime: number | undefined;
  #lastUlidRandom: Uint8Array | undefined;
  #savepointSequence = 0;
  #transactionDepth = 0;

  constructor(databasePath: string) {
    if (typeof databasePath !== "string") {
      throw new TypeError("databasePath must be a string.");
    }
    if (databasePath !== ":memory:" && !isAbsolute(databasePath)) {
      throw new TypeError("databasePath must be ':memory:' or an absolute path.");
    }

    if (databasePath !== ":memory:") {
      mkdirSync(dirname(databasePath), { recursive: true });
    }

    this.databasePath = databasePath;
    this.#database = new DatabaseSync(databasePath);

    try {
      // These are deliberately explicit PRAGMAs rather than relying only on
      // constructor options, which vary between supported Node 22 releases.
      this.#database.exec("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
      if (databasePath !== ":memory:") {
        this.#database.exec("PRAGMA journal_mode = WAL;");
      }
      this.#migrate();
      const ulidState = this.getMeta<unknown>(ULID_STATE_KEY);
      if (isStoredUlidState(ulidState)) {
        this.#lastUlidTime = ulidState.time;
        this.#lastUlidRandom = decodeHex(ulidState.random);
      }
    } catch (error) {
      this.#database.close();
      this.#closed = true;
      throw error;
    }
  }

  close(): void {
    if (this.#closed) {
      return;
    }
    if (this.#transactionDepth !== 0) {
      throw new Error("Cannot close a SQLite store while a transaction is active.");
    }
    this.#database.close();
    this.#closed = true;
  }

  reset(): void {
    this.#assertOpen();
    this.transaction(() => {
      this.#database.exec("DELETE FROM records; DELETE FROM metadata;");
    });
    this.#lastUlidTime = undefined;
    this.#lastUlidRandom = undefined;
  }

  get<T>(namespace: string, scope: string, id: string): T | undefined {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");
    assertKey(id, "id");

    const row = this.#database
      .prepare("SELECT value FROM records WHERE namespace = ? AND scope = ? AND id = ?")
      .get(namespace, scope, id);
    if (row === undefined) {
      return undefined;
    }
    return parseJson<T>(row.value, "record");
  }

  list<T>(namespace: string, scope: string): T[] {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");

    const rows = this.#database
      .prepare(
        "SELECT value FROM records WHERE namespace = ? AND scope = ? ORDER BY id ASC",
      )
      .all(namespace, scope);
    return rows.map((row) => parseJson<T>(row.value, "record"));
  }

  listNamespace<T>(
    namespace: string,
  ): readonly { readonly scope: string; readonly id: string; readonly value: T }[] {
    this.#assertOpen();
    assertKey(namespace, "namespace");

    const rows = this.#database
      .prepare(
        "SELECT scope, id, value FROM records WHERE namespace = ? ORDER BY scope ASC, id ASC",
      )
      .all(namespace);
    return rows.map((row) => ({
      scope: String(row.scope),
      id: String(row.id),
      value: parseJson<T>(row.value, "record"),
    }));
  }

  put<T>(namespace: string, scope: string, id: string, value: T): void {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");
    assertKey(id, "id");
    const encoded = stringifyJson(value);

    this.#database
      .prepare(
        `INSERT INTO records (namespace, scope, id, value)
         VALUES (?, ?, ?, ?)
         ON CONFLICT(namespace, scope, id) DO UPDATE SET value = excluded.value`,
      )
      .run(namespace, scope, id, encoded);
  }

  delete(namespace: string, scope: string, id: string): boolean {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");
    assertKey(id, "id");

    const result = this.#database
      .prepare("DELETE FROM records WHERE namespace = ? AND scope = ? AND id = ?")
      .run(namespace, scope, id);
    return Number(result.changes) !== 0;
  }

  clearNamespace(namespace: string): void {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    this.#database.prepare("DELETE FROM records WHERE namespace = ?").run(namespace);
  }

  getMeta<T>(key: string): T | undefined {
    this.#assertOpen();
    assertKey(key, "key");

    const row = this.#database.prepare("SELECT value FROM metadata WHERE key = ?").get(key);
    if (row === undefined) {
      return undefined;
    }
    return parseJson<T>(row.value, "metadata value");
  }

  setMeta<T>(key: string, value: T): void {
    this.#assertOpen();
    assertKey(key, "key");
    const encoded = stringifyJson(value);

    this.#database
      .prepare(
        `INSERT INTO metadata (key, value)
         VALUES (?, ?)
         ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
      )
      .run(key, encoded);
  }

  nextUlid(): string {
    this.#assertOpen();

    let timestamp = Date.now();
    const previousTimestamp = this.#lastUlidTime;
    let random: Uint8Array;

    if (previousTimestamp !== undefined && timestamp <= previousTimestamp) {
      timestamp = previousTimestamp;
      random = this.#lastUlidRandom ?? randomBytes(10);
      if (!incrementRandom(random)) {
        if (timestamp >= ULID_TIME_MAX) {
          throw new RangeError("Unable to generate a ULID: timestamp is exhausted.");
        }
        timestamp += 1;
        random = randomBytes(10);
      }
    } else {
      random = randomBytes(10);
    }

    this.#lastUlidTime = timestamp;
    this.#lastUlidRandom = random;
    const value = `${encodeTimestamp(timestamp)}${encodeRandom(random)}`;
    // Keep this check tied to the SDK's canonical ULID contract. It also
    // guards this generator if the encoding implementation is changed later.
    const ulid: Ulid = toUlid(value);
    if (!isUlid(ulid)) {
      throw new Error("Generated an invalid ULID.");
    }
    this.setMeta(ULID_STATE_KEY, { time: timestamp, random: encodeHex(random) });
    return ulid;
  }

  transaction<T>(work: () => T): T {
    this.#assertOpen();
    if (typeof work !== "function") {
      throw new TypeError("transaction work must be a function.");
    }

    const nested = this.#transactionDepth !== 0;
    const savepoint = nested ? `terrascale_tx_${++this.#savepointSequence}` : undefined;
    if (savepoint === undefined) {
      this.#database.exec("BEGIN IMMEDIATE;");
    } else {
      this.#database.exec(`SAVEPOINT ${savepoint};`);
    }
    this.#transactionDepth += 1;

    try {
      const result = work();
      if (savepoint === undefined) {
        this.#database.exec("COMMIT;");
      } else {
        this.#database.exec(`RELEASE SAVEPOINT ${savepoint};`);
      }
      this.#transactionDepth -= 1;
      return result;
    } catch (error) {
      this.#rollback(savepoint);
      this.#transactionDepth -= 1;
      throw error;
    }
  }

  #migrate(): void {
    this.#database.exec("BEGIN IMMEDIATE;");
    try {
      this.#database.exec(`
        CREATE TABLE IF NOT EXISTS schema_migrations (
          version INTEGER NOT NULL PRIMARY KEY,
          applied_at TEXT NOT NULL
        ) WITHOUT ROWID;
      `);

      const row = this.#database
        .prepare("SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1")
        .get();
      const version = row?.version;
      if (version !== undefined && typeof version !== "number") {
        throw new Error("Invalid schema migration version.");
      }
      if (version !== undefined && version > SCHEMA_VERSION) {
        throw new Error(
          `Database schema version ${version} is newer than supported version ${SCHEMA_VERSION}.`,
        );
      }

      if (version === undefined) {
        this.#database.exec(`
          CREATE TABLE records (
            namespace TEXT NOT NULL,
            scope TEXT NOT NULL,
            id TEXT NOT NULL,
            value TEXT NOT NULL CHECK (json_valid(value) = 1),
            PRIMARY KEY (namespace, scope, id)
          ) WITHOUT ROWID;

          CREATE TABLE metadata (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL CHECK (json_valid(value) = 1)
          ) WITHOUT ROWID;
        `);
        this.#database
          .prepare("INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)")
          .run(SCHEMA_VERSION, new Date().toISOString());
      }
      this.#database.exec("COMMIT;");
    } catch (error) {
      this.#rollback();
      throw error;
    }
  }

  #rollback(savepoint: string | undefined = undefined): void {
    try {
      if (savepoint === undefined) {
        this.#database.exec("ROLLBACK;");
      } else {
        this.#database.exec(`ROLLBACK TO SAVEPOINT ${savepoint}; RELEASE SAVEPOINT ${savepoint};`);
      }
    } catch {
      // Preserve the original work/migration exception. SQLite will normally
      // have already rolled back a failed transaction automatically.
    }
  }

  #assertOpen(): void {
    if (this.#closed) {
      throw new Error("The SQLite store is closed.");
    }
  }
}

function isStoredUlidState(value: unknown): value is { readonly time: number; readonly random: string } {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const state = value as { readonly time?: unknown; readonly random?: unknown };
  return typeof state.time === "number"
    && Number.isSafeInteger(state.time)
    && state.time >= 0
    && state.time <= ULID_TIME_MAX
    && typeof state.random === "string"
    && /^[0-9a-f]{20}$/u.test(state.random);
}

function encodeHex(value: Uint8Array): string {
  return [...value].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function decodeHex(value: string): Uint8Array {
  const result = new Uint8Array(10);
  for (let index = 0; index < result.length; index++) {
    result[index] = Number.parseInt(value.slice(index * 2, index * 2 + 2), 16);
  }
  return result;
}

function assertKey(value: string, name: string): void {
  if (typeof value !== "string") {
    throw new TypeError(`${name} must be a string.`);
  }
  if (value.includes("\u0000")) {
    throw new TypeError(`${name} must not contain a NUL character.`);
  }
}

function stringifyJson(value: unknown): string {
  try {
    assertJsonValue(value, new WeakSet<object>());
    const encoded = JSON.stringify(value);
    if (encoded === undefined) {
      throw new TypeError("Value must be JSON serializable.");
    }
    // JSON.stringify is expected to produce valid JSON, but parsing here
    // keeps the invariant explicit before handing data to SQLite.
    JSON.parse(encoded);
    return encoded;
  } catch (error) {
    if (error instanceof TypeError && error.message === "Value must be JSON serializable.") {
      throw error;
    }
    throw new TypeError("Value must be JSON serializable.", { cause: error });
  }
}

function assertJsonValue(value: unknown, ancestors: WeakSet<object>): void {
  if (value === null || typeof value === "string" || typeof value === "boolean") {
    return;
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw new TypeError("JSON numbers must be finite.");
    }
    return;
  }
  if (
    typeof value === "undefined" ||
    typeof value === "bigint" ||
    typeof value === "function" ||
    typeof value === "symbol"
  ) {
    throw new TypeError("Value contains a non-JSON value.");
  }
  if (typeof value !== "object") {
    throw new TypeError("Value contains a non-JSON value.");
  }
  if (ancestors.has(value)) {
    throw new TypeError("Value contains a circular reference.");
  }
  ancestors.add(value);
  if (Array.isArray(value)) {
    for (const item of value) {
      assertJsonValue(item, ancestors);
    }
  } else {
    const objectValue = value as Record<string, unknown>;
    for (const key of Object.keys(value)) {
      assertJsonValue(objectValue[key], ancestors);
    }
    for (const symbol of Object.getOwnPropertySymbols(value)) {
      if (Object.prototype.propertyIsEnumerable.call(value, symbol)) {
        throw new TypeError("Value contains a symbol property.");
      }
    }
  }
  ancestors.delete(value);
}

function parseJson<T>(encoded: unknown, label: string): T {
  if (typeof encoded !== "string") {
    throw new Error(`Invalid ${label}: SQLite value is not text.`);
  }
  try {
    return JSON.parse(encoded) as T;
  } catch (error) {
    throw new Error(`Invalid ${label}: stored JSON is malformed.`, { cause: error });
  }
}

function incrementRandom(random: Uint8Array): boolean {
  for (let index = random.length - 1; index >= 0; index -= 1) {
    const current = random[index];
    if (current === undefined) {
      throw new Error("Invalid ULID random state.");
    }
    if (current < 0xff) {
      random[index] = current + 1;
      return true;
    }
    random[index] = 0;
  }
  return false;
}

function encodeTimestamp(timestamp: number): string {
  let remaining = timestamp;
  const encoded = new Array<string>(10);
  for (let index = encoded.length - 1; index >= 0; index -= 1) {
    const digit = remaining % 32;
    encoded[index] = ULID_ALPHABET[digit] ?? "";
    remaining = Math.floor(remaining / 32);
  }
  if (remaining !== 0 || encoded.some((character) => character.length !== 1)) {
    throw new RangeError("Unable to encode ULID timestamp.");
  }
  return encoded.join("");
}

function encodeRandom(random: Uint8Array): string {
  if (random.length !== 10) {
    throw new Error("Invalid ULID random state.");
  }
  let value = 0n;
  for (const byte of random) {
    value = (value << 8n) | BigInt(byte);
  }

  const encoded = new Array<string>(16);
  for (let index = encoded.length - 1; index >= 0; index -= 1) {
    encoded[index] = ULID_ALPHABET[Number(value & 31n)] ?? "";
    value >>= 5n;
  }
  if (value !== 0n || encoded.some((character) => character.length !== 1)) {
    throw new Error("Unable to encode ULID randomness.");
  }
  return encoded.join("");
}
