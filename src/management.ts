import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp, type TerraScaleRouteName } from "./http.js";
import { normalizeRegionCodes, type TerraScaleRegionCode } from "./regions.js";
import type { TerraScaleResult } from "./result.js";

export type { TerraScaleRuntimeConfig } from "./config.js";

const apiRoot = "/api/v1";

export type ManagementPageRequest = {
  readonly skip?: number;
  readonly take?: number;
  readonly cursor?: string;
  readonly limit?: number;
};

export type ManagementOperationOptions = {
  /** Stable identity for a retryable mutation. `false` opts out only where the contract permits it. */
  readonly idempotencyKey?: string | false;
  /** Project scope for project-owned routes; otherwise the runtime config is used. */
  readonly projectId?: string;
};

export type PlacementLock =
  | "eu-only"
  | "br-only"
  | readonly TerraScaleRegionCode[];

export type CreateDatabaseRequest = {
  readonly name: string;
  readonly placement: readonly TerraScaleRegionCode[];
  readonly residency?: Exclude<PlacementLock, readonly TerraScaleRegionCode[]>;
};

export type Database = {
  readonly databaseId: string;
  readonly name: string;
  readonly placement: readonly TerraScaleRegionCode[];
  readonly residency?: string | null;
  readonly deleted?: boolean;
  readonly createdAt?: string;
  readonly [key: string]: unknown;
};

export type DatabaseListResponse = {
  readonly databases: readonly Database[];
};

export type DeleteDatabaseOptions = ManagementOperationOptions & {
  readonly confirm?: true;
};

export type DatabaseBranch = {
  readonly branchId: string;
  readonly sourceDatabaseId: string;
  readonly name?: string;
  readonly placement?: readonly TerraScaleRegionCode[];
  readonly status?: string;
  readonly createdAt?: string;
  readonly [key: string]: unknown;
};

export type BranchListResponse = {
  readonly branches: readonly DatabaseBranch[];
};

export type CreateBranchRequest = {
  readonly name: string;
  readonly at?: number;
  readonly placement?: PlacementLock;
};

export type Collection = {
  readonly name: string;
  readonly [key: string]: unknown;
};

export type CollectionListResponse = {
  readonly collections: readonly string[];
};

export type CollectionIndex = {
  readonly field: string;
  readonly state?: string;
  readonly backfillRemaining?: number;
  readonly [key: string]: unknown;
};

export type CreateIndexRequest = {
  readonly field: string;
};

export type CollectionSchema = {
  readonly name?: string;
  readonly fields?: Readonly<Record<string, string | Readonly<Record<string, unknown>>>>;
  readonly indexes?: readonly CreateIndexRequest[];
  readonly ttl?: Readonly<Record<string, unknown>> | null;
  readonly historyRetentionDays?: number;
  readonly merge?: Readonly<Record<string, unknown>> | null;
  readonly [key: string]: unknown;
};

export type Backup = {
  readonly backupId?: string;
  readonly databaseId: string;
  readonly status?: string;
  readonly ready?: boolean;
  readonly streaming?: boolean;
  readonly createdAt?: string;
  readonly [key: string]: unknown;
};

export type BackupStatus = {
  readonly ready: boolean;
  readonly streaming: boolean;
};

export type BackupListRequest = ManagementPageRequest;

export type RestoreRequest = {
  readonly name: string;
  readonly at?: number;
  readonly placement?: PlacementLock;
};

export type RestorePoint = {
  readonly name: string;
  readonly createdAt?: string;
  readonly backupId?: string | null;
  readonly [key: string]: unknown;
};

export type ExportFormat = "bundle" | "jsonl";

export type CreateExportRequest = {
  readonly format?: ExportFormat;
  readonly includeHistory?: boolean;
};

export type ExportJob = {
  readonly exportId: string;
  readonly databaseId: string;
  readonly format?: ExportFormat;
  readonly status?: string;
  readonly progress?: number;
  readonly downloadUrl?: string | null;
  readonly [key: string]: unknown;
};

export type ImportFormat = "jsonl" | "csv" | "mongo" | "cosmos" | "dynamodb";

export type CreateImportRequest = {
  readonly format: ImportFormat;
  readonly source: string;
  readonly name?: string;
  readonly placement?: PlacementLock;
};

export type ImportJob = {
  readonly importId: string;
  readonly databaseId?: string;
  readonly status?: string;
  readonly progress?: number;
  readonly [key: string]: unknown;
};

