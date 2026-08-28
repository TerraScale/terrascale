import { createTerraScaleAdminClient, type AdminClient } from "../admin.js";
import type {
  FetchLike,
  TerraScaleRuntimeConfig,
  TerraScaleSqliteRuntimeConfig,
} from "../config.js";
import { createTerraScaleClient, type TerraScaleClient } from "../factory.js";
import type {
  DatabaseId,
  DocumentId,
  OrganizationId,
  ProjectId,
  QueueId,
  RepositoryId,
} from "../ids.js";
import {
  TerraScaleSyncSession,
  type TerraScaleSyncSessionOptions,
} from "../sync/client.js";
import {
  TerraScaleSyncV2Session,
  type TerraScaleSyncV2SessionOptions,
} from "../sync/v2/session.js";
import { TerraScaleLocalRouter, type LocalAuthenticationMode } from "./router.js";
import { clearLocalAdminState } from "./handlers/admin.js";
import { clearLocalPlatformState } from "./handlers/platform.js";
import { clearLocalSyncV2State } from "./handlers/sync-v2.js";
import {
  TerraScaleLocalHttpServer,
  type LocalServerAddress,
} from "./server.js";
import { TerraScaleSqliteStore } from "./store.js";
import {
  clearLocalSyncV1State,
  createLocalSyncWebSocketFactory,
} from "./sync-v1.js";

const inProcessOrigin = "http://terrascale.local";

export type TerraScaleSqliteRuntimeOptions = {
  readonly sqlite: TerraScaleSqliteRuntimeConfig;
  readonly allowRemote?: boolean;
  readonly authentication?: LocalAuthenticationMode;
};

export type TerraScaleLocalListenOptions = {
  readonly host?: string;
  readonly port?: number;
};

export type TerraScaleLocalClientOptions = Omit<
  TerraScaleRuntimeConfig,
  "apiBaseUrl" | "authBaseUrl" | "fetch" | "sqlite"
>;

export type TerraScaleLocalSeed = {
  readonly databaseId: DatabaseId;
  readonly documentId: DocumentId;
  readonly organizationId: OrganizationId;
  readonly projectId: ProjectId;
  readonly queueId: QueueId;
  readonly repositoryId: RepositoryId;
};

export type TerraScaleLocalResetOptions = {
  readonly seed?: boolean;
};

export type TerraScaleLocalSyncV1Options = Omit<
  TerraScaleSyncSessionOptions,
  "Url" | "WebSocketFactory"
> & {
  readonly Url?: string;
};

export type TerraScaleLocalSyncV2Options = Omit<
  TerraScaleSyncV2SessionOptions,
  "Fetch" | "StreamFactory" | "Url"
> & {
  readonly Url?: string;
};

export { type LocalAuthenticationMode, type LocalServerAddress };

/**
 * Opt-in Node development emulator for the public TerraScale SDK contracts.
 *
 * The normal package entrypoints never import this module. Consumers must
 * explicitly import `@terrascale/sdk/local`.
 */
