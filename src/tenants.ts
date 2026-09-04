import { TerraScaleHttp, type TerraScaleRouteName } from "./http.js";
import { normalizeRegionCodes, type TerraScaleRegionCode } from "./regions.js";
import {
  err,
  type TerraScaleFailure,
  type TerraScaleResult,
} from "./result.js";

const apiRoot = "/api/v1/tenants";

/** A residency constraint inherited by a tenant database. */
export type TenantPlacementLock =
  | "eu-only"
  | "br-only"
  | readonly TerraScaleRegionCode[];

/** The template from which tenant placement and key policy are inherited. */
export type TenantTemplate = {
  readonly name: string;
  readonly placementLock: TenantPlacementLock;
  readonly keyTemplate?: string | null;
};

/** Request to provision one tenant database. */
export type CreateTenantRequest = {
  readonly key: string;
  readonly template?: string | undefined;
  readonly placementLock?: TenantPlacementLock | undefined;
};

/** A tenant is a database; no second storage identity is introduced. */
export type Tenant = {
  readonly tenantId?: string | undefined;
  readonly key: string;
  readonly databaseId: string;
  readonly template?: string | null | undefined;
  readonly placementLock?: TenantPlacementLock | null | undefined;
  readonly regions?: readonly TerraScaleRegionCode[] | undefined;
  readonly createdAt?: string | undefined;
};

/** A tenant-scoped key minted according to the inherited key template. */
export type TenantApiKey = {
  readonly keyId: string;
  readonly token?: string | undefined;
  readonly tenantKey: string;
};

/** Optional key metadata accepted by the tenant-key operation. */
export type CreateTenantKeyRequest = {
  readonly name?: string | undefined;
  readonly scopes?: readonly string[] | undefined;
};

/** Optional support-branch metadata. Placement is inherited from the tenant. */
export type CreateTenantBranchRequest = {
  readonly name?: string | undefined;
  readonly reason?: string | undefined;
};

export type TenantListQuery = {
  readonly cursor?: string | undefined;
  readonly limit?: number | undefined;
};

export type TenantOperationOptions = {
  /** Stable key used to replay a mutation safely. */
  readonly idempotencyKey?: string | undefined;
  /** Abort before or while waiting for the operation. */
  readonly signal?: AbortSignal | undefined;
};

export type DeleteTenantOptions = TenantOperationOptions & {
  readonly confirm: true;
};

/**
 * Typed database-per-tenant operations from the public contract.
 *
 * This client deliberately exposes named operations only. It does not accept
 * arbitrary paths, methods, headers, or response types.
 */
