import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import { normalizeRegionCodes, type TerraScaleRegionCode } from "./regions.js";
import type * as Models from "./models.js";
import type { TerraScaleResult } from "./result.js";

export type { TerraScaleRuntimeConfig } from "./config.js";

const apiRoot = "/api/v1";

/** The bounded page shape used by management list operations. */
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

/**
 * Database creation currently publishes only `name`. The optional residency
 * fields are accepted for validation at the boundary, then fail closed until
 * the owner publishes their request shape; they are never silently dropped.
 */
export type CreateDatabaseRequest = Models.CreateDatabaseRequest & {
  readonly placement?: readonly TerraScaleRegionCode[];
  readonly residency?: Exclude<PlacementLock, readonly TerraScaleRegionCode[]>;
};
export type Database = Models.ProjectDatabase;
export type DatabaseListResponse = readonly Models.ProjectDatabase[];
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
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(projectId?: string, query?: ManagementPageRequest): Promise<TerraScaleResult<DatabaseListResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectPath(this.#http, projectId)}/databases`,
      routeName: "databases.list",
      query: pageQuery(query),
    });
  }

  create(request: CreateDatabaseRequest, projectId?: string): Promise<TerraScaleResult<Database>> {
    return this.#http.request({
      method: "POST",
      path: `${projectPath(this.#http, projectId)}/databases`,
      routeName: "databases.create",
      body: validateCreateDatabase(request),
    });
  }

  get(databaseId: string, projectId?: string): Promise<TerraScaleResult<Database>> {
    return this.#http.request({
      method: "GET",
      path: databasePath(this.#http, databaseId, projectId),
      routeName: "databases.get",
    });
  }

  delete(databaseId: string, options: DeleteDatabaseOptions): Promise<TerraScaleResult<Models.DeletedProjectDatabaseResponse>> {
    if (options?.confirm !== true) throw new TypeError("Database deletion requires options.confirm to be true.");
    return this.#http.request({
      method: "DELETE",
      path: databasePath(this.#http, databaseId, options.projectId),
      routeName: "databases.delete",
      headers: { "x-terrascale-delete-confirmation": databaseId },
    });
  }
}

export class ManagementBranchesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  getCapability(projectId?: string): Promise<TerraScaleResult<DatabaseBranchCapabilityResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectPath(this.#http, projectId)}/database-branches/capability`,
      routeName: "databaseBranches.getCapability",
    });
  }

  requestClone(request: CreateDatabaseBranchRequest = {}, projectId?: string): Promise<TerraScaleResult<never>> {
    return this.#http.request({
      method: "POST",
      path: `${projectPath(this.#http, projectId)}/database-branches`,
      routeName: "databaseBranches.requestClone",
      body: validateBranchRequest(request),
      idempotencyKey: false,
    });
  }
}

