import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import { normalizeRegionCodes, type TerraScaleRegionCode } from "./regions.js";
import type * as Models from "./models.js";
import { validateSchema, type CollectionSchema } from "./schema.js";
import type * as Contracts from "./management-contracts.js";
export type * from "./management-contracts.js";
import type { TerraScaleResult } from "./result.js";

export type { TerraScaleRuntimeConfig } from "./config.js";

const apiRoot = "/api/v1";

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
  readonly created_at: Contracts.UInt64Value;
  readonly deleted: boolean;
  readonly name: string;
  readonly placement: string;
  readonly residency: string;
  readonly parent: string | null;
};
export type CreateDatabaseRequest = {
  readonly name: string;
  readonly placement: readonly TerraScaleRegionCode[];
  readonly residency?: string | null;
};
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
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(_projectId?: string, _query?: ManagementPageRequest): Promise<TerraScaleResult<DatabaseListResponse>> {
    return this.#http.request({
      method: "GET",
      path: apiRoot + "/databases",
      routeName: "databases.listDatabases",
    });
  }

  create(request: CreateDatabaseRequest, _projectId?: string): Promise<TerraScaleResult<Database>> {
    validateCreateDatabase(request);
    return this.#http.request({
      method: "POST",
      path: apiRoot + "/databases",
      routeName: "databases.createDatabase",
      body: request,
      idempotencyKey: false,
    });
  }

  get(databaseId: string, _projectId?: string): Promise<TerraScaleResult<Database>> {
    return this.#http.request({
      method: "GET",
      path: databasePath(databaseId),
      routeName: "databases.getDatabase",
    });
  }

  delete(databaseId: string, options: DeleteDatabaseOptions): Promise<TerraScaleResult<Database>> {
    if (options?.confirm !== true) throw new TypeError("Database deletion requires options.confirm to be true.");
    return this.#http.request({
      method: "DELETE",
      path: databasePath(databaseId),
      routeName: "databases.deleteDatabase",
    });
  }

  restore(databaseId: string): Promise<TerraScaleResult<Database>> {
    return this.#http.request({
      method: "POST",
      path: `${databasePath(databaseId)}/restore`,
      routeName: "databases.restoreDatabase",
      idempotencyKey: false,
    });
  }

  listDatabases(): Promise<TerraScaleResult<DatabaseListResponse>> { return this.list(); }
  createDatabase(request: CreateDatabaseRequest): Promise<TerraScaleResult<Database>> { return this.create(request); }
  getDatabase(databaseId: string): Promise<TerraScaleResult<Database>> { return this.get(databaseId); }
  deleteDatabase(databaseId: string, options: DeleteDatabaseOptions): Promise<TerraScaleResult<Database>> { return this.delete(databaseId, options); }
  restoreDatabase(databaseId: string): Promise<TerraScaleResult<Database>> { return this.restore(databaseId); }
}