export type ApiKey = {
  readonly keyId: string;
  readonly name: string;
  readonly prefix?: string;
  readonly scopes?: readonly string[];
  readonly createdAt?: string;
  readonly revokedAt?: string | null;
  readonly [key: string]: unknown;
};

export type CreateApiKeyRequest = {
  readonly name: string;
  readonly scopes?: readonly string[];
  readonly expiresAt?: string | null;
};

export type CreatedApiKey = ApiKey & { readonly secret: string };
export type RotatedApiKey = ApiKey & { readonly secret: string };

export type Usage = {
  readonly databaseId?: string;
  readonly projectId?: string;
  readonly writes?: number | string;
  readonly storageBytes?: number | string;
  readonly periodStart?: string;
  readonly periodEnd?: string;
  readonly [key: string]: unknown;
};

export type ManagementRouteName =
  | "databases.list"
  | "databases.create"
  | "databases.get"
  | "databases.delete"
  | "databases.restore"
  | "branches.list"
  | "branches.create"
  | "collections.list"
  | "collections.indexes.list"
  | "collections.indexes.create"
  | "collections.indexes.delete"
  | "collections.indexes.recommendations"
  | "schemas.get"
  | "schemas.put"
  | "backups.list"
  | "backups.restore"
  | "exports.create"
  | "imports.create"
  | "apiKeys.list"
  | "apiKeys.create"
  | "apiKeys.rotate"
  | "apiKeys.revoke"
  | "usage.database.get"
  | "usage.project.get"
  | "usage.organization.get";

type RequestRouteName = TerraScaleRouteName & ManagementRouteName;

export class ManagementDatabasesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(): Promise<TerraScaleResult<DatabaseListResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/databases`,
      routeName: route("databases.list"),
    });
  }

  create(
    request: CreateDatabaseRequest,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<Database>> {
    const body = validateCreateDatabase(request);
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/databases`,
      routeName: route("databases.create"),
      body,
      idempotencyKey: options?.idempotencyKey,
    });
  }

  get(databaseId: string): Promise<TerraScaleResult<Database>> {
    const id = idSegment(databaseId, "databaseId");
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/databases/${id}`,
      routeName: route("databases.get"),
    });
  }

  delete(
    databaseId: string,
    options: DeleteDatabaseOptions = {},
  ): Promise<TerraScaleResult<Database>> {
    const id = idSegment(databaseId, "databaseId");
    if (options.confirm !== undefined && options.confirm !== true) {
      throw new TypeError("Database deletion requires options.confirm to be true.");
    }
    return this.#http.request({
      method: "DELETE",
      path: `${apiRoot}/databases/${id}`,
      routeName: route("databases.delete"),
      headers: options.confirm === true ? { "x-terrascale-delete-confirmation": databaseId } : undefined,
      idempotencyKey: options.idempotencyKey,
    });
  }

  restore(
    databaseId: string,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<Database>> {
    const id = idSegment(databaseId, "databaseId");
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/databases/${id}/restore`,
      routeName: route("databases.restore"),
      idempotencyKey: options?.idempotencyKey,
    });
  }
}

export class ManagementBranchesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(databaseId: string): Promise<TerraScaleResult<BranchListResponse>> {
    const id = idSegment(databaseId, "databaseId");
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/databases/${id}/branches`,
      routeName: route("branches.list"),
    });
  }

  create(
    databaseId: string,
    request: CreateBranchRequest,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<DatabaseBranch>> {
    const id = idSegment(databaseId, "databaseId");
    const body = validateBranch(request);
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/databases/${id}/branches`,
      routeName: route("branches.create"),
      body,
      idempotencyKey: options?.idempotencyKey,
    });
  }
}

