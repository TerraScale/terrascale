import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import { normalizeRegionCodes, type TerraScaleRegionCode } from "./regions.js";
import type * as Models from "./models.js";
import type { TerraScaleResult } from "./result.js";

export type { TerraScaleRuntimeConfig } from "./config.js";

/** The bounded page shape used by still-pending management operations. */
export type ManagementPageRequest = Models.PageRequest;

export type ManagementOperationOptions = {
  /** Stable identity for a retryable mutation. */
  readonly idempotencyKey?: string | false;
  /** Project scope; the runtime project is used when omitted. */
  readonly projectId?: string;
};

/** A residency constraint. Region lists are normalized and bounded. */
export type PlacementLock =
  | "eu-only"
  | "br-only"
  | readonly TerraScaleRegionCode[];

/** A typed signal for an operation whose owning service has not published its contract. */
export class ManagementContractGapError extends Error {
  readonly code = "contract_gap" as const;
  readonly operation: string;

  constructor(operation: string) {
    super(`The published management contract is missing ${operation}.`);
    this.name = "ManagementContractGapError";
    this.operation = operation;
  }
}

export type Database = {
  readonly created_at: number;
  readonly deleted: boolean;
  readonly name: string;
  readonly placement: string;
  readonly residency: string;
  readonly parent: string | null;
};
export type CreateDatabaseRequest = { readonly name: string };
export type DatabaseListResponse = { readonly databases: readonly Database[] };
export type RestoreRequest = { readonly name: string; readonly at?: number };
export type BranchListResponse = { readonly branches: readonly Database[] };
export type BackupStatusResponse = { readonly ready: boolean; readonly streaming: boolean };
export type CollectionsResponse = { readonly collections: readonly string[] };
export type CreateIndexRequest = { readonly field: string };
export type IndexDescriptor = { readonly field: string; readonly state?: "active" | "building" };
export type IndexListResponse = { readonly indexes: readonly IndexDescriptor[] };
export type IndexCreatedResponse = { readonly field: string; readonly state: "active" | "building" };
export type DeleteDatabaseOptions = Models.DeleteProjectDatabaseOptions;
export type DatabaseBranchCapabilityResponse = Models.DatabaseBranchCapabilityResponse;
export type CreateDatabaseBranchRequest = Models.CreateDatabaseBranchRequest;
export type ConfigureBackupPolicyRequest = Models.ConfigureBackupPolicyRequest;
export type BackupPolicyResponse = Models.BackupPolicyResponse;
export type DatabaseBackupConfirmationPlanRequest = Models.DatabaseBackupConfirmationPlanRequest;
export type DatabaseBackupConfirmationPlanResponse = Models.DatabaseBackupConfirmationPlanResponse;
export type DatabaseBackupConfirmationExecuteRequest = Models.DatabaseBackupConfirmationExecuteRequest;
export type BackupJobResponse = Models.BackupJobResponse;
export type RestorePointResponse = Models.RestorePointResponse;
export type StartRestoreRequest = Models.StartRestoreRequest;
export type RestoreJobResponse = Models.RestoreJobResponse;
export type UsageSummary = Models.UsageSummary;
export type OrganizationUsage = Models.OrganizationUsage;

export class ManagementDatabasesClient {
  constructor(_http: TerraScaleHttp) {}

  list(_projectId?: string, _query?: ManagementPageRequest): Promise<TerraScaleResult<DatabaseListResponse>> {
    return contractGap("management.databases.list");
  }

  create(_request: CreateDatabaseRequest, _projectId?: string): Promise<TerraScaleResult<Database>> {
    return contractGap("management.databases.create");
  }

  get(_databaseId: string, _projectId?: string): Promise<TerraScaleResult<Database>> {
    return contractGap("management.databases.get");
  }

  delete(_databaseId: string, _options: DeleteDatabaseOptions): Promise<TerraScaleResult<Models.DeletedProjectDatabaseResponse>> {
    return contractGap("management.databases.delete");
  }
}

export class ManagementBranchesClient {
  constructor(_http: TerraScaleHttp) {}

  getCapability(_projectId?: string): Promise<TerraScaleResult<DatabaseBranchCapabilityResponse>> {
    return contractGap("management.databaseBranches.getCapability");
  }

  requestClone(_request: CreateDatabaseBranchRequest = {}, _projectId?: string): Promise<TerraScaleResult<never>> {
    return contractGap("management.databaseBranches.requestClone");
  }
}

export class ManagementBackupsClient {
  constructor(_http: TerraScaleHttp) {}

  configurePolicy(_databaseId: string, _request: ConfigureBackupPolicyRequest, _projectId?: string): Promise<TerraScaleResult<BackupPolicyResponse>> {
    return contractGap("management.backups.policy.configure");
  }

