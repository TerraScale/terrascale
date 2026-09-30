import { isAbsolute } from "node:path";
import { randomUUID } from "node:crypto";
import {
  createTerraBaseClient,
  type TerraBaseClient,
  type TerraBaseClientConfig,
} from "../database.js";
import type { CollectionBinding, DatabaseScope } from "../database-types.js";
import { normalizeLocalBearerToken } from "./authentication.js";
import type { LocalBetterAuthAdapterFactory } from "./better-auth.js";
import { assertLocalSqliteEnvironment } from "./environment.js";
import { TerraScaleLocalRouter } from "./router.js";
import {
  TerraScaleLocalHttpServer,
  type LocalServerAddress,
} from "./server.js";
import { TerraScaleSqliteStore } from "./store.js";
import type { LocalAuthenticationMode } from "./context.js";
export type {
  LocalAuthenticationMode,
  LocalServerAddress,
  LocalBetterAuthAdapterFactory,
};

export type TerraScaleSqliteRuntimeOptions = {
  readonly sqlite: { readonly enabled: true; readonly databasePath: string };
  readonly scope?: DatabaseScope;
  readonly authentication?: LocalAuthenticationMode;
  readonly bearerToken?: string;
  readonly allowRemote?: boolean;
};
export type TerraScaleLocalListenOptions = {
  readonly host?: string;
  readonly port?: number;
};

/** Explicit Node development backend. Never load this entrypoint in deployment or qualification. */
export class TerraScaleSqliteRuntime {
  readonly #store: TerraScaleSqliteStore;
  #router: TerraScaleLocalRouter;
  readonly #server: TerraScaleLocalHttpServer;
  readonly #token: string;
  readonly scope: DatabaseScope;
  #closed = false;
  #closePromise: Promise<void> | undefined;
  constructor(options: TerraScaleSqliteRuntimeOptions) {
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
      const stored = this.#store.getMeta<{
        tenant: string;
        database: string;
        shard: string;
      }>("local-native-scope");
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
  get databasePath(): string {
    return this.#store.databasePath;
  }
  get address(): LocalServerAddress | undefined {
    return this.#server.address;
  }
  get authority() {
    this.#assertOpen();
    return this.#router.authority;
  }
  async bindCollection(binding: CollectionBinding): Promise<void> {
    this.#assertOpen();
    await this.#store.exclusive(() => {
      this.#assertOpen();
      this.#store.transaction(() => this.#router.bindCollection(binding));
    });
  }
  createClient(
    options: Pick<TerraBaseClientConfig, "requestTimeoutMs"> = {},
  ): TerraBaseClient {
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
  async createBetterAuthAdapter(): Promise<LocalBetterAuthAdapterFactory> {
    this.#assertOpen();
    const { createLocalBetterAuthAdapter } = await import("./better-auth.js");
    this.#assertOpen();
    return createLocalBetterAuthAdapter(this.#store);
  }
  start(
    options: TerraScaleLocalListenOptions = {},
  ): Promise<LocalServerAddress> {
    this.#assertOpen();
    return this.#server.start(options);
  }
  stop(): Promise<void> {
    return this.#server.close();
  }
  async reset(): Promise<void> {
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
  close(): Promise<void> {
    if (this.#closePromise !== undefined) return this.#closePromise;
    this.#closed = true;
    this.#closePromise = (async () => {
      await this.#server.close();
      await this.#store.exclusive(() => this.#store.close());
    })();
    return this.#closePromise;
  }
  #assertOpen(): void {
    if (this.#closed) throw new Error("The SQLite runtime is closed.");
  }
}
export function createTerraScaleSqliteRuntime(
  options: TerraScaleSqliteRuntimeOptions,
): TerraScaleSqliteRuntime {
  return new TerraScaleSqliteRuntime(options);
}