export class ManagementBackupsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  configurePolicy(databaseId: string, request: ConfigureBackupPolicyRequest, projectId?: string): Promise<TerraScaleResult<BackupPolicyResponse>> {
    return this.#http.request({
      method: "PUT",
      path: `${databasePath(this.#http, databaseId, projectId)}/backups/policy`,
      routeName: "backups.policy.configure",
      body: validateBackupPolicy(request),
    });
  }

  planBackup(databaseId: string, request: DatabaseBackupConfirmationPlanRequest, projectId?: string): Promise<TerraScaleResult<DatabaseBackupConfirmationPlanResponse>> {
    validateBoundedText(request?.operationSessionId, "operationSessionId", 128);
    return this.#http.request({
      method: "POST",
      path: `${databasePath(this.#http, databaseId, projectId)}/backups:plan`,
      routeName: "backups.plan",
      body: request,
    });
  }

  executeBackup(databaseId: string, request: DatabaseBackupConfirmationExecuteRequest, idempotencyKey: string, projectId?: string): Promise<TerraScaleResult<BackupJobResponse>> {
    validateBoundedText(request?.operationSessionId, "operationSessionId", 128);
    validateBoundedText(request?.confirmationToken, "confirmationToken", 8192);
    validateBoundedText(idempotencyKey, "idempotencyKey", 128);
    return this.#http.request({
      method: "POST",
      path: `${databasePath(this.#http, databaseId, projectId)}/backups:execute`,
      routeName: "backups.execute",
      body: request,
      idempotencyKey,
    });
  }

  listBackups(databaseId: string, query?: ManagementPageRequest, projectId?: string): Promise<TerraScaleResult<readonly BackupJobResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(this.#http, databaseId, projectId)}/backups`,
      routeName: "backups.list",
      query: pageQuery(query),
    });
  }

  getBackup(databaseId: string, backupId: string, projectId?: string): Promise<TerraScaleResult<BackupJobResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(this.#http, databaseId, projectId)}/backups/${idSegment(backupId, "backupId")}`,
      routeName: "backups.get",
    });
  }

  listRestorePoints(databaseId: string, projectId?: string): Promise<TerraScaleResult<readonly RestorePointResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(this.#http, databaseId, projectId)}/restore-points`,
      routeName: "backups.restorePoints.list",
    });
  }

  startRestore(databaseId: string, request: StartRestoreRequest, projectId?: string): Promise<TerraScaleResult<RestoreJobResponse>> {
    validateBoundedText(request?.backupId, "backupId", 128);
    if (request.idempotencyKey !== undefined && request.idempotencyKey !== null) validateBoundedText(request.idempotencyKey, "idempotencyKey", 128);
    return this.#http.request({
      method: "POST",
      path: `${databasePath(this.#http, databaseId, projectId)}/restores`,
      routeName: "backups.restores.start",
      body: request,
      idempotencyKey: request.idempotencyKey ?? undefined,
    });
  }

  getRestore(databaseId: string, restoreId: string, projectId?: string): Promise<TerraScaleResult<RestoreJobResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(this.#http, databaseId, projectId)}/restores/${idSegment(restoreId, "restoreId")}`,
      routeName: "backups.restores.get",
    });
  }

  listRestores(databaseId: string, query?: ManagementPageRequest, projectId?: string): Promise<TerraScaleResult<readonly RestoreJobResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(this.#http, databaseId, projectId)}/restores`,
      routeName: "backups.restores.list",
      query: pageQuery(query),
    });
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
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  getProject(projectId?: string): Promise<TerraScaleResult<UsageSummary>> {
    return this.#http.request({ method: "GET", path: `${projectPath(this.#http, projectId)}/usage`, routeName: "usage.project.get" });
  }

  getOrganization(organizationId: string): Promise<TerraScaleResult<OrganizationUsage>> {
    return this.#http.request({ method: "GET", path: `${apiRoot}/organizations/${idSegment(organizationId, "organizationId")}/usage`, routeName: "usage.organization.get" });
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

function projectPath(http: TerraScaleHttp, projectId: string | undefined): string {
  return `${apiRoot}/projects/${idSegment(projectId ?? http.config.projectId ?? "", "projectId")}`;
}

function databasePath(http: TerraScaleHttp, databaseId: string, projectId: string | undefined): string {
  return `${projectPath(http, projectId)}/databases/${idSegment(databaseId, "databaseId")}`;
}

function idSegment(value: string, name: string): string {
  validateBoundedText(value, name, 256);
  return encodeURIComponent(value);
}

function validateBoundedText(value: unknown, name: string, maxLength: number): asserts value is string {
  if (typeof value !== "string" || value.trim().length === 0 || value.length > maxLength || /[\u0000-\u001f\u007f]/u.test(value)) {
    throw new TypeError(`${name} must be 1-${maxLength} printable characters.`);
  }
}

function pageQuery(query: ManagementPageRequest | undefined): ManagementPageRequest | undefined {
  if (query === undefined) return undefined;
  if (query.skip !== undefined && (!Number.isSafeInteger(query.skip) || query.skip < 0)) throw new TypeError("skip must be a non-negative safe integer.");
  if (query.take !== undefined && (!Number.isSafeInteger(query.take) || query.take < 1 || query.take > 200)) throw new TypeError("take must be between 1 and 200.");
  return { ...query };
}

function validateCreateDatabase(request: CreateDatabaseRequest): CreateDatabaseRequest {
  validateBoundedText(request?.name, "name", 256);
  if (request.residency !== undefined && request.residency !== "eu-only" && request.residency !== "br-only") throw new TypeError("residency must be eu-only or br-only.");
  if (request.placement !== undefined) {
    const placement = normalizeRegionCodes(request.placement);
    if (request.residency === "eu-only" && placement.some((region) => region !== "FRA")) throw new TypeError("eu-only residency cannot be widened beyond FRA.");
    if (request.residency === "br-only" && placement.some((region) => region !== "GRU")) throw new TypeError("br-only residency cannot be widened beyond GRU.");
    throw new ManagementContractGapError("databases.create placement/residency");
  }
  if (request.residency !== undefined) throw new ManagementContractGapError("databases.create placement/residency");
  return { name: request.name };
}

function validateBranchRequest(request: CreateDatabaseBranchRequest): CreateDatabaseBranchRequest {
  if (request?.sourceDatabaseId !== undefined) validateBoundedText(request.sourceDatabaseId, "sourceDatabaseId", 256);
  return request;
}

function validateBackupPolicy(request: ConfigureBackupPolicyRequest): ConfigureBackupPolicyRequest {
  validateBoundedText(request?.fullBackupCron, "fullBackupCron", 128);
  if (request.incrementalBackupCron !== undefined && request.incrementalBackupCron !== null) validateBoundedText(request.incrementalBackupCron, "incrementalBackupCron", 128);
  if (request.retentionDays !== undefined && (!Number.isSafeInteger(request.retentionDays) || request.retentionDays < 1 || request.retentionDays > 365)) throw new TypeError("retentionDays must be between 1 and 365.");
  return request;
}
