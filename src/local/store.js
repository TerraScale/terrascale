import { mkdirSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import { dirname, isAbsolute } from "node:path";

const SCHEMA_VERSION = 1;

/**
 * A small synchronous JSON record store for Node local adapters.
 *
 * This module intentionally imports node:sqlite and must not be used from a
 * browser bundle. The database handle is kept private so consumers cannot
 * bypass the store's serialization and transaction invariants.
 */
export class TerraScaleSqliteStore {
  /**
   * @readonly
   * @type {string}
   */
  databasePath;

  /** @type {DatabaseSync} */
  #database;
  #closed = false;
  /** @type {Promise<void>} */
  #queue = Promise.resolve();
  #savepointSequence = 0;
  #transactionDepth = 0;

  /**
   * @param {string} databasePath
   */
  constructor(databasePath) {
    if (typeof databasePath !== "string") {
      throw new TypeError("databasePath must be a string.");
    }
    if (databasePath !== ":memory:" && !isAbsolute(databasePath)) {
      throw new TypeError(
        "databasePath must be ':memory:' or an absolute path.",
      );
    }

    if (databasePath !== ":memory:") {
      mkdirSync(dirname(databasePath), { recursive: true });
    }

    this.databasePath = databasePath;
    this.#database = new DatabaseSync(databasePath);

    try {
      // These are deliberately explicit PRAGMAs rather than relying only on
      // constructor options, which vary between supported Node 22 releases.
      this.#database.exec(
        "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;",
      );
      if (databasePath !== ":memory:") {
        this.#database.exec("PRAGMA journal_mode = WAL;");
      }
      this.#migrate();
    } catch (error) {
      this.#database.close();
      this.#closed = true;
      throw error;
    }
  }

  /** @returns {void} */
  close() {
    if (this.#closed) {
      return;
    }
    if (this.#transactionDepth !== 0) {
      throw new Error(
        "Cannot close a SQLite store while a transaction is active.",
      );
    }
    this.#database.close();
    this.#closed = true;
  }

  /** @returns {void} */
  reset() {
    this.#assertOpen();
    this.transaction(() => {
      this.#database.exec("DELETE FROM records; DELETE FROM metadata;");
    });
  }

  /**
   * @template T
   * @param {string} namespace
   * @param {string} scope
   * @param {string} id
   * @returns {T | undefined}
   */
  get(namespace, scope, id) {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");
    assertKey(id, "id");

    const row = this.#database
      .prepare(
        "SELECT value FROM records WHERE namespace = ? AND scope = ? AND id = ?",
      )
      .get(namespace, scope, id);
    if (row === undefined) {
      return undefined;
    }
    return /** @type {T} */ (parseJson(row.value, "record"));
  }

  /**
   * @template T
   * @param {string} namespace
   * @param {string} scope
   * @returns {T[]}
   */
  list(namespace, scope) {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");

    const rows = this.#database
      .prepare(
        "SELECT value FROM records WHERE namespace = ? AND scope = ? ORDER BY id ASC",
      )
      .all(namespace, scope);
    return rows.map((row) => /** @type {T} */ (parseJson(row.value, "record")));
  }

  /**
   * @template T
   * @param {string} namespace
   * @param {string} scope
   * @returns {readonly { readonly id: string; readonly value: T }[]}
   */
  listEntries(namespace, scope) {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");
    const rows = this.#database
      .prepare("SELECT id, value FROM records WHERE namespace = ? AND scope = ? ORDER BY id ASC")
      .all(namespace, scope);
    return rows.map(row => ({
      id: String(row.id),
      value: /** @type {T} */ (parseJson(row.value, "record")),
    }));
  }

  /**
   * @template T
   * @param {string} namespace
   * @returns {readonly {
   *   readonly scope: string;
   *   readonly id: string;
   *   readonly value: T;
   * }[]}
   */
  listNamespace(namespace) {
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
      value: /** @type {T} */ (parseJson(row.value, "record")),
    }));
  }

  /**
   * @template T
   * @param {string} namespace
   * @param {string} scope
   * @param {string} id
   * @param {T} value
   * @returns {void}
   */
  put(namespace, scope, id, value) {
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

  /**
   * @param {string} namespace
   * @param {string} scope
   * @param {string} id
   * @returns {boolean}
   */
  delete(namespace, scope, id) {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    assertKey(scope, "scope");
    assertKey(id, "id");

    const result = this.#database
      .prepare(
        "DELETE FROM records WHERE namespace = ? AND scope = ? AND id = ?",
      )
      .run(namespace, scope, id);
    return Number(result.changes) !== 0;
  }

  /**
   * @param {string} namespace
   * @returns {void}
   */
  clearNamespace(namespace) {
    this.#assertOpen();
    assertKey(namespace, "namespace");
    this.#database
      .prepare("DELETE FROM records WHERE namespace = ?")
      .run(namespace);
  }

  /**
   * @template T
   * @param {string} key
   * @returns {T | undefined}
   */
  getMeta(key) {
    this.#assertOpen();
    assertKey(key, "key");

    const row = this.#database
      .prepare("SELECT value FROM metadata WHERE key = ?")
      .get(key);
    if (row === undefined) {
      return undefined;
    }
    return /** @type {T} */ (parseJson(row.value, "metadata value"));
  }

  /**
   * @template T
   * @param {string} key
   * @param {T} value
   * @returns {void}
   */
  setMeta(key, value) {
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

  /**
   * Serialize local native requests and async auth callbacks on this connection.
   *
   * @template T
   * @param {() => Promise<T> | T} work
   * @returns {Promise<T>}
   */
  async exclusive(work) {
    const prior = this.#queue;
    /** @type {() => void} */
    let release = () => {};
    this.#queue = /** @type {Promise<void>} */ (
      new Promise((resolve) => {
        release = resolve;
      })
    );
    await prior;
    try {
      this.#assertOpen();
      return await work();
    } finally {
      release();
    }
  }

  /**
   * @template T
   * @param {() => Promise<T>} work
   * @returns {Promise<T>}
   */
  async transactionAsync(work) {
    return this.exclusive(async () => {
      if (this.#transactionDepth !== 0)
        throw new Error(
          "Nested asynchronous SQLite transaction is unavailable.",
        );
      this.#database.exec("BEGIN IMMEDIATE;");
      this.#transactionDepth += 1;
      try {
        const result = await work();
        this.#database.exec("COMMIT;");
        return result;
      } catch (error) {
        this.#rollback();
        throw error;
      } finally {
        this.#transactionDepth -= 1;
      }
    });
  }

  /**
   * @template T
   * @param {() => T} work
   * @returns {T}
   */
  transaction(work) {
    this.#assertOpen();
    if (typeof work !== "function") {
      throw new TypeError("transaction work must be a function.");
    }

    const nested = this.#transactionDepth !== 0;
    const savepoint = nested
      ? `terrascale_tx_${++this.#savepointSequence}`
      : undefined;
    if (savepoint === undefined) {
      this.#database.exec("BEGIN IMMEDIATE;");
    } else {
      this.#database.exec(`SAVEPOINT ${savepoint};`);
    }
    this.#transactionDepth += 1;

    try {
      const result = work();
      if (result instanceof Promise)
        throw new TypeError("Use transactionAsync for asynchronous callbacks.");
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

  /** @returns {void} */
  #migrate() {
    this.#database.exec("BEGIN IMMEDIATE;");
    try {
      this.#database.exec(`
        CREATE TABLE IF NOT EXISTS schema_migrations (
          version INTEGER NOT NULL PRIMARY KEY,
          applied_at TEXT NOT NULL
        ) WITHOUT ROWID;
      `);

      const row = this.#database
        .prepare(
          "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1",
        )
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
          .prepare(
            "INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)",
          )
          .run(SCHEMA_VERSION, new Date().toISOString());
      }
      this.#database.exec("COMMIT;");
    } catch (error) {
      this.#rollback();
      throw error;
    }
  }

  /**
   * @param {string | undefined} [savepoint]
   * @returns {void}
   */
  #rollback(savepoint = undefined) {
    try {
      if (savepoint === undefined) {
        this.#database.exec("ROLLBACK;");
      } else {
        this.#database.exec(
          `ROLLBACK TO SAVEPOINT ${savepoint}; RELEASE SAVEPOINT ${savepoint};`,
        );
      }
    } catch {
      // Preserve the original work/migration exception. SQLite will normally
      // have already rolled back a failed transaction automatically.
    }
  }

  /** @returns {void} */
  #assertOpen() {
    if (this.#closed) {
      throw new Error("The SQLite store is closed.");
    }
  }
}

/**
 * @param {string} value
 * @param {string} name
 * @returns {void}
 */
function assertKey(value, name) {
  if (typeof value !== "string") {
    throw new TypeError(`${name} must be a string.`);
  }
  if (value.includes("\u0000")) {
    throw new TypeError(`${name} must not contain a NUL character.`);
  }
}

/**
 * @param {unknown} value
 * @returns {string}
 */
function stringifyJson(value) {
  try {
    assertJsonValue(value, new WeakSet());
    const encoded = JSON.stringify(value);
    if (encoded === undefined) {
      throw new TypeError("Value must be JSON serializable.");
    }
    // JSON.stringify is expected to produce valid JSON, but parsing here
    // keeps the invariant explicit before handing data to SQLite.
    JSON.parse(encoded);
    return encoded;
  } catch (error) {
    if (
      error instanceof TypeError &&
      error.message === "Value must be JSON serializable."
    ) {
      throw error;
    }
    throw new TypeError("Value must be JSON serializable.", { cause: error });
  }
}

/**
 * @param {unknown} value
 * @param {WeakSet<object>} ancestors
 * @returns {void}
 */
function assertJsonValue(value, ancestors) {
  if (
    value === null ||
    typeof value === "string" ||
    typeof value === "boolean"
  ) {
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
    const objectValue = /** @type {Record<string, unknown>} */ (value);
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

/**
 * @param {unknown} encoded
 * @param {string} label
 * @returns {unknown}
 */
function parseJson(encoded, label) {
  if (typeof encoded !== "string") {
    throw new Error(`Invalid ${label}: SQLite value is not text.`);
  }
  try {
    return JSON.parse(encoded);
  } catch (error) {
    throw new Error(`Invalid ${label}: stored JSON is malformed.`, {
      cause: error,
    });
  }
}