export class ManagementBranchesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  getCapability(_projectId?: string): Promise<TerraScaleResult<DatabaseBranchCapabilityResponse>> {
    return contractGap("management.databaseBranches.getCapability");
  }

  requestClone(_request: CreateDatabaseBranchRequest = {}, _projectId?: string): Promise<TerraScaleResult<never>> {
    return contractGap("management.databaseBranches.requestClone");
  }

  list(databaseId: string): Promise<TerraScaleResult<BranchListResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(databaseId)}/branches`,
      routeName: "databases.listBranches",
    });
  }

  create(databaseId: string, request: RestoreRequest): Promise<TerraScaleResult<Database>> {
    validateRestoreRequest(request);
    return this.#http.request({
      method: "POST",
      path: `${databasePath(databaseId)}/branches`,
      routeName: "databases.createBranch",
      body: request,
      idempotencyKey: false,
    });
  }

  listBranches(databaseId: string): Promise<TerraScaleResult<BranchListResponse>> { return this.list(databaseId); }
  createBranch(databaseId: string, request: RestoreRequest): Promise<TerraScaleResult<Database>> { return this.create(databaseId, request); }
}

export class ManagementBackupsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  configurePolicy(_databaseId: string, _request: ConfigureBackupPolicyRequest, _projectId?: string): Promise<TerraScaleResult<BackupPolicyResponse>> {
    return contractGap("management.backups.policy.configure");
  }

  planBackup(_databaseId: string, _request: DatabaseBackupConfirmationPlanRequest, _projectId?: string): Promise<TerraScaleResult<DatabaseBackupConfirmationPlanResponse>> {
    return contractGap("management.backups.plan");
  }

  executeBackup(_databaseId: string, _request: DatabaseBackupConfirmationExecuteRequest, _idempotencyKey: string, _projectId?: string): Promise<TerraScaleResult<BackupJobResponse>> {
    return contractGap("management.backups.execute");
  }

  listBackups(databaseId: string, _query?: ManagementPageRequest, _projectId?: string): Promise<TerraScaleResult<BackupStatusResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(databaseId)}/backups`,
      routeName: "databases.listBackups",
    });
  }

  restoreBackup(databaseId: string, request: RestoreRequest): Promise<TerraScaleResult<Database>> {
    validateRestoreRequest(request);
    return this.#http.request({
      method: "POST",
      path: `${databasePath(databaseId)}/backups/restore`,
      routeName: "databases.restoreBackup",
      body: request,
      idempotencyKey: false,
    });
  }

  restore(databaseId: string, request: RestoreRequest): Promise<TerraScaleResult<Database>> {
    return this.restoreBackup(databaseId, request);
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

export type SchemaOperationOptions = {
  readonly idempotencyKey: string;
  readonly ifMatch?: string;
  readonly signal?: AbortSignal;
};

export class ManagementSchemasClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  get(databaseId: string, collection: string): Promise<TerraScaleResult<Contracts.SchemaDocument>> {
    return this.#http.request({ method: "GET", path: `${collectionPath(databaseId, collection)}/schema`, routeName: "databases.getSchema" });
  }

  put(databaseId: string, collection: string, schema: CollectionSchema, options: SchemaOperationOptions): Promise<TerraScaleResult<Contracts.SchemaDocument>> {
    validateSchema({ collections: [schema] });
    if (schema.name !== collection) throw new TypeError("schema.name must match the collection.");
    requireText(options?.idempotencyKey, "idempotencyKey", 256);
    if (options.ifMatch !== undefined && !/^"[0-9]+"$/u.test(options.ifMatch)) throw new TypeError("ifMatch must be a strong schema generation ETag.");
    return this.#http.request({
      method: "PUT", path: `${collectionPath(databaseId, collection)}/schema`, routeName: "databases.putSchema",
      body: schema, idempotencyKey: options.idempotencyKey,
      ...(options.ifMatch === undefined ? {} : { headers: { "if-match": options.ifMatch } }),
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
}

export class ManagementCollectionsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(databaseId: string): Promise<TerraScaleResult<CollectionsResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databasePath(databaseId)}/collections`,
      routeName: "databases.listCollections",
    });
  }

  listIndexes(databaseId: string, collection: string): Promise<TerraScaleResult<IndexListResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${collectionPath(databaseId, collection)}/indexes`,
      routeName: "databases.listIndexes",
    });
  }

  createIndex(databaseId: string, collection: string, request: CreateIndexRequest, _options?: ManagementOperationOptions): Promise<TerraScaleResult<IndexCreatedResponse>> {
    validateCreateIndexRequest(request);
    return this.#http.request({
      method: "POST",
      path: `${collectionPath(databaseId, collection)}/indexes`,
      routeName: "databases.createIndex",
      body: request,
      idempotencyKey: false,
    });
  }

  deleteIndex(databaseId: string, collection: string, field: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `${collectionPath(databaseId, collection)}/indexes/${idSegment(field, "field")}`,
      routeName: "databases.deleteIndex",
      idempotencyKey: false,
    });
  }

  recommendations(databaseId: string, collection: string, options: { readonly limit?: number } = {}): Promise<TerraScaleResult<Contracts.IndexRecommendations>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 64)) throw new TypeError("limit must be an integer between 1 and 64.");
    return this.#http.request({ method: "GET", path: `${collectionPath(databaseId, collection)}/indexes/recommendations`, routeName: "databases.indexRecommendations", query: options });
  }

  listCollections(databaseId: string): Promise<TerraScaleResult<CollectionsResponse>> { return this.list(databaseId); }
}

export class ManagementExportsClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  create(databaseId: string, request: Contracts.CreateExportRequest): Promise<TerraScaleResult<Contracts.ExportAccepted>> {
    requireText(request?.idempotency_key, "idempotency_key", 512);
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/exports`, routeName: "databases.createExport", body: request, idempotencyKey: false });
  }

  get(databaseId: string, jobId: string): Promise<TerraScaleResult<Contracts.ExportStatus>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}`, routeName: "databases.getExport" });
  }

  download(databaseId: string, jobId: string, member: string): Promise<TerraScaleResult<Uint8Array>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}/artifacts/${idSegment(member, "artifact member")}`, routeName: "databases.downloadExport" });
  }

  resume(databaseId: string, jobId: string, request: Contracts.ResumeExportRequest): Promise<TerraScaleResult<Contracts.ExportStatus>> {
    requireText(request?.resume_cursor, "resume_cursor");
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}/resume`, routeName: "databases.resumeExport", body: request, idempotencyKey: false });
  }
}

