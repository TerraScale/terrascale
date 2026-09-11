import { isAbsolute } from "node:path";

import { createTerraScaleAdminClient, type AdminClient } from "../admin.js";
import type {
  FetchLike,
  TerraScaleRuntimeConfig,
  TerraScaleSqliteRuntimeConfig,
} from "../config.js";
import { createTerraScaleClient, type TerraScaleClient } from "../factory.js";
import { registerLocalRuntimeFetch } from "../local-runtime-capability.js";
import type {
  DatabaseId,
  OrganizationId,
  ProjectId,
} from "../ids.js";
import type { OrganizationPlan } from "../models.js";
import { normalizeLocalBearerToken } from "./authentication.js";
import { assertLocalSqliteEnvironment } from "./environment.js";
import { parseOrganizationPlan } from "./plans.js";
import { clearLocalAdminState } from "./handlers/admin.js";
import { clearLocalPlatformState } from "./handlers/platform.js";
import { TerraScaleLocalRouter, type LocalAuthenticationMode } from "./router.js";
import {
  TerraScaleLocalHttpServer,
  type LocalServerAddress,
} from "./server.js";
import { TerraScaleSqliteStore } from "./store.js";

const inProcessOrigin = "http://127.0.0.1";

export type TerraScaleSqliteRuntimeOptions = {
  readonly sqlite: TerraScaleSqliteRuntimeConfig;
  readonly allowRemote?: boolean;
  readonly authentication?: LocalAuthenticationMode;
  readonly bearerToken?: string;
};

export type TerraScaleLocalListenOptions = {
  readonly host?: string;
  readonly port?: number;
};

export type TerraScaleLocalClientOptions = Omit<
  TerraScaleRuntimeConfig,
  "apiBaseUrl" | "fetch" | "sqlite"
>;

export type TerraScaleLocalPlan = OrganizationPlan;

export type TerraScaleLocalSeed = {
  readonly databaseId: DatabaseId;
  readonly organizationId: OrganizationId;
  readonly projectId: ProjectId;
  readonly plan: TerraScaleLocalPlan;
};

export type TerraScaleLocalResetOptions = {
  readonly seed?: boolean;
  readonly plan?: TerraScaleLocalPlan;
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
  readonly #bearerToken: string | undefined;
  #closed = false;
  constructor(options: TerraScaleSqliteRuntimeOptions) {
    if (options === null || typeof options !== "object") throw new TypeError("SQLite runtime options are required.");
    if (options.sqlite === undefined || options.sqlite.enabled !== true) throw new TypeError("SQLite runtime requires sqlite: { enabled: true }.");
    assertLocalSqliteEnvironment();
    if (typeof options.sqlite.databasePath !== "string" || !isAbsolute(options.sqlite.databasePath)) {
      throw new TypeError("SQLite runtime databasePath must be an absolute path.");
    }
    const authentication = options.authentication ?? "permissive";
    if (authentication !== "permissive" && authentication !== "required") {
      throw new TypeError('authentication must be either "permissive" or "required".');
    }
    const bearerToken = authentication === "required"
      ? normalizeLocalBearerToken(options.bearerToken)
      : undefined;
    if (authentication === "permissive" && options.bearerToken !== undefined) {
      throw new TypeError('bearerToken can only be set when authentication is "required".');
    }
    const allowRemote = options.allowRemote ?? false;
    if (typeof allowRemote !== "boolean") {
      throw new TypeError("allowRemote must be a boolean.");
    }
    if (allowRemote && authentication !== "required") {
      throw new TypeError('allowRemote requires authentication: "required" and a strong bearerToken.');
    }
    this.#sqlite = options.sqlite;
    this.#bearerToken = bearerToken;
    this.#store = new TerraScaleSqliteStore(options.sqlite.databasePath);
    this.#router = new TerraScaleLocalRouter(this.#store, {
      authentication,
      ...(bearerToken === undefined ? {} : { bearerToken }),
    });
    this.#server = new TerraScaleLocalHttpServer(
      (request) => this.#router.handle(request),
      { allowRemote },
    );
    registerLocalRuntimeFetch(this.#fetch);
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
      ...(this.#bearerToken === undefined || options.accessToken !== undefined
        ? {}
        : { accessToken: this.#bearerToken }),
      apiBaseUrl: inProcessOrigin,
      sqlite: this.#sqlite,
      fetch: this.#fetch,
    });
  }

  createAdminClient(options: TerraScaleLocalClientOptions = {}): AdminClient {
    this.#assertOpen();
    return createTerraScaleAdminClient({
      ...options,
      ...(this.#bearerToken === undefined || options.accessToken !== undefined
        ? {}
        : { accessToken: this.#bearerToken }),
      apiBaseUrl: inProcessOrigin,
      sqlite: this.#sqlite,
      fetch: this.#fetch,
    });
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
    this.#clearTransientState();
    this.#store.reset();
    return options.seed === true ? this.seed(options.plan ?? "free") : undefined;
  }

  async seed(plan: TerraScaleLocalPlan = "free"): Promise<TerraScaleLocalSeed> {
    this.#assertOpen();
    const selectedPlan = assertLocalPlan(plan);
    const client = this.createClient({
      idempotencyKeyFactory: () => `local-seed-${this.#store.nextUlid()}`,
    });
    const organization = requireValue(
      await client.organizations.create({
        name: `Local development (${selectedPlan})`,
        plan: selectedPlan,
      }),
    );
    const project = requireValue(
      await client.organizations.createProject(organization.organizationId, {
        name: `Local project (${selectedPlan})`,
        regions: ["GRU"],
        backupRegion: "GRU",
      }),
    );
    const scoped = this.createClient({
      projectId: project.projectId,
      idempotencyKeyFactory: () => `local-seed-${this.#store.nextUlid()}`,
    });
    const database = requireValue(
      await scoped.databases.create({ name: `Local database (${selectedPlan})` }),
    );
    return {
      organizationId: organization.organizationId,
      projectId: project.projectId,
      databaseId: database.databaseId,
      plan: selectedPlan,
    };
  }

  seedStarter(): Promise<TerraScaleLocalSeed> {
    return this.seed("starter");
  }

  seedPro(): Promise<TerraScaleLocalSeed> {
    return this.seed("pro");
  }

  seedBusiness(): Promise<TerraScaleLocalSeed> {
    return this.seed("business");
  }

  async close(): Promise<void> {
    if (this.#closed) {
      return;
    }
    await this.#server.close();
    this.#clearTransientState();
    this.#store.close();
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

function assertLocalPlan(value: unknown): TerraScaleLocalPlan {
  const plan = parseOrganizationPlan(value);
  if (plan !== undefined) {
    return plan;
  }
  throw new TypeError("plan must be one of: free, starter, pro, scale, business, enterprise.");
}
