import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import { normalizeRegionCodes, type TerraScaleRegionCode } from "./regions.js";
import type * as Models from "./models.js";
import { validateSchema, type CollectionSchema } from "./schema.js";
import type * as Contracts from "./management-contracts.js";
export type * from "./management-contracts.js";
import { err, type TerraScaleResult, type TerraScaleSuccess, type TerraScaleFailure } from "./result.js";

export type { TerraScaleRuntimeConfig } from "./config.js";

const apiRoot = "/api/v1";

/** The bounded page shape used by still-pending management operations. */
export type ManagementPageRequest = Models.PageRequest;

export type ManagementOperationOptions = {
  /** Stable identity for a retryable mutation. */
  readonly idempotencyKey?: string | false;
  readonly signal?: AbortSignal;
  /** Project scope; the runtime project is used when omitted. */
  readonly projectId?: string;
};

export type ManagementRequestOptions = Pick<ManagementOperationOptions, "signal">;

export type OperationSummary = {
  readonly operation_id: string;
  /** Current producer coverage is explicit; newer kinds may be added additively. */
  readonly kind: string;
  readonly database_id: string;
  readonly name: string;
  readonly state: string;
  readonly stage: string;
  readonly created_at?: string | null;
  readonly revision?: Contracts.UInt64Value | null;
  readonly cancellable: boolean;
  readonly cancellation_reason: string;
};

export type OperationPage = {
  readonly operations: readonly OperationSummary[];
  readonly next_cursor?: string | null;
  readonly covered_kinds: readonly string[];
};

export type OperationListOptions = {
  readonly after?: string | undefined;
  readonly limit?: number | undefined;
  readonly signal?: AbortSignal | undefined;
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
  readonly created_at: Contracts.Int64Value;
  readonly deleted: boolean;
  readonly name: string;
  readonly placement: string;
  readonly residency: string;
  readonly parent: string | null;
};
export type ProvisioningOperation = {
  readonly environment: string;
  readonly tenant_id: string;
  readonly project_id: string;
  readonly operation_id: string;
  readonly database_id: string;
  readonly key_version: string;
  readonly name: string;
  readonly regions: readonly string[];
  readonly residency: string;
  readonly parent_database_id: string | null;
  /** Acknowledged authority progress; acceptance does not mean serving readiness. */
  readonly state: string;
};
export type CreateDatabaseResult = TerraScaleFailure
  | (TerraScaleSuccess<Database> & { readonly status: 201 })
  | (TerraScaleSuccess<ProvisioningOperation> & { readonly status: 202 });
export type RestoreBackupResult = TerraScaleFailure
  | (TerraScaleSuccess<ProvisioningOperation> & { readonly status: 202 });

export type CreateDatabaseRequest = {
  readonly name: string;
  readonly placement: readonly TerraScaleRegionCode[];
} & (
  | {
    /** Stable production provisioning identity; reuse it when resubmitting. */
    readonly operation_id: string;
    readonly residency: string;
  }
  | {
    /** Direct development control-plane creation has no provisioning operation. */
    readonly operation_id?: undefined;
    readonly residency?: string | null;
  }
);
export type DatabaseListResponse = { readonly databases: readonly Database[] };
export type RestoreRequest = { readonly name: string; readonly at?: number };
export type BranchListResponse = { readonly branches: readonly Database[] };
export type BackupStatusResponse = { readonly ready: boolean; readonly streaming: boolean };
export type CollectionsResponse = { readonly collections: readonly string[] };
export type CreateIndexRequest = { readonly field: string };
export type IndexDescriptor = {
  readonly field: string;
  readonly state: "active" | "building";
  readonly backfill_remaining: Contracts.UInt64Value;
};
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
export type ExportDownload = {
  readonly bytes: Uint8Array;
  readonly length: Contracts.UInt64Value;
  readonly checksum: string;
};

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

  create(request: CreateDatabaseRequest, _projectId?: string): Promise<CreateDatabaseResult> {
    validateCreateDatabase(request);
    return this.#http.request({
      method: "POST",
      path: apiRoot + "/databases",
      routeName: "databases.createDatabase",
      body: request,
      idempotencyKey: false,
    }).then(decodeCreateDatabaseResult);
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
  createDatabase(request: CreateDatabaseRequest): Promise<CreateDatabaseResult> { return this.create(request); }
  getDatabase(databaseId: string): Promise<TerraScaleResult<Database>> { return this.get(databaseId); }
  deleteDatabase(databaseId: string, options: DeleteDatabaseOptions): Promise<TerraScaleResult<Database>> { return this.delete(databaseId, options); }
  restoreDatabase(databaseId: string): Promise<TerraScaleResult<Database>> { return this.restore(databaseId); }
}