export class ManagementWebhooksClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(databaseId: string): Promise<TerraScaleResult<Contracts.WebhookList>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/webhooks`, routeName: "databases.listWebhooks" });
  }

  create(databaseId: string, request: Contracts.CreateWebhookRequest): Promise<TerraScaleResult<Contracts.WebhookEndpoint>> {
    const endpoint = new URL(request?.endpoint);
    if (endpoint.protocol !== "https:") throw new TypeError("webhook endpoint must use HTTPS.");
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/webhooks`, routeName: "databases.createWebhook", body: request, idempotencyKey: false });
  }

  delete(databaseId: string, webhookId: string): Promise<TerraScaleResult<Contracts.WebhookEndpoint>> {
    return this.#http.request({ method: "DELETE", path: webhookPath(databaseId, webhookId), routeName: "databases.deleteWebhook", idempotencyKey: false });
  }

  deliveries(databaseId: string, webhookId: string): Promise<TerraScaleResult<Contracts.WebhookDeliveryLog>> {
    return this.#http.request({ method: "GET", path: `${webhookPath(databaseId, webhookId)}/deliveries`, routeName: "databases.listDeliveries" });
  }

  replay(databaseId: string, webhookId: string, deliveryId: string): Promise<TerraScaleResult<Contracts.WebhookDelivery>> {
    return this.#http.request({ method: "POST", path: `${webhookPath(databaseId, webhookId)}/deliveries/${idSegment(deliveryId, "delivery id")}/replay`, routeName: "databases.replayWebhook", idempotencyKey: false });
  }
}

export class ManagementImportsClient {
  create(_databaseId: string, _request: unknown, _options?: ManagementOperationOptions): Promise<never> { return contractGap("imports.create"); }
}

export class ManagementUsageClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  getDatabase(databaseId: string): Promise<TerraScaleResult<Contracts.DatabaseUsageSummary>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/usage`, routeName: "databases.databaseUsage" });
  }

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
  readonly webhooks: ManagementWebhooksClient;
  readonly usage: ManagementUsageClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.#http = new TerraScaleHttp(config);
    this.databases = new ManagementDatabasesClient(this.#http);
    this.branches = new ManagementBranchesClient(this.#http);
    this.backups = new ManagementBackupsClient(this.#http);
    this.schemas = new ManagementSchemasClient(this.#http);
    this.collections = new ManagementCollectionsClient(this.#http);
    this.exports = new ManagementExportsClient(this.#http);
    this.imports = new ManagementImportsClient();
    this.webhooks = new ManagementWebhooksClient(this.#http);
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

function databasePath(databaseId: string): string {
  return `${apiRoot}/databases/${idSegment(databaseId, "database name")}`;
}

function collectionPath(databaseId: string, collection: string): string {
  return `${databasePath(databaseId)}/collections/${idSegment(collection, "collection")}`;
}

function idSegment(value: string, name: string): string {
  if (typeof value !== "string" || value.length === 0 || value !== value.trim() ||
    [...value].some(character => character.charCodeAt(0) < 0x20 || character.charCodeAt(0) === 0x7f) ||
    new TextEncoder().encode(value).byteLength > 255) {
    throw new TypeError(`${name} must be a nonblank printable string of at most 255 UTF-8 bytes.`);
  }
  return encodeURIComponent(value);
}

function validateCreateDatabase(request: CreateDatabaseRequest): void {
  idSegment(request?.name, "database name");
  const placement = normalizeRegionCodes(request?.placement);
  if (placement.length !== request.placement.length) {
    throw new TypeError("placement must not contain duplicate region codes.");
  }
  if (request.residency !== undefined && request.residency !== null &&
    (typeof request.residency !== "string" || request.residency.trim().length === 0)) {
    throw new TypeError("residency must be null or a nonblank string.");
  }
}

function validateRestoreRequest(request: RestoreRequest): void {
  idSegment(request?.name, "database name");
  if (request.at !== undefined && (!Number.isSafeInteger(request.at) || request.at < 0)) {
    throw new TypeError("at must be a non-negative safe integer.");
  }
}

function validateCreateIndexRequest(request: CreateIndexRequest): void {
  idSegment(request?.field, "field");
}

function webhookPath(databaseId: string, webhookId: string): string {
  return `${databasePath(databaseId)}/webhooks/${idSegment(webhookId, "webhook id")}`;
}

function requireText(value: string, name: string, maximumBytes?: number): void {
  if (typeof value !== "string" || value.trim().length === 0 || (maximumBytes !== undefined && new TextEncoder().encode(value).length > maximumBytes)) throw new TypeError(`${name} must be nonblank${maximumBytes === undefined ? "" : ` and at most ${maximumBytes} UTF-8 bytes`}.`);
}