export class ManagementCollectionsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(databaseId: string): Promise<TerraScaleResult<CollectionListResponse>> {
    const id = idSegment(databaseId, "databaseId");
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/databases/${id}/collections`,
      routeName: route("collections.list"),
    });
  }

  listIndexes(databaseId: string, collection: string): Promise<TerraScaleResult<readonly CollectionIndex[]>> {
    const base = collectionPath(databaseId, collection);
    return this.#http.request({ method: "GET", path: `${base}/indexes`, routeName: route("collections.indexes.list") });
  }

  createIndex(
    databaseId: string,
    collection: string,
    request: CreateIndexRequest,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<CollectionIndex>> {
    const base = collectionPath(databaseId, collection);
    const body = validateIndex(request);
    return this.#http.request({ method: "POST", path: `${base}/indexes`, routeName: route("collections.indexes.create"), body, idempotencyKey: options?.idempotencyKey });
  }

  deleteIndex(
    databaseId: string,
    collection: string,
    field: string,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<void>> {
    const base = collectionPath(databaseId, collection);
    const index = idSegment(field, "field");
    return this.#http.request({ method: "DELETE", path: `${base}/indexes/${index}`, routeName: route("collections.indexes.delete"), idempotencyKey: options?.idempotencyKey });
  }

  listIndexRecommendations(databaseId: string, collection: string): Promise<TerraScaleResult<readonly CollectionIndex[]>> {
    const base = collectionPath(databaseId, collection);
    return this.#http.request({ method: "GET", path: `${base}/indexes/recommendations`, routeName: route("collections.indexes.recommendations") });
  }
}

export class ManagementSchemasClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  get(databaseId: string, collection: string): Promise<TerraScaleResult<CollectionSchema>> {
    const base = collectionPath(databaseId, collection);
    return this.#http.request({ method: "GET", path: `${base}/schema`, routeName: route("schemas.get") });
  }

  put(
    databaseId: string,
    collection: string,
    schema: CollectionSchema,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<CollectionSchema>> {
    const base = collectionPath(databaseId, collection);
    return this.#http.request({ method: "PUT", path: `${base}/schema`, routeName: route("schemas.put"), body: schema, idempotencyKey: options?.idempotencyKey });
  }

  update(
    databaseId: string,
    collection: string,
    schema: CollectionSchema,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<CollectionSchema>> {
    return this.put(databaseId, collection, schema, options);
  }
}

export class ManagementBackupsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(databaseId: string): Promise<TerraScaleResult<BackupStatus>> {
    const id = idSegment(databaseId, "databaseId");
    return this.#http.request({ method: "GET", path: `${apiRoot}/databases/${id}/backups`, routeName: route("backups.list") });
  }

  restore(
    databaseId: string,
    request: RestoreRequest,
    options?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<Database>> {
    const id = idSegment(databaseId, "databaseId");
    const body = validateRestore(request);
    return this.#http.request({ method: "POST", path: `${apiRoot}/databases/${id}/backups/restore`, routeName: route("backups.restore"), body, idempotencyKey: options?.idempotencyKey });
  }

}

export class ManagementExportsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  create(databaseId: string, request: CreateExportRequest = {}, options?: ManagementOperationOptions): Promise<TerraScaleResult<ExportJob>> {
    const id = idSegment(databaseId, "databaseId");
    return this.#http.request({ method: "POST", path: `${apiRoot}/databases/${id}/exports`, routeName: route("exports.create"), body: request, idempotencyKey: options?.idempotencyKey });
  }
}

export class ManagementImportsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  create(databaseId: string, request: CreateImportRequest, options?: ManagementOperationOptions): Promise<TerraScaleResult<ImportJob>> {
    const id = idSegment(databaseId, "databaseId");
    const body = validateImport(request);
    return this.#http.request({ method: "POST", path: `${apiRoot}/databases/${id}/imports`, routeName: route("imports.create"), body, idempotencyKey: options?.idempotencyKey });
  }
}

export class ManagementApiKeysClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  list(query?: ManagementPageRequest, options?: ManagementOperationOptions): Promise<TerraScaleResult<readonly ApiKey[]>>;
  list(projectId: string | undefined, query?: ManagementPageRequest, options?: ManagementOperationOptions): Promise<TerraScaleResult<readonly ApiKey[]>>;
  list(
    projectIdOrQuery?: string | ManagementPageRequest,
    queryOrOptions?: ManagementPageRequest | ManagementOperationOptions,
    explicitOptions?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<readonly ApiKey[]>> {
    const hasProject = typeof projectIdOrQuery === "string" || projectIdOrQuery === undefined;
    const projectId = hasProject ? projectIdOrQuery : undefined;
    const query = (hasProject ? queryOrOptions : projectIdOrQuery) as ManagementPageRequest | undefined;
    const options = (hasProject ? explicitOptions : queryOrOptions) as ManagementOperationOptions | undefined;
    return this.#http.request({ method: "GET", path: `${projectPath(this.#http, projectId ?? options?.projectId)}/auth/api-keys`, routeName: route("apiKeys.list"), query });
  }

  create(request: CreateApiKeyRequest, options?: ManagementOperationOptions): Promise<TerraScaleResult<CreatedApiKey>>;
  create(projectId: string | undefined, request: CreateApiKeyRequest, options?: ManagementOperationOptions): Promise<TerraScaleResult<CreatedApiKey>>;
  create(
    projectIdOrRequest: string | undefined | CreateApiKeyRequest,
    requestOrOptions?: CreateApiKeyRequest | ManagementOperationOptions,
    explicitOptions?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<CreatedApiKey>> {
    const hasProject = typeof projectIdOrRequest === "string" || projectIdOrRequest === undefined;
    const projectId = hasProject ? projectIdOrRequest : undefined;
    const request = (hasProject ? requestOrOptions : projectIdOrRequest) as CreateApiKeyRequest;
    const options = (hasProject ? explicitOptions : requestOrOptions) as ManagementOperationOptions | undefined;
    const body = validateApiKey(request);
    return this.#http.request({ method: "POST", path: `${projectPath(this.#http, projectId ?? options?.projectId)}/auth/api-keys`, routeName: route("apiKeys.create"), body, idempotencyKey: options?.idempotencyKey });
  }

  rotate(keyId: string, options?: ManagementOperationOptions): Promise<TerraScaleResult<RotatedApiKey>>;
  rotate(projectId: string | undefined, keyId: string, options?: ManagementOperationOptions): Promise<TerraScaleResult<RotatedApiKey>>;
  rotate(
    projectIdOrKeyId: string | undefined,
    keyIdOrOptions: string | ManagementOperationOptions | undefined,
    explicitOptions?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<RotatedApiKey>> {
    const hasProject = typeof keyIdOrOptions === "string";
    const projectId = hasProject ? projectIdOrKeyId : undefined;
    const keyId = (hasProject ? keyIdOrOptions : projectIdOrKeyId) as string;
    const options = (hasProject ? explicitOptions : keyIdOrOptions) as ManagementOperationOptions | undefined;
    const path = `${projectPath(this.#http, projectId ?? options?.projectId)}/auth/api-keys/${idSegment(keyId, "keyId")}/rotate`;
    return this.#http.request({ method: "POST", path, routeName: route("apiKeys.rotate"), idempotencyKey: options?.idempotencyKey });
  }

  revoke(keyId: string, options?: ManagementOperationOptions): Promise<TerraScaleResult<void>>;
  revoke(projectId: string | undefined, keyId: string, options?: ManagementOperationOptions): Promise<TerraScaleResult<void>>;
  revoke(
    projectIdOrKeyId: string | undefined,
    keyIdOrOptions: string | ManagementOperationOptions | undefined,
    explicitOptions?: ManagementOperationOptions,
  ): Promise<TerraScaleResult<void>> {
    const hasProject = typeof keyIdOrOptions === "string";
    const projectId = hasProject ? projectIdOrKeyId : undefined;
    const keyId = (hasProject ? keyIdOrOptions : projectIdOrKeyId) as string;
    const options = (hasProject ? explicitOptions : keyIdOrOptions) as ManagementOperationOptions | undefined;
    const path = `${projectPath(this.#http, projectId ?? options?.projectId)}/auth/api-keys/${idSegment(keyId, "keyId")}`;
    return this.#http.request({ method: "DELETE", path, routeName: route("apiKeys.revoke"), idempotencyKey: options?.idempotencyKey });
  }
}

export class ManagementUsageClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) { this.#http = http; }

  getDatabase(databaseId: string): Promise<TerraScaleResult<Usage>> {
    return this.#http.request({ method: "GET", path: `${apiRoot}/databases/${idSegment(databaseId, "databaseId")}/usage`, routeName: route("usage.database.get") });
  }

  getProject(projectId?: string, options?: ManagementOperationOptions): Promise<TerraScaleResult<Usage>> {
    return this.#http.request({ method: "GET", path: `${projectPath(this.#http, projectId ?? options?.projectId)}/usage`, routeName: route("usage.project.get") });
  }

  getOrganization(organizationId: string): Promise<TerraScaleResult<Usage>> {
    const id = idSegment(organizationId, "organizationId");
    return this.#http.request({ method: "GET", path: `${apiRoot}/organizations/${id}/usage`, routeName: route("usage.organization.get") });
  }

  getProjectUsage(projectId?: string, options?: ManagementOperationOptions): Promise<TerraScaleResult<Usage>> { return this.getProject(projectId, options); }
}

export class TerraScaleManagementClient {
  readonly #http: TerraScaleHttp;
  readonly databases: ManagementDatabasesClient;
  readonly branches: ManagementBranchesClient;
  readonly collections: ManagementCollectionsClient;
  readonly schemas: ManagementSchemasClient;
  readonly backups: ManagementBackupsClient;
  readonly exports: ManagementExportsClient;
  readonly imports: ManagementImportsClient;
  readonly apiKeys: ManagementApiKeysClient;
  readonly usage: ManagementUsageClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.#http = new TerraScaleHttp(config);
    this.databases = new ManagementDatabasesClient(this.#http);
    this.branches = new ManagementBranchesClient(this.#http);
    this.collections = new ManagementCollectionsClient(this.#http);
    this.schemas = new ManagementSchemasClient(this.#http);
    this.backups = new ManagementBackupsClient(this.#http);
    this.exports = new ManagementExportsClient(this.#http);
    this.imports = new ManagementImportsClient(this.#http);
    this.apiKeys = new ManagementApiKeysClient(this.#http);
    this.usage = new ManagementUsageClient(this.#http);
  }

  get config() { return this.#http.config; }
}

/** Short name used by consumers that construct the management client directly. */
export { TerraScaleManagementClient as ManagementClient };

export function createTerraScaleManagementClient(config: TerraScaleRuntimeConfig): TerraScaleManagementClient {
  return new TerraScaleManagementClient(config);
}

function route(name: ManagementRouteName): RequestRouteName {
  return name as RequestRouteName;
}

function idSegment(value: string, name: string): string {
  if (typeof value !== "string" || value.trim().length === 0) throw new TypeError(`${name} must be nonblank.`);
  return encodeURIComponent(value);
}

function projectPath(http: TerraScaleHttp, projectId: string | undefined): string {
  const selected = projectId ?? http.config.projectId;
  return `${apiRoot}/projects/${idSegment(selected ?? "", "projectId")}`;
}

function collectionPath(databaseId: string, collection: string): string {
  return `${apiRoot}/databases/${idSegment(databaseId, "databaseId")}/collections/${idSegment(collection, "collection")}`;
}

function validateCreateDatabase(request: CreateDatabaseRequest): CreateDatabaseRequest {
  if (typeof request?.name !== "string" || request.name.trim().length === 0) throw new TypeError("name must be nonblank.");
  const placement = normalizeRegionCodes(request.placement);
  if (request.residency !== undefined && request.residency !== "eu-only" && request.residency !== "br-only") throw new TypeError("residency must be eu-only or br-only.");
  if (request.residency === "eu-only" && placement.some((region) => region !== "FRA")) {
    throw new TypeError("eu-only residency cannot be widened beyond FRA.");
  }
  if (request.residency === "br-only" && placement.some((region) => region !== "GRU")) {
    throw new TypeError("br-only residency cannot be widened beyond GRU.");
  }
  return { ...request, placement };
}

function validateBranch(request: CreateBranchRequest): CreateBranchRequest {
  if (typeof request?.name !== "string" || request.name.trim().length === 0) throw new TypeError("name must be nonblank.");
  return { ...request, ...(request.placement === undefined ? {} : { placement: validatePlacement(request.placement) }) };
}

function validateRestore(request: RestoreRequest): RestoreRequest {
  if (typeof request?.name !== "string" || request.name.trim().length === 0) throw new TypeError("name must be nonblank.");
  return { ...request, ...(request.placement === undefined ? {} : { placement: validatePlacement(request.placement) }) };
}

function validatePlacement(placement: PlacementLock): PlacementLock {
  if (typeof placement === "string") {
    if (placement !== "eu-only" && placement !== "br-only") throw new TypeError("placement must be eu-only, br-only, or a region list.");
    return placement;
  }
  return normalizeRegionCodes(placement);
}

function validateIndex(request: CreateIndexRequest): CreateIndexRequest {
  if (typeof request?.field !== "string" || request.field.trim().length === 0) throw new TypeError("field must be nonblank.");
  return request;
}

function validateImport(request: CreateImportRequest): CreateImportRequest {
  if (!(["jsonl", "csv", "mongo", "cosmos", "dynamodb"] as readonly string[]).includes(request?.format)) throw new TypeError("format is not a supported import format.");
  if (typeof request?.source !== "string" || request.source.trim().length === 0) throw new TypeError("source must be nonblank.");
  return { ...request, ...(request.placement === undefined ? {} : { placement: validatePlacement(request.placement) }) };
}

function validateApiKey(request: CreateApiKeyRequest): CreateApiKeyRequest {
  if (typeof request?.name !== "string" || request.name.trim().length === 0) throw new TypeError("name must be nonblank.");
  return request;
}