export class ManagementOperationsClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(options: OperationListOptions = {}): Promise<TerraScaleResult<OperationPage>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 50)) {
      throw new TypeError("limit must be an integer between 1 and 50.");
    }
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/operations`,
      routeName: "databases.listOperations",
      query: {
        ...(options.after === undefined ? {} : { after: options.after }),
        ...(options.limit === undefined ? {} : { limit: options.limit }),
      },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }

  get(operationId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<ProvisioningOperation>> {
    requireText(operationId, "operationId", 128);
    return this.#http.request({ method: "GET", path: `${apiRoot}/operations/${idSegment(operationId, "operation id")}`, routeName: "databases.getProvisioningOperation", signal: options.signal });
  }
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

  restoreBackup(databaseId: string, request: RestoreRequest): Promise<RestoreBackupResult> {
    validateRestoreRequest(request);
    return this.#http.request<unknown>({
      method: "POST",
      path: `${databasePath(databaseId)}/backups/restore`,
      routeName: "databases.restoreBackup",
      body: request,
      idempotencyKey: false,
    }).then(decodeRestoreBackupResult);
  }

  restore(databaseId: string, request: RestoreRequest): Promise<RestoreBackupResult> {
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

  get(databaseId: string, collection: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.SchemaDocument>> {
    return this.#http.request({ method: "GET", path: `${collectionPath(databaseId, collection)}/schema`, routeName: "databases.getSchema", signal: options.signal });
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

  createIndex(databaseId: string, collection: string, request: CreateIndexRequest, options: ManagementOperationOptions = {}): Promise<TerraScaleResult<IndexCreatedResponse>> {
    validateCreateIndexRequest(request);
    return this.#http.request({
      method: "POST",
      path: `${collectionPath(databaseId, collection)}/indexes`,
      routeName: "databases.createIndex",
      signal: options.signal,
      body: request,
      idempotencyKey: false,
    });
  }

  deleteIndex(databaseId: string, collection: string, field: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `${collectionPath(databaseId, collection)}/indexes/${idSegment(field, "field")}`,
      routeName: "databases.deleteIndex",
      signal: options.signal,
      idempotencyKey: false,
    });
  }

  recommendations(databaseId: string, collection: string, options: ManagementRequestOptions & { readonly limit?: number } = {}): Promise<TerraScaleResult<Contracts.IndexRecommendations>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 64)) throw new TypeError("limit must be an integer between 1 and 64.");
    return this.#http.request({ method: "GET", path: `${collectionPath(databaseId, collection)}/indexes/recommendations`, routeName: "databases.indexRecommendations", query: { limit: options.limit }, signal: options.signal });
  }

  listCollections(databaseId: string): Promise<TerraScaleResult<CollectionsResponse>> { return this.list(databaseId); }
}

export class ManagementExportsClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(databaseId: string, options: ManagementRequestOptions & { readonly after?: string; readonly limit?: number } = {}): Promise<TerraScaleResult<Contracts.ExportList>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 50)) throw new TypeError("limit must be an integer between 1 and 50.");
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/exports`, routeName: "databases.listExports", signal: options.signal, query: { after: options.after, limit: options.limit } });
  }

  cancel(databaseId: string, jobId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.ExportStatus>> {
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}/cancel`, routeName: "databases.cancelExport", signal: options.signal, idempotencyKey: false });
  }

  create(databaseId: string, request: Contracts.CreateExportRequest, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.ExportAccepted>> {
    requireText(request?.idempotency_key, "idempotency_key", 512);
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/exports`, routeName: "databases.createExport", signal: options.signal, body: request, idempotencyKey: false });
  }

  get(databaseId: string, jobId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.ExportStatus>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}`, routeName: "databases.getExport", signal: options.signal });
  }

  download(databaseId: string, jobId: string, member: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<ExportDownload>> {
    return this.#http.request<Uint8Array>({ method: "GET", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}/artifacts/${idSegment(member, "artifact member")}`, routeName: "databases.downloadExport", signal: options.signal }).then(decodeExportDownload);
  }

  resume(databaseId: string, jobId: string, request: Contracts.ResumeExportRequest, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.ExportStatus>> {
    requireText(request?.resume_cursor, "resume_cursor");
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/exports/${idSegment(jobId, "job id")}/resume`, routeName: "databases.resumeExport", signal: options.signal, body: request, idempotencyKey: false });
  }
}

export type WebhookCreateOptions = ManagementRequestOptions & { readonly idempotencyKey?: string };

function validateWebhookEndpoint(raw: unknown): string {
  if (typeof raw !== "string" || raw.trim() !== raw || raw.length === 0 || /[^\x00-\x7f]/.test(raw)) {
    throw new TypeError("webhook endpoint is invalid.");
  }
  if ([...raw].some(character => /\s|[\u0000-\u001f\u007f]/.test(character)) || raw.includes("#") || raw.includes("?")) {
    throw new TypeError("webhook endpoint is invalid.");
  }
  const authority = raw.slice("https://".length).split(/[/?#]/, 1)[0] ?? "";
  if (raw.startsWith("https://") && (authority === "" || authority.includes("@") || authority.includes(".."))) {
    throw new TypeError("webhook endpoint host is invalid.");
  }
  let endpoint: URL;
  try { endpoint = new URL(raw); } catch { throw new TypeError("webhook endpoint is invalid."); }
  if (endpoint.protocol !== "https:" || endpoint.username !== "" || endpoint.password !== "" || endpoint.search !== "" || endpoint.hash !== "" || endpoint.hostname === "") {
    throw new TypeError("webhook endpoint must be an HTTPS URL without credentials, query, or fragment.");
  }
  const host = endpoint.hostname.toLowerCase();
  if (host === "localhost" || host.endsWith(".localhost") || host.endsWith(".local") || host.endsWith(".internal") || host.endsWith(".home.arpa") || host.startsWith(".") || host.endsWith(".") || host.includes("..")) {
    throw new TypeError("webhook endpoint host is not public.");
  }
  const octets = host.match(/^((?:\d{1,3}\.){3}\d{1,3})$/)?.[1]?.split(".").map(Number);
  const [first, second] = octets ?? [];
  if (octets && (octets.some(part => part > 255) || first === 0 || first === 10 || first === 127 || (first === 192 && second === 168) || (first === 172 && second !== undefined && second >= 16 && second <= 31) || (first === 169 && second === 254))) {
    throw new TypeError("webhook endpoint host is not public.");
  }
  return endpoint.toString();
}

export class ManagementWebhooksClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(databaseId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.WebhookList>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/webhooks`, routeName: "databases.listWebhooks", signal: options.signal });
  }

  create(databaseId: string, request: Contracts.CreateWebhookRequest, options: WebhookCreateOptions = {}): Promise<TerraScaleResult<Contracts.WebhookSecretResponse>> {
    if (options.idempotencyKey !== undefined) requireText(options.idempotencyKey, "idempotencyKey", 128);
    const endpoint = validateWebhookEndpoint(request?.endpoint);
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/webhooks`, routeName: "databases.createWebhook", signal: options.signal, body: { ...request, endpoint }, idempotencyKey: options.idempotencyKey ?? false });
  }

  rotateSecret(databaseId: string, webhookId: string, request: Contracts.RotateWebhookSecretRequest = {}, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.WebhookSecretResponse>> {
    if (request.overlap_seconds !== undefined && (!Number.isInteger(request.overlap_seconds) || request.overlap_seconds < 0 || request.overlap_seconds > 3600)) throw new TypeError("overlap_seconds must be an integer between 0 and 3600.");
    return this.#http.request({ method: "POST", path: `${webhookPath(databaseId, webhookId)}/rotate-secret`, routeName: "databases.rotateSecret", signal: options.signal, body: request, idempotencyKey: false });
  }

  delete(databaseId: string, webhookId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.WebhookEndpoint>> {
    return this.#http.request({ method: "DELETE", path: webhookPath(databaseId, webhookId), routeName: "databases.deleteWebhook", signal: options.signal, idempotencyKey: false });
  }

  deliveries(databaseId: string, webhookId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.WebhookDeliveryLog>> {
    return this.#http.request({ method: "GET", path: `${webhookPath(databaseId, webhookId)}/deliveries`, routeName: "databases.listDeliveries", signal: options.signal });
  }

  replay(databaseId: string, webhookId: string, deliveryId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.WebhookDelivery>> {
    return this.#http.request({ method: "POST", path: `${webhookPath(databaseId, webhookId)}/deliveries/${idSegment(deliveryId, "delivery id")}/replay`, routeName: "databases.replayWebhook", signal: options.signal, idempotencyKey: false });
  }
}

/** Bounded, authorized admission faults and diagnostics for registered cloud branches. */
export class ManagementDevelopmentClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  setFault(databaseId: string, request: Contracts.DevelopmentFaultRequest, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.DevelopmentFaultResponse>> {
    if (request.confirmBranch !== databaseId) throw new TypeError("confirmBranch must match the target database branch.");
    if (!Number.isInteger(request.durationMs) || request.durationMs < 1 || request.durationMs > 30000) throw new TypeError("durationMs must be an integer between 1 and 30000.");
    if (!Number.isInteger(request.maxOperations) || request.maxOperations < 1 || request.maxOperations > 100) throw new TypeError("maxOperations must be an integer between 1 and 100.");
    if (request.fault !== "latency" && request.fault !== "unavailable") throw new TypeError("fault must be latency or unavailable.");
    if (!Number.isInteger(request.delayMs) || (request.fault === "latency" ? request.delayMs < 1 || request.delayMs > 1000 : request.delayMs !== 0)) throw new TypeError("delayMs must be 1 to 1000 for latency and zero for unavailable.");
    return this.#http.request({ method: "POST", path: `${databasePath(databaseId)}/dev/faults`, routeName: "databases.developmentFault", signal: options.signal, body: request, idempotencyKey: false });
  }

  clearFault(databaseId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.DevelopmentFaultResponse>> {
    return this.#http.request({ method: "DELETE", path: `${databasePath(databaseId)}/dev/faults`, routeName: "databases.clearDevelopmentFault", signal: options.signal, idempotencyKey: false });
  }

  logs(databaseId: string, options: ManagementRequestOptions & { readonly limit?: number } = {}): Promise<TerraScaleResult<Contracts.DevelopmentLogResponse>> {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 100)) throw new TypeError("limit must be an integer between 1 and 100.");
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/dev/logs`, routeName: "databases.developmentLogs", signal: options.signal, query: { limit: options.limit } });
  }
}