  planBackup(_databaseId: string, _request: DatabaseBackupConfirmationPlanRequest, _projectId?: string): Promise<TerraScaleResult<DatabaseBackupConfirmationPlanResponse>> {
    return contractGap("management.backups.plan");
  }

  executeBackup(_databaseId: string, _request: DatabaseBackupConfirmationExecuteRequest, _idempotencyKey: string, _projectId?: string): Promise<TerraScaleResult<BackupJobResponse>> {
    return contractGap("management.backups.execute");
  }

  listBackups(_databaseId: string, _query?: ManagementPageRequest, _projectId?: string): Promise<TerraScaleResult<readonly BackupJobResponse[]>> {
    return contractGap("management.backups.list");
  }

  getBackup(_databaseId: string, _backupId: string, _projectId?: string): Promise<TerraScaleResult<BackupJobResponse>> {
    return contractGap("management.backups.get");
  }

  listRestorePoints(_databaseId: string, _projectId?: string): Promise<TerraScaleResult<readonly RestorePointResponse[]>> {
    return contractGap("management.backups.restorePoints.list");
  }

  startRestore(_databaseId: string, _request: StartRestoreRequest, _projectId?: string): Promise<TerraScaleResult<RestoreJobResponse>> {
    return contractGap("management.backups.restores.start");
  }

  getRestore(_databaseId: string, _restoreId: string, _projectId?: string): Promise<TerraScaleResult<RestoreJobResponse>> {
    return contractGap("management.backups.restores.get");
  }

  listRestores(_databaseId: string, _query?: ManagementPageRequest, _projectId?: string): Promise<TerraScaleResult<readonly RestoreJobResponse[]>> {
    return contractGap("management.backups.restores.list");
  }
}

/** Pending domains fail before transport until their owning contract is published. */
export class ManagementSchemasClient {
  get(_databaseId: string, _collection: string): Promise<never> { return contractGap("schemas.get"); }
  put(_databaseId: string, _collection: string, _schema: unknown, _options?: ManagementOperationOptions): Promise<never> { return contractGap("schemas.put"); }
}

export class ManagementCollectionsClient {
  list(_databaseId: string): Promise<never> { return contractGap("collections.list"); }
  listIndexes(_databaseId: string, _collection: string): Promise<never> { return contractGap("collections.indexes.list"); }
  createIndex(_databaseId: string, _collection: string, _request: unknown, _options?: ManagementOperationOptions): Promise<never> { return contractGap("collections.indexes.create"); }
}

export class ManagementExportsClient {
  create(_databaseId: string, _request: unknown = {}, _options?: ManagementOperationOptions): Promise<never> { return contractGap("exports.create"); }
}

export class ManagementImportsClient {
  create(_databaseId: string, _request: unknown, _options?: ManagementOperationOptions): Promise<never> { return contractGap("imports.create"); }
}

export class ManagementUsageClient {
  constructor(_http: TerraScaleHttp) {}

  getProject(_projectId?: string): Promise<TerraScaleResult<UsageSummary>> {
    return contractGap("management.usage.project.get");
  }

  getOrganization(_organizationId: string): Promise<TerraScaleResult<OrganizationUsage>> {
    return contractGap("management.usage.organization.get");
  }
}

export class TerraScaleManagementClient {
  readonly #http: TerraScaleHttp;
  readonly databases: ManagementDatabasesClient;
  readonly branches: ManagementBranchesClient;
  readonly backups: ManagementBackupsClient;
  readonly schemas: ManagementSchemasClient;
  readonly collections: ManagementCollectionsClient;
  readonly exports: ManagementExportsClient;
  readonly imports: ManagementImportsClient;
  readonly usage: ManagementUsageClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.#http = new TerraScaleHttp(config);
    this.databases = new ManagementDatabasesClient(this.#http);
    this.branches = new ManagementBranchesClient(this.#http);
    this.backups = new ManagementBackupsClient(this.#http);
    this.schemas = new ManagementSchemasClient();
    this.collections = new ManagementCollectionsClient();
    this.exports = new ManagementExportsClient();
    this.imports = new ManagementImportsClient();
    this.usage = new ManagementUsageClient(this.#http);
  }

  get config() { return this.#http.config; }
}

export { TerraScaleManagementClient as ManagementClient };

export function createTerraScaleManagementClient(config: TerraScaleRuntimeConfig): TerraScaleManagementClient {
  return new TerraScaleManagementClient(config);
}

/** Validate and normalize an explicit residency lock without making a request. */
export function validatePlacementLock(lock: PlacementLock): PlacementLock {
  if (typeof lock === "string") {
    if (lock !== "eu-only" && lock !== "br-only") throw new TypeError("placement must be eu-only, br-only, or a region list.");
    return lock;
  }
  return normalizeRegionCodes(lock);
}

function contractGap(operation: string): never { throw new ManagementContractGapError(operation); }
