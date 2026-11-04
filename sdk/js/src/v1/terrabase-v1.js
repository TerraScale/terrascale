// The TerraBase v1 client: SQL over REST and WebSocket, SQL table shapes, backups and usage
// for the project-scoped C# API (sdk/SQL-API.md).
import { V1FileStorage } from "./file-storage.js";
import { requireId } from "../ids.js";
import { requireDatabaseSelector } from "./ids.js";
import { executeSql, sqlRowObjects, sqlStatement } from "./sql-execute.js";
import { V1TableShape } from "./table-shape.js";
import { V1Databases } from "./catalog.js";
import { V1Transport } from "./transport.js";
import { V1Health, V1Platform } from "./system.js";
import { V1Backups, V1BackupDestinationClient } from "./backups.js";
import { V1ProjectUsageClient, V1DatabaseUsageClient } from "./usage.js";
import { V1SavedQueries } from "./saved-queries.js";
/** @import { TerraBaseV1ClientConfig } from "./transport.js" */

/** @typedef {{ readonly signal?: AbortSignal | undefined }} V1RequestOptions */

/** A database handle by current name or immutable ULID. It performs no I/O until an operation runs. */
export class V1Database {
  /** @type {V1Backups} */
  backups;
  /** @type {V1DatabaseUsageClient} */
  metrics;
  /** @type {V1Transport} */
  #transport;
  /** @type {string} */
  name;

  /**
   * @param {V1Transport} transport
   * @param {string} name
   */
  constructor(transport, name) {
    this.#transport = transport;
    this.name = requireDatabaseSelector(name);
    this.metrics = new V1DatabaseUsageClient(transport, this.name);
    this.backups = new V1Backups(transport, this.name);
    Object.freeze(this);
  }

  /**
   * Runs one relational SQL statement as its own transaction. Reads need the read scope; writes and DDL also need the write scope.
   * @param {string} sql
   * @param {import("./sql-execute.js").V1SqlArguments} [args] Positional array, or named values keyed without their prefix.
   * @param {V1RequestOptions} [options]
   * @returns {Promise<import("./sql-execute.js").V1SqlExecuteResult>}
   */
  async execute(sql, args, options = {}) {
    const [result] = await executeSql(this.#transport, this.name, [sqlStatement(sql, args)], options);
    return /** @type {import("./sql-execute.js").V1SqlExecuteResult} */ (result);
  }

  /**
   * Runs one statement and returns its rows as objects keyed by column name.
   * @template [T=Record<string, import("./sql-execute.js").V1SqlValue>]
   * @param {string} sql
   * @param {import("./sql-execute.js").V1SqlArguments} [args]
   * @param {V1RequestOptions} [options]
   * @returns {Promise<T[]>}
   */
  async query(sql, args, options = {}) {
    return sqlRowObjects(await this.execute(sql, args, options));
  }

  /**
   * Runs 1 to 256 statements as one transaction: all commit or none do. A refused statement throws SqlError.
   * @param {readonly (string | import("./sql-execute.js").V1SqlStatement)[]} statements
   * @param {V1RequestOptions} [options]
   */
  batch(statements, options = {}) {
    return executeSql(this.#transport, this.name, statements, options);
  }

  /**
   * A SQL table's shape: `shape("todos", { where: "done = 0" }).subscribe(({ rows }) => …)` syncs the matching rows, then live changes.
   * @param {string} table
   * @param {import("./table-shape.js").V1TableShapeRequest} [request]
   * @returns {V1TableShape}
   */
  shape(table, request = {}) {
    return new V1TableShape(this.#transport, this.name, table, request);
  }

  /**
   * Opens a SQL WebSocket session: lower latency per statement and interactive transactions.
   * @param {V1RequestOptions} [options]
   * @returns {Promise<import("./sql-socket.js").V1SqlSocket>}
   */
  connect(options = {}) {
    return this.#transport.openSqlSocket({ database: this.name }, options.signal);
  }
}


/**
 * A TerraBase v1 data-plane client for one project and access token. The token is held
 * privately; it is never serialized, logged or forwarded to redirects.
 */
export class TerraBaseV1Client {
  /** @type {V1FileStorage} */
  #storage;
  /** @type {V1Health} */
  #health;
  /** @type {V1Platform} */
  #platform;
  /** @type {V1BackupDestinationClient} */
  #backupDestination;
  /** @type {V1ProjectUsageClient} */
  #metrics;
  /** @type {V1SavedQueries} */
  #savedQueries;
  /** @type {V1Databases} */
  #databases;
  /** @type {V1Transport} */
  #transport;

  /** @param {TerraBaseV1ClientConfig} config */
  constructor(config) {
    requireId(config.project);
    this.#transport = new V1Transport(config);
    this.#storage = new V1FileStorage(this.#transport, config.fetch ?? globalThis.fetch.bind(globalThis));
    this.#databases = new V1Databases(this.#transport);
    this.#savedQueries = new V1SavedQueries(this.#transport);
    this.#metrics = new V1ProjectUsageClient(this.#transport);
    this.#backupDestination = new V1BackupDestinationClient(this.#transport);
    this.#health = new V1Health(this.#transport);
    this.#platform = new V1Platform(this.#transport);
    Object.freeze(this);
  }

  /**
   * @param {string} name
   * @returns {V1Database}
   */
  database(name) {
    return new V1Database(this.#transport, name);
  }

  /** @returns {V1Databases} Project-scoped database catalog. */
  get databases() { return this.#databases; }
  /** @returns {V1FileStorage} Project file buckets. */
  get storage() { return this.#storage; }
  /** @returns {V1SavedQueries} */
  get savedQueries() { return this.#savedQueries; }
  /** @returns {V1ProjectUsageClient} */
  get metrics() { return this.#metrics; }
  /** @returns {V1BackupDestinationClient} */
  get backupDestination() { return this.#backupDestination; }
  /** @returns {V1Health} */
  get health() { return this.#health; }
  /** @returns {V1Platform} */
  get platform() { return this.#platform; }
}

/**
 * Creates a project-scoped TerraBase client for SQL, table shapes and catalog operations.
 * @param {TerraBaseV1ClientConfig} config
 * @returns {TerraBaseV1Client}
 */
export function createTerraBaseV1Client(config) {
  return new TerraBaseV1Client(config);
}