export class ManagementImportsClient {
  create(_databaseId: string, _request: unknown, _options?: ManagementOperationOptions): Promise<never> { return contractGap("imports.create"); }
}

export class ManagementUsageClient {
  readonly #http: TerraScaleHttp;
  constructor(http: TerraScaleHttp) { this.#http = http; }

  getDatabase(databaseId: string, options: ManagementRequestOptions = {}): Promise<TerraScaleResult<Contracts.DatabaseUsageSummary>> {
    return this.#http.request({ method: "GET", path: `${databasePath(databaseId)}/usage`, routeName: "databases.databaseUsage", signal: options.signal });
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
  readonly operations: ManagementOperationsClient;
  readonly branches: ManagementBranchesClient;
  readonly backups: ManagementBackupsClient;
  readonly schemas: ManagementSchemasClient;
  readonly collections: ManagementCollectionsClient;
  readonly exports: ManagementExportsClient;
  readonly imports: ManagementImportsClient;
  readonly development: ManagementDevelopmentClient;
  readonly webhooks: ManagementWebhooksClient;
  readonly usage: ManagementUsageClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.#http = new TerraScaleHttp(config);
    this.databases = new ManagementDatabasesClient(this.#http);
    this.operations = new ManagementOperationsClient(this.#http);
    this.branches = new ManagementBranchesClient(this.#http);
    this.backups = new ManagementBackupsClient(this.#http);
    this.schemas = new ManagementSchemasClient(this.#http);
    this.collections = new ManagementCollectionsClient(this.#http);
    this.exports = new ManagementExportsClient(this.#http);
    this.imports = new ManagementImportsClient();
    this.development = new ManagementDevelopmentClient(this.#http);
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
  if (request.operation_id !== undefined) {
    requireText(request.operation_id, "operation_id", 128);
    if (typeof request.residency !== "string" || request.residency.length === 0) throw new TypeError("production provisioning requires a residency string.");
  }
  const placement = normalizeRegionCodes(request?.placement);
  if (placement.length !== request.placement.length) {
    throw new TypeError("placement must not contain duplicate region codes.");
  }
  if (request.residency !== undefined && request.residency !== null &&
    (typeof request.residency !== "string" || request.residency.trim().length === 0)) {
    throw new TypeError("residency must be null or a nonblank string.");
  }
}

function decodeExportDownload(result: TerraScaleResult<Uint8Array>): TerraScaleResult<ExportDownload> {
  if (!result.ok) return result;
  const lengthHeader = result.headers.get("x-terrascale-artifact-length");
  const checksumHeader = result.headers.get("x-terrascale-artifact-checksum");
  const length = parseCanonicalUInt64Header(lengthHeader);
  if (length === undefined) {
    return err(result.routeName, { kind: "invalid-response", code: "invalid_artifact_length", message: "Export artifact length header is invalid.", status: result.status }, result.status, result.headers);
  }
  if (checksumHeader === null || checksumHeader.includes(",") || !/^[0-9a-fA-F]{64}$/u.test(checksumHeader)) {
    return err(result.routeName, { kind: "invalid-response", code: "invalid_artifact_checksum", message: "Export artifact checksum header is invalid.", status: result.status }, result.status, result.headers);
  }
  if (BigInt(result.value.byteLength) !== BigInt(length)) {
    return err(result.routeName, { kind: "invalid-response", code: "artifact_length_mismatch", message: "Export artifact length header does not match the response body.", status: result.status }, result.status, result.headers);
  }
  return { ...result, value: { bytes: result.value, length, checksum: checksumHeader.toLowerCase() } };
}

function parseCanonicalUInt64Header(value: string | null): Contracts.UInt64Value | undefined {
  if (value === null || value.includes(",") || !/^(?:0|[1-9][0-9]*)$/u.test(value)) return undefined;
  try {
    const parsed = BigInt(value);
    if (parsed > 18446744073709551615n) return undefined;
    return parsed <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(parsed) : parsed;
  } catch {
    return undefined;
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
  if (typeof value !== "string" || value.trim().length === 0 || /\p{Cc}/u.test(value) || (maximumBytes !== undefined && new TextEncoder().encode(value).length > maximumBytes)) throw new TypeError(`${name} must be nonblank${maximumBytes === undefined ? "" : ` and at most ${maximumBytes} UTF-8 bytes`}.`);
}

function decodeCreateDatabaseResult(result: TerraScaleResult<unknown>): CreateDatabaseResult {
  if (!result.ok) return result;
  if (result.status === 201 && isDatabase(result.value)) return { ...result, status: 201, value: result.value };
  if (result.status === 202 && isProvisioningOperation(result.value)) return { ...result, status: 202, value: result.value };
  return { ok: false, routeName: result.routeName, status: result.status, headers: result.headers,
    error: { kind: "invalid-response", code: "invalid_create_response", message: "Database creation must return a 201 database or a 202 provisioning operation.", status: result.status }, body: result.value };
}

function decodeRestoreBackupResult(result: TerraScaleResult<unknown>): RestoreBackupResult {
  if (!result.ok) return result;
  if (result.status === 202 && isProvisioningOperation(result.value)) return { ...result, status: 202, value: result.value };
  return { ok: false, routeName: result.routeName, status: result.status, headers: result.headers,
    error: { kind: "invalid-response", code: "invalid_restore_response", message: "Backup restore must return a 202 provisioning operation.", status: result.status }, body: result.value };
}

function isDatabase(value: unknown): value is Database {
  if (typeof value !== "object" || value === null) return false;
  const record = value as Record<string, unknown>;
  return (typeof record.created_at === "number" || typeof record.created_at === "bigint") && typeof record.deleted === "boolean"
    && typeof record.name === "string" && typeof record.placement === "string" && typeof record.residency === "string"
    && (record.parent === null || typeof record.parent === "string");
}

function isProvisioningOperation(value: unknown): value is ProvisioningOperation {
  if (typeof value !== "object" || value === null) return false;
  const record = value as Record<string, unknown>;
  return ["environment", "tenant_id", "project_id", "operation_id", "database_id", "key_version", "name", "residency", "state"].every(key => typeof record[key] === "string")
    && (record.parent_database_id === null || typeof record.parent_database_id === "string")
    && Array.isArray(record.regions) && record.regions.every(region => typeof region === "string");
}