export class TerraScaleSqliteRuntime {
  readonly #router: TerraScaleLocalRouter;
  readonly #server: TerraScaleLocalHttpServer;
  readonly #sqlite: TerraScaleSqliteRuntimeConfig;
  readonly #store: TerraScaleSqliteStore;
  readonly #syncV1Sessions = new Set<TerraScaleSyncSession>();
  readonly #syncV2Sessions = new Set<TerraScaleSyncV2Session>();
  #closed = false;
  constructor(options: TerraScaleSqliteRuntimeOptions) {
    if (options === null || typeof options !== "object") throw new TypeError("SQLite runtime options are required.");
    if (options.sqlite === undefined || options.sqlite.enabled !== true) throw new TypeError("SQLite runtime requires sqlite: { enabled: true }.");
    this.#sqlite = options.sqlite;
    this.#store = new TerraScaleSqliteStore(options.sqlite.databasePath ?? ":memory:");
    this.#router = new TerraScaleLocalRouter(this.#store, {
      ...(options.authentication === undefined ? {} : { authentication: options.authentication }),
    });
    this.#server = new TerraScaleLocalHttpServer(
      (request) => this.#router.handle(request),
      options.allowRemote === undefined ? {} : { allowRemote: options.allowRemote },
    );
  }

  get databasePath(): string {
    return this.#store.databasePath;
  }

  get address(): LocalServerAddress | undefined {
    return this.#server.address;
  }

  readonly #fetch: FetchLike = async (input, init) => {
    this.#assertOpen();
    return this.#router.fetch(input, init);
  };

  createClient(options: TerraScaleLocalClientOptions = {}): TerraScaleClient {
    this.#assertOpen();
    return createTerraScaleClient({
      ...options,
      apiBaseUrl: inProcessOrigin,
      authBaseUrl: inProcessOrigin,
      sqlite: this.#sqlite,
      fetch: this.#fetch,
    });
  }

  createAdminClient(options: TerraScaleLocalClientOptions = {}): AdminClient {
    this.#assertOpen();
    return createTerraScaleAdminClient({
      ...options,
      apiBaseUrl: inProcessOrigin,
      authBaseUrl: inProcessOrigin,
      sqlite: this.#sqlite,
      fetch: this.#fetch,
    });
  }

  createSyncV1Session(options: TerraScaleLocalSyncV1Options): TerraScaleSyncSession {
    this.#assertOpen();
    const session = new TerraScaleSyncSession({
      ...options,
      Url: options.Url ?? "ws://terrascale.local/api/v1/sync",
      WebSocketFactory: createLocalSyncWebSocketFactory(this.#store),
    });
    this.#syncV1Sessions.add(session);
    return session;
  }

  createSyncV2Session(options: TerraScaleLocalSyncV2Options): TerraScaleSyncV2Session {
    this.#assertOpen();
    const session = new TerraScaleSyncV2Session({
      ...options,
      Url: options.Url ?? inProcessOrigin,
      Fetch: (input, init) => this.#fetch(input, {
        ...(init?.method === undefined ? {} : { method: init.method }),
        ...(init?.headers === undefined ? {} : { headers: init.headers }),
        ...(init?.body === undefined ? {} : { body: init.body }),
        ...(init?.signal === undefined ? {} : { signal: init.signal }),
      }),
    });
    this.#syncV2Sessions.add(session);
    return session;
  }

  start(options: TerraScaleLocalListenOptions = {}): Promise<LocalServerAddress> {
    this.#assertOpen();
    return this.#server.start(options);
  }

  stop(): Promise<void> {
    return this.#server.close();
  }

  async reset(options: TerraScaleLocalResetOptions = {}): Promise<TerraScaleLocalSeed | undefined> {
    this.#assertOpen();
    await this.#disconnectSyncSessions();
    this.#clearTransientState();
    this.#store.reset();
    return options.seed === true ? this.seed() : undefined;
  }

  async seed(): Promise<TerraScaleLocalSeed> {
    this.#assertOpen();
    const client = this.createClient({
      idempotencyKeyFactory: () => `local-seed-${this.#store.nextUlid()}`,
    });
    const organization = requireValue(
      await client.organizations.create({ name: "Local development", plan: "developer" }),
    );
    const project = requireValue(
      await client.organizations.createProject(organization.organizationId, {
        name: "Local project",
        regions: ["local"],
        backupRegion: "local",
      }),
    );
    const scoped = this.createClient({
      projectId: project.projectId,
      idempotencyKeyFactory: () => `local-seed-${this.#store.nextUlid()}`,
    });
    const database = requireValue(await scoped.databases.create({ name: "Local database" }));
    const repository = requireValue(
      await scoped.repositories.create({
        name: "Local documents",
        databaseId: database.databaseId,
        collection: "documents",
      }),
    );
    const queue = requireValue(
      await scoped.queues.create({ name: "local-events", kind: "queue" }),
    );
    const document = requireValue(
      await scoped.documents.create(
        { repositoryId: repository.repositoryId },
        { title: "SQLite development runtime", status: "ready" },
      ),
    );
    return {
      organizationId: organization.organizationId,
      projectId: project.projectId,
      databaseId: database.databaseId,
      repositoryId: repository.repositoryId,
      queueId: queue.queueId,
      documentId: document.id,
    };
  }

  async close(): Promise<void> {
    if (this.#closed) {
      return;
    }
    await this.#disconnectSyncSessions();
    await this.#server.close();
    this.#clearTransientState();
    this.#store.close();
    this.#syncV1Sessions.clear();
    this.#syncV2Sessions.clear();
    this.#closed = true;
  }

  #assertOpen(): void {
    if (this.#closed) {
      throw new Error("The TerraScale SQLite runtime is closed.");
    }
  }

  #clearTransientState(): void {
    clearLocalAdminState(this.#store);
    clearLocalPlatformState(this.#store);
    clearLocalSyncV1State(this.#store);
    clearLocalSyncV2State(this.#store);
  }

  async #disconnectSyncSessions(): Promise<void> {
    for (const session of this.#syncV1Sessions) {
      session.disconnect();
    }
    await Promise.allSettled(
      [...this.#syncV2Sessions].map((session) => session.disconnect()),
    );
  }
}

export function createTerraScaleSqliteRuntime(
  options: TerraScaleSqliteRuntimeOptions,
): TerraScaleSqliteRuntime {
  return new TerraScaleSqliteRuntime(options);
}

function requireValue<T>(result: {
  readonly ok: boolean;
  readonly value?: T;
  readonly error?: { readonly message: string };
}): T {
  if (result.ok && result.value !== undefined) {
    return result.value;
  }
  throw new Error(result.error?.message ?? "The local seed request failed.");
}