export class TenantsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  /** Provision a database from the project's tenant template. */
  create(
    key: string,
    options?: TenantOperationOptions & {
      readonly template?: string | undefined;
      readonly placementLock?: TenantPlacementLock | undefined;
    },
  ): Promise<TerraScaleResult<Tenant>>;
  create(
    request: CreateTenantRequest,
    options?: TenantOperationOptions,
  ): Promise<TerraScaleResult<Tenant>>;
  create(
    keyOrRequest: string | CreateTenantRequest,
    options: TenantOperationOptions & {
      readonly template?: string | undefined;
      readonly placementLock?: TenantPlacementLock | undefined;
    } = {},
  ): Promise<TerraScaleResult<Tenant>> {
    const request: CreateTenantRequest = typeof keyOrRequest === "string"
      ? {
          key: keyOrRequest,
          ...(options.template === undefined ? {} : { template: options.template }),
          ...(options.placementLock === undefined
            ? {}
            : { placementLock: normalizePlacementLock(options.placementLock) }),
        }
      : {
          ...keyOrRequest,
          ...(keyOrRequest.placementLock === undefined
            ? {}
            : { placementLock: normalizePlacementLock(keyOrRequest.placementLock) }),
        };

    requireTenantKey(request.key);
    if (request.template !== undefined) requireNonBlank(request.template, "template");
    return this.#request("databases.create", {
      method: "POST",
      path: apiRoot,
      body: request,
      idempotencyKey: options.idempotencyKey,
      signal: options.signal,
    });
  }

  /** Fetch one tenant database by its stable tenant key. */
  get(
    key: string,
    options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<Tenant>> {
    requireTenantKey(key);
    return this.#request("databases.get", {
      method: "GET",
      path: tenantPath(key),
      signal: options.signal,
    });
  }

  /** List tenant databases in the project. */
  async list(
    query?: TenantListQuery,
    options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<readonly Tenant[]>> {
    if (query?.limit !== undefined && (!Number.isInteger(query.limit) || query.limit < 1)) {
      throw new TypeError("limit must be a positive integer.");
    }
    const result = await this.#request<readonly Tenant[] | { readonly tenants: readonly Tenant[] }>(
      "databases.list",
      {
        method: "GET",
        path: apiRoot,
        query,
        signal: options.signal,
      },
    );
    if (!result.ok || isTenantArray(result.value)) {
      return result as TerraScaleResult<readonly Tenant[]>;
    }
    return { ...result, value: result.value.tenants };
  }

  /** Soft-delete a tenant database; explicit confirmation is required. */
  delete(
    key: string,
    options: DeleteTenantOptions,
  ): Promise<TerraScaleResult<Tenant>> {
    requireTenantKey(key);
    if (options?.confirm !== true) {
      throw new TypeError("Tenant deletion requires options.confirm to be true.");
    }
    return this.#request("databases.delete", {
      method: "DELETE",
      path: tenantPath(key),
      headers: { "x-terrascale-delete-confirmation": key },
      idempotencyKey: options.idempotencyKey,
      signal: options.signal,
    });
  }

  /** Mint one API key scoped to this tenant database. */
  createKey(
    key: string,
    request: CreateTenantKeyRequest = {},
    options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<TenantApiKey>> {
    requireTenantKey(key);
    return this.#request("databases.create", {
      method: "POST",
      path: `${tenantPath(key)}/keys`,
      body: request,
      idempotencyKey: options.idempotencyKey,
      signal: options.signal,
    });
  }

  /** Create a support/debug branch while retaining the tenant's placement lock. */
  createBranch(
    key: string,
    request: CreateTenantBranchRequest = {},
    options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<TenantBranch>> {
    requireTenantKey(key);
    return this.#request("databaseBranches.requestClone", {
      method: "POST",
      path: `${tenantPath(key)}/branches`,
      body: request,
      idempotencyKey: options.idempotencyKey,
      signal: options.signal,
    });
  }

  async #request<TResponse>(
    routeName: TerraScaleRouteName,
    options: {
      readonly method: "DELETE" | "GET" | "POST";
      readonly path: string;
      readonly query?: TenantListQuery | undefined;
      readonly body?: unknown;
      readonly headers?: HeadersInit;
      readonly idempotencyKey?: string | undefined;
      readonly signal?: AbortSignal | undefined;
    },
  ): Promise<TerraScaleResult<TResponse>> {
    if (options.signal?.aborted === true) return cancelled(routeName);

    // `signal` is part of the shared HTTP request options in the published
    // transport. Keep this boundary typed locally while older generated
    // declarations are refreshed; dropping it here would leave cancellation
    // as a response race and would not stop the underlying fetch.
    const request = {
      method: options.method,
      path: options.path,
      routeName,
      query: options.query,
      body: options.body,
      headers: options.headers,
      idempotencyKey: options.idempotencyKey,
      signal: options.signal,
    } as Parameters<TerraScaleHttp["request"]>[0] & { readonly signal?: AbortSignal };
    const pending = this.#http.request<TResponse>(request);
    const signal = options.signal;
    if (signal === undefined) return pending;

    return new Promise<TerraScaleResult<TResponse>>((resolve, reject) => {
      const onAbort = (): void => {
        cleanup();
        resolve(cancelled(routeName));
      };
      const cleanup = (): void => signal.removeEventListener("abort", onAbort);
      signal.addEventListener("abort", onAbort, { once: true });
      // The signal may have been aborted between the initial check and listener
      // registration (for example, while the transport built its headers).
      if (signal.aborted) onAbort();
      pending.then(
        (result) => {
          cleanup();
          resolve(isAbortedResult(result) && signal.aborted ? cancelled(routeName) : result);
        },
        (error: unknown) => {
          cleanup();
          if (signal.aborted && isAbortError(error)) {
            resolve(cancelled(routeName));
            return;
          }
          // TerraScaleHttp normally converts transport failures to a typed
          // result. If an injected/future transport rejects for another
          // reason, preserve that failure instead of calling it cancellation.
          reject(error);
        },
      );
    });
  }
}

export type TenantBranch = {
  readonly branchId: string;
  readonly tenantKey: string;
  readonly status?: string | undefined;
};

function tenantPath(key: string): string {
  return `${apiRoot}/${encodeURIComponent(key)}`;
}

function requireTenantKey(key: string): void {
  requireNonBlank(key, "tenant key");
}

function requireNonBlank(value: string, name: string): void {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new TypeError(`${name} cannot be blank.`);
  }
}

function normalizePlacementLock(lock: TenantPlacementLock): TenantPlacementLock {
  if (lock === "eu-only" || lock === "br-only") return lock;
  return normalizeRegionCodes(lock);
}

function isTenantArray(value: readonly Tenant[] | { readonly tenants: readonly Tenant[] }): value is readonly Tenant[] {
  return Array.isArray(value);
}

function cancelled(routeName: string): TerraScaleFailure {
  return err(routeName, {
    kind: "transport",
    code: "request_cancelled",
    message: "The TerraScale request was cancelled.",
  });
}

function isAbortedResult<T>(result: TerraScaleResult<T>): boolean {
  return !result.ok && (
    result.error.code === "request_cancelled" ||
    result.error.code === "aborted" ||
    isAbortError(result.error.details)
  );
}

function isAbortError(value: unknown): boolean {
  return typeof value === "object" && value !== null && "name" in value && value.name === "AbortError";
}
