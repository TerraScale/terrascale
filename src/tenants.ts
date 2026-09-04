import type { TerraScaleHttp } from "./http.js";
import type { TerraScaleRegionCode } from "./regions.js";
import type { TerraScaleResult } from "./result.js";

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

/** Typed fail-closed signal until the producer publishes tenant operations. */
export class TenantContractGapError extends Error {
  readonly code = "contract_gap" as const;
  constructor(readonly operation: string) {
    super(`The published tenant contract is missing ${operation}.`);
    this.name = "TenantContractGapError";
  }
}

/**
 * Typed database-per-tenant operations from the public contract.
 *
 * This client deliberately exposes named operations only. It does not accept
 * arbitrary paths, methods, headers, or response types.
 */
export class TenantsClient {
  constructor(_http: TerraScaleHttp) {}

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
    _keyOrRequest: string | CreateTenantRequest,
    _options: TenantOperationOptions & {
      readonly template?: string | undefined;
      readonly placementLock?: TenantPlacementLock | undefined;
    } = {},
  ): Promise<TerraScaleResult<Tenant>> {
    return tenantContractGap("tenants.create");
  }

  /** Fetch one tenant database by its stable tenant key. */
  get(
    _key: string,
    _options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<Tenant>> {
    return tenantContractGap("tenants.get");
  }

  /** List tenant databases in the project. */
  async list(
    _query?: TenantListQuery,
    _options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<readonly Tenant[]>> {
    return tenantContractGap("tenants.list");
  }

  /** Soft-delete a tenant database; explicit confirmation is required. */
  delete(
    _key: string,
    _options: DeleteTenantOptions,
  ): Promise<TerraScaleResult<Tenant>> {
    return tenantContractGap("tenants.delete");
  }

  /** Mint one API key scoped to this tenant database. */
  createKey(
    _key: string,
    _request: CreateTenantKeyRequest = {},
    _options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<TenantApiKey>> {
    return tenantContractGap("tenants.createKey");
  }

  /** Create a support/debug branch while retaining the tenant's placement lock. */
  createBranch(
    _key: string,
    _request: CreateTenantBranchRequest = {},
    _options: TenantOperationOptions = {},
  ): Promise<TerraScaleResult<TenantBranch>> {
    return tenantContractGap("tenants.createBranch");
  }

}

export type TenantBranch = {
  readonly branchId: string;
  readonly tenantKey: string;
  readonly status?: string | undefined;
};

function tenantContractGap(operation: string): never {
  throw new TenantContractGapError(operation);
}
