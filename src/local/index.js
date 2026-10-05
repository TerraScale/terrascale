import { isAbsolute } from "node:path";
import { randomUUID } from "node:crypto";
import { createTerraBaseClient } from "./legacy-client.js";
import { normalizeLocalBearerToken } from "./authentication.js";
import { assertLocalSqliteEnvironment } from "./environment.js";
import { TerraScaleLocalRouter } from "./router.js";
import { TerraScaleLocalHttpServer } from "./server.js";
import { TerraScaleSqliteStore } from "./store.js";
/** @import { TerraBaseClient, TerraBaseClientConfig } from "./legacy-client.js" */
/** @import { CollectionBinding, DatabaseScope } from "../database-types.js" */

/** @typedef {import("./context.js").LocalAuthenticationMode} LocalAuthenticationMode */
/** @typedef {import("./server.js").LocalServerAddress} LocalServerAddress */
/** @typedef {import("./better-auth.js").LocalBetterAuthAdapterFactory} LocalBetterAuthAdapterFactory */

/**
 * @typedef {{
 *   readonly sqlite: { readonly enabled: true; readonly databasePath: string };
 *   readonly scope?: DatabaseScope;
 *   readonly authentication?: LocalAuthenticationMode;
 *   readonly bearerToken?: string;
 *   readonly allowRemote?: boolean;
 * }} TerraScaleSqliteRuntimeOptions
 */
/**
 * @typedef {{
 *   readonly host?: string;
 *   readonly port?: number;
 * }} TerraScaleLocalListenOptions
 */

/** @deprecated Retired Rust SQLite emulator; use a local TerraBase release binary.
 * Explicit Node development backend. Never load this entrypoint in deployment or qualification. */
export class TerraScaleSqliteRuntime {
  /** @type {TerraScaleSqliteStore} */
  #store;
  /** @type {TerraScaleLocalRouter} */
  #router;
  /** @type {TerraScaleLocalHttpServer} */
  #server;
  /** @type {string} */
  #token;
  /**
   * @readonly
   * @type {DatabaseScope}
   */
  scope;
  #closed = false;
  /** @type {Promise<void> | undefined} */
  #closePromise;
  /**
   * @param {TerraScaleSqliteRuntimeOptions} options
   */
  constructor(options) {
    assertLocalSqliteEnvironment();
    if (options?.sqlite?.enabled !== true)
      throw new TypeError("SQLite runtime requires sqlite: { enabled: true }.");
    if (!isAbsolute(options.sqlite.databasePath))
      throw new TypeError(
        "SQLite runtime databasePath must be an absolute path.",
      );
    const authentication = options.authentication ?? "permissive";
    if (authentication !== "permissive" && authentication !== "required")
      throw new TypeError("Invalid local authentication mode.");
    if (
      options.allowRemote !== undefined &&
      typeof options.allowRemote !== "boolean"
    )
      throw new TypeError("allowRemote must be boolean.");
    if (options.allowRemote === true && authentication !== "required")
      throw new TypeError(
        "Remote local listeners require explicit authentication.",
      );
    if (authentication === "permissive" && options.bearerToken !== undefined)
      throw new TypeError("bearerToken requires authentication: required.");
    this.#token =
      authentication === "required"
        ? normalizeLocalBearerToken(options.bearerToken)
        : "local-development-only";
    this.#store = new TerraScaleSqliteStore(options.sqlite.databasePath);
    try {
      const stored =
        /** @type {{ tenant: string; database: string; shard: string } | undefined} */ (
          this.#store.getMeta("local-native-scope")
        );
      const scope =
        options.scope ??
        (stored === undefined
          ? { tenant: randomUUID(), database: randomUUID(), shard: 0n }
          : { ...stored, shard: BigInt(stored.shard) });
      this.#router = new TerraScaleLocalRouter(this.#store, scope, {
        authentication,
        ...(authentication === "required" ? { bearerToken: this.#token } : {}),
      });
      this.scope = Object.freeze({ ...scope });
      this.#store.setMeta("local-native-scope", {
        ...scope,
        shard: scope.shard.toString(),
      });
      this.#server = new TerraScaleLocalHttpServer(
        (request) => this.#router.handle(request),
        { allowRemote: options.allowRemote ?? false },
      );
    } catch (error) {
      this.#store.close();
      throw error;
    }
  }
  /** @returns {string} */
  get databasePath() {
    return this.#store.databasePath;
  }
  /** @returns {LocalServerAddress | undefined} */
  get address() {
    return this.#server.address;
  }
  get authority() {
    this.#assertOpen();
    return this.#router.authority;
  }
  /**
   * @param {CollectionBinding} binding
   * @returns {Promise<void>}
   */
  async bindCollection(binding) {
    this.#assertOpen();
    await this.#store.exclusive(() => {
      this.#assertOpen();
      this.#store.transaction(() => this.#router.bindCollection(binding));
    });
  }
  /**
   * @param {Pick<TerraBaseClientConfig, "requestTimeoutMs">} [options]
   * @returns {TerraBaseClient}
   */
  createClient(options = {}) {
    this.#assertOpen();
    return createTerraBaseClient({
      ...this.scope,
      ...options,
      endpoint: "http://127.0.0.1",
      apiKey: this.#token,
      fetch: async (input, init) => {
        this.#assertOpen();
        return this.#router.fetch(input, init);
      },
    });
  }
  /** @returns {Promise<LocalBetterAuthAdapterFactory>} */
  async createBetterAuthAdapter() {
    this.#assertOpen();
    const { createLocalBetterAuthAdapter } = await import("./better-auth.js");
    this.#assertOpen();
    return createLocalBetterAuthAdapter(this.#store);
  }
  /**
   * @param {TerraScaleLocalListenOptions} [options]
   * @returns {Promise<LocalServerAddress>}
   */
  start(options = {}) {
    this.#assertOpen();
    return this.#server.start(options);
  }
  /** @returns {Promise<void>} */
  stop() {
    return this.#server.close();
  }
  /** @returns {Promise<void>} */
  async reset() {
    this.#assertOpen();
    await this.#store.exclusive(() => {
      this.#assertOpen();
      this.#store.reset();
      this.#router = new TerraScaleLocalRouter(this.#store, this.scope, {
        authentication: "required",
        bearerToken: this.#token,
      });
      this.#store.setMeta("local-native-scope", {
        ...this.scope,
        shard: this.scope.shard.toString(),
      });
    });
  }
  /** @returns {Promise<void>} */
  close() {
    if (this.#closePromise !== undefined) return this.#closePromise;
    this.#closed = true;
    this.#closePromise = (async () => {
      await this.#server.close();
      await this.#store.exclusive(() => this.#store.close());
    })();
    return this.#closePromise;
  }
  /** @returns {void} */
  #assertOpen() {
    if (this.#closed) throw new Error("The SQLite runtime is closed.");
  }
}
/**
 * @deprecated Retired Rust emulator; use a local TerraBase release.
 * @param {TerraScaleSqliteRuntimeOptions} options
 * @returns {TerraScaleSqliteRuntime}
 */
export function createTerraScaleSqliteRuntime(options) {
  return new TerraScaleSqliteRuntime(options);
}
