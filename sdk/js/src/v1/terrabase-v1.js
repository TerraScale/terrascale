// The TerraBase v1 client: SQL over REST and WebSocket, SQL table shapes, backups and usage
// for the project-scoped C# API (sdk/SQL-API.md).
import { V1FileStorage } from "./file-storage.js";
import { requireId } from "../ids.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { executeSql, sqlRowObjects, sqlStatement } from "./sql-execute.js";
import { V1TableShape } from "./table-shape.js";
import { decodeManagedDatabase, region, requirePlacement, requireTtlSeconds, V1Databases } from "./catalog.js";
import { awaitRestoreOperation, restoreOperation } from "./backups.js";
import { TerraBaseV1Error } from "./errors.js";
import { members } from "./management-validation.js";
import { object, string } from "./decode.js";
import { stringifyExactJson } from "./json.js";
import { V1Transport } from "./transport.js";
import { V1Health, V1Platform, V1Regions } from "./system.js";
import { V1Backups, V1BackupDestinationClient } from "./backups.js";
import { V1ProjectUsageClient, V1DatabaseUsageClient } from "./usage.js";
import { V1SavedQueries } from "./saved-queries.js";
/** @import { TerraBaseV1ClientConfig } from "./transport.js" */

/** @typedef {{ readonly ttlSeconds?: number | undefined; readonly signal?: AbortSignal | undefined }} V1AgentDatabaseOptions */
/** @typedef {{ readonly replicaRegions?: readonly string[] | undefined; readonly allRegions?: boolean | undefined }} V1DatabasePlacement */
/** @typedef {V1AgentDatabaseOptions & V1DatabasePlacement & { readonly homeRegion?: string | undefined }} V1CreateDatabaseOptions */
/** @typedef {V1AgentDatabaseOptions & { readonly pointInTime?: string | undefined }} V1ForkDatabaseOptions */
/** @typedef {{ readonly project?: string | undefined; readonly database: string; readonly access: "read" | "read_write"; readonly ttlSeconds?: number | undefined }} V1ScopedKeyRequest */
/** @typedef {{ readonly key: string; readonly id: string; readonly projectId: string; readonly database: string; readonly access: "read" | "read_write"; readonly expiresAt: string }} V1ScopedKey */

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
   * Streams the database as one SQL dump (BEGIN … COMMIT) from one read snapshot; needs the read scope.
   * The caller consumes the returned body and cancels it when done.
   * @param {V1RequestOptions} [options]
   * @returns {Promise<ReadableStream<Uint8Array>>}
   */
  exportDatabase(options = {}) {
    return this.#transport.stream("exportDatabase", { database: this.name }, options.signal);
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
  /** @type {V1Regions} */
  #regions;
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

  /** @type {TerraBaseV1ClientConfig} */
  #config;
  /** @type {Map<string, V1Transport>} */
  #projects = new Map();

  /** @param {TerraBaseV1ClientConfig} config */
  constructor(config) {
    requireId(config.project);
    this.#config = Object.freeze({ ...config });
    this.#transport = new V1Transport(config);
    this.#projects.set(config.project, this.#transport);
    this.#storage = new V1FileStorage(this.#transport, config.fetch ?? globalThis.fetch.bind(globalThis));
    this.#databases = new V1Databases(this.#transport);
    this.#savedQueries = new V1SavedQueries(this.#transport);
    this.#metrics = new V1ProjectUsageClient(this.#transport);
    this.#backupDestination = new V1BackupDestinationClient(this.#transport);
    this.#health = new V1Health(this.#transport);
    this.#platform = new V1Platform(this.#transport);
    this.#regions = new V1Regions(this.#transport);
    Object.freeze(this);
  }

  /**
   * @param {string} name
   * @returns {V1Database}
   */
  database(name) {
    return new V1Database(this.#transport, name);
  }

  /** @param {string} project @returns {V1Transport} */
  #projectTransport(project) {
    requireId(project, "Project ID");
    let transport = this.#projects.get(project);
    if (transport === undefined) {
      transport = new V1Transport({ ...this.#config, project });
      this.#projects.set(project, transport);
    }
    return transport;
  }

  /** @param {string} project @param {string} name @param {V1CreateDatabaseOptions} [options] @returns {Promise<import("./catalog.js").V1ManagedDatabase>} */
  createDatabase(project, name, options = {}) {
    members(options, ["homeRegion", "replicaRegions", "allRegions", "ttlSeconds", "signal"]);
    return new V1Databases(this.#projectTransport(project)).create({ name,
      ...(options.homeRegion === undefined ? {} : { homeRegion: options.homeRegion }),
      ...(options.replicaRegions === undefined ? {} : { replicaRegions: options.replicaRegions }),
      ...(options.allRegions === undefined ? {} : { allRegions: options.allRegions }),
      ...(options.ttlSeconds === undefined ? {} : { ttlSeconds: options.ttlSeconds }),
    }, options.signal === undefined ? {} : { signal: options.signal });
  }

  /** @param {string} project @param {string} database @param {V1DatabasePlacement} placement @param {V1RequestOptions} [options] @returns {Promise<import("./catalog.js").V1ManagedDatabase>} */
  async setDatabasePlacement(project, database, placement, options = {}) {
    members(placement, ["replicaRegions", "allRegions"]);
    const body = requirePlacement(placement);
    if (Object.keys(body).length === 0) throw new TypeError("Supply replicaRegions or allRegions.");
    const response = await this.#projectTransport(project).call("updateDatabase", { database: requireDatabaseSelector(database) }, {
      body: stringifyExactJson(body, "database placement"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }

  /** @param {string} project @param {string} database @param {string} homeRegion @param {V1RequestOptions} [options] @returns {Promise<import("./catalog.js").V1ManagedDatabase>} */
  async moveDatabaseHome(project, database, homeRegion, options = {}) {
    const transport = this.#projectTransport(project);
    const params = { database: requireDatabaseSelector(database) };
    const body = stringifyExactJson({ homeRegion: region(homeRegion) }, "database move");
    for (let attempt = 0; ; attempt++) {
      try {
        return decodeManagedDatabase((await transport.call("moveDatabaseHome", params, { body, signal: options.signal })).value);
      } catch (error) {
        if (!(error instanceof TerraBaseV1Error) || error.status !== 503 || error.code !== "move_unresolved" || attempt >= 5) throw error;
        options.signal?.throwIfAborted();
        await new Promise((resolve, reject) => {
          const timer = setTimeout(() => { options.signal?.removeEventListener("abort", abort); resolve(undefined); }, (error.retryAfter ?? 1) * 1000);
          const abort = () => { clearTimeout(timer); reject(options.signal?.reason); };
          options.signal?.addEventListener("abort", abort, { once: true });
        });
      }
    }
  }

  /** @param {string} project @param {string} source @param {string} name @param {V1ForkDatabaseOptions} [options] @returns {Promise<import("./catalog.js").V1ManagedDatabase>} */
  async forkDatabase(project, source, name, options = {}) {
    members(options, ["pointInTime", "ttlSeconds", "signal"]);
    if (options.ttlSeconds !== undefined) requireTtlSeconds(options.ttlSeconds);
    if (options.pointInTime !== undefined && (typeof options.pointInTime !== "string" ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/u.test(options.pointInTime) ||
        !Number.isFinite(Date.parse(options.pointInTime)))) throw new TypeError("pointInTime must be an RFC 3339 instant.");
    const transport = this.#projectTransport(project);
    const params = { database: requireDatabaseSelector(source) };
    const body = stringifyExactJson({ name: requireName("database", name),
      ...(options.pointInTime === undefined ? {} : { pointInTime: options.pointInTime }),
      ...(options.ttlSeconds === undefined ? {} : { ttlSeconds: options.ttlSeconds }),
    }, "database fork");
    const response = await transport.call(options.pointInTime === undefined ? "branchDatabase" : "restorePointInTime", params,
      { body, signal: options.signal });
    if (options.pointInTime === undefined) return decodeManagedDatabase(response.value);
    const done = await awaitRestoreOperation(transport, params, restoreOperation(response.value), { signal: options.signal });
    return decodeManagedDatabase((await transport.call("getDatabase", { database: requireDatabaseSelector(done.database) },
      { signal: options.signal })).value);
  }

  /** @param {string} project @param {string} database @param {number} ttlSeconds @param {V1RequestOptions} [options] @returns {Promise<import("./catalog.js").V1ManagedDatabase>} */
  async setDatabaseTtl(project, database, ttlSeconds, options = {}) {
    const response = await this.#projectTransport(project).call("setDatabaseTtl", { database: requireDatabaseSelector(database) }, {
      body: stringifyExactJson({ ttlSeconds: requireTtlSeconds(ttlSeconds) }, "database TTL"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }

  /** @param {string} project @param {string} database @param {V1RequestOptions} [options] @returns {Promise<import("./catalog.js").V1ManagedDatabase>} */
  async clearDatabaseTtl(project, database, options = {}) {
    return decodeManagedDatabase((await this.#projectTransport(project).call("clearDatabaseTtl", {
      database: requireDatabaseSelector(database),
    }, { signal: options.signal })).value);
  }

  /** @param {string} project @param {string} database @param {V1RequestOptions} [options] @returns {Promise<void>} */
  deleteDatabase(project, database, options = {}) {
    return new V1Databases(this.#projectTransport(project)).delete(database, options);
  }

  /** @param {V1ScopedKeyRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ScopedKey>} */
  async createScopedKey(request, options = {}) {
    members(request, ["project", "database", "access", "ttlSeconds"]);
    if (request.access !== "read" && request.access !== "read_write") throw new TypeError("access must be read or read_write.");
    const raw = object(await this.#transport.createScopedKey(stringifyExactJson({
      ...(request.project === undefined ? {} : { project: requireId(request.project, "Project ID") }),
      database: requireId(request.database, "Database ID"), access: request.access,
      ...(request.ttlSeconds === undefined ? {} : { ttl_seconds: requireTtlSeconds(request.ttlSeconds, 2_592_000) }),
    }, "scoped key"), options.signal), "scoped key");
    if (raw.access !== "read" && raw.access !== "read_write") throw new TypeError("Malformed scoped-key access.");
    const expiresAt = string(raw.expires_at, "scoped-key expires_at");
    if (!Number.isFinite(Date.parse(expiresAt))) throw new TypeError("Malformed scoped-key expiry.");
    return Object.freeze({ key: string(raw.key, "scoped-key key"), id: requireId(raw.id),
      projectId: requireId(raw.project_id), database: requireId(raw.database), access: raw.access, expiresAt });
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
  /** @returns {V1Regions} */
  get regions() { return this.#regions; }
}

/**
 * Creates a project-scoped TerraBase client for SQL, table shapes and catalog operations.
 * @param {TerraBaseV1ClientConfig} config
 * @returns {TerraBaseV1Client}
 */
export function createTerraBaseV1Client(config) {
  return new TerraBaseV1Client(config);
}
