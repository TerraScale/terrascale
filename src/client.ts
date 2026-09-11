import type { ResolvedTerraScaleRuntimeConfig, TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp, type TerraScaleRouteName } from "./http.js";
import { TerraScaleManagementClient } from "./management.js";
import type * as Models from "./models.js";
import {
  normalizeRegionCodes,
  parseRegionCode,
} from "./regions.js";
import { err, type TerraScaleFailure, type TerraScaleResult, type TerraScaleSuccess } from "./result.js";
import { TerraScaleSyncV1Client } from "./sync/v1/client.js";

const apiRoot = "/api/v1";

export const sdkRouteManifest = {
  onboarding: {
    state: `${apiRoot}/onboarding`,
    organization: `${apiRoot}/onboarding/organization`,
    project: `${apiRoot}/onboarding/project`,
  },
} as const;

export class TerraScaleClient {
  readonly #http: TerraScaleHttp;

  readonly onboarding: OnboardingClient;
  readonly organizations: OrganizationsClient;
  readonly regions: RegionsClient;
  readonly savedQueries: SavedQueriesClient;
  readonly databases: DatabasesClient;
  readonly databaseBranches: DatabaseBranchesClient;
  readonly backups: BackupsClient;
  readonly access: AccessClient;
  readonly usage: UsageClient;
  readonly audit: AuditClient;
  readonly notifications: NotificationsClient;
  /** The separately named, contract-bound management surface. */
  readonly management: TerraScaleManagementClient;
  /** Direct document CRUD against the public database API. */
  readonly documents: DocumentsClient;
  /** Read-only parameterized SQL against a named database. */
  readonly sql: SqlClient;
  /** The canonical reactive view client backed by the sync v1 state machine. */
  readonly reactive: TerraScaleSyncV1Client;

  constructor(config: TerraScaleRuntimeConfig) {
    this.#http = new TerraScaleHttp(config);
    this.onboarding = new OnboardingClient(this.#http);
    this.organizations = new OrganizationsClient(this.#http);
    this.regions = new RegionsClient(this.#http);
    this.savedQueries = new SavedQueriesClient(this.#http);
    this.databases = new DatabasesClient(this.#http);
    this.databaseBranches = new DatabaseBranchesClient(this.#http);
    this.backups = new BackupsClient(this.#http);
    this.access = new AccessClient(this.#http);
    this.usage = new UsageClient(this.#http);
    this.audit = new AuditClient(this.#http);
    this.notifications = new NotificationsClient(this.#http);
    this.documents = new DocumentsClient(this.#http);
    this.sql = new SqlClient(this.#http);
    this.reactive = new TerraScaleSyncV1Client({
      ...(config.apiBaseUrl === undefined ? {} : { apiBaseUrl: config.apiBaseUrl }),
      ...(config.syncBaseUrl === undefined ? {} : { syncBaseUrl: config.syncBaseUrl }),
      ...(config.fetch === undefined ? {} : { fetch: config.fetch }),
      ...(config.accessToken === undefined ? {} : { accessToken: config.accessToken }),
      ...(config.tenantId === undefined ? {} : { tenantId: config.tenantId }),
      ...(config.projectId === undefined ? {} : { projectId: config.projectId }),
    });

    // Keep these additional public surfaces out of the ordinary client route
    // enumeration. Their operation families have their own named manifests
    // and must not be presented as part of the dashboard product routes.
    this.management = new TerraScaleManagementClient(config);
    Object.defineProperties(this, {
      management: { enumerable: false },
      documents: { enumerable: false },
      sql: { enumerable: false },
      reactive: { enumerable: false },
    });
  }

  get config(): ResolvedTerraScaleRuntimeConfig {
    return this.#http.config;
  }

}

/** One row returned by the collection document listing route. */
export type DocumentRow = {
  readonly id: string;
  /** The producer publishes this as an int64; unsafe values are bigint. */
  readonly version: number | bigint;
  readonly value: Models.DocumentValue;
};

export type DocumentsResponse = {
  readonly documents: readonly DocumentRow[];
};

export type DocumentWriteResponse = {
  readonly revision: string;
};

export type DocumentOperationOptions = {
  /** Stable identity used to safely replay a mutation. */
  readonly idempotencyKey?: string | false | undefined;
  /** Abort before or while waiting for the operation. */
  readonly signal?: AbortSignal | undefined;
  /** Optimistic concurrency precondition, sent as `If-Match`. */
  readonly ifMatch?: string | undefined;
  /** Create only when no live document exists. */
  readonly ifNoneMatch?: "*" | undefined;
  /** Requested acknowledgement class for a write. */
  readonly durability?: "regional" | "global" | undefined;
};

export type DocumentListOptions = {
  readonly signal?: AbortSignal | undefined;
  readonly limit?: number | undefined;
  readonly after?: string | undefined;
};

export type DocumentReadOptions = {
  /** Abort before or while waiting for the operation. */
  readonly signal?: AbortSignal | undefined;
  /** Unix epoch milliseconds for a point-in-time read. */
  readonly at?: number | undefined;
  /** Conditional read precondition, sent as `If-None-Match`. */
  readonly ifNoneMatch?: string | undefined;
  /** Read-your-writes sequence, sent as `X-TerraScale-Session`. */
  /** Opaque base64url causal session token returned by the API. */
  readonly session?: string | undefined;
};

export type DocumentHistoryEntry = {
  readonly revision: string;
  readonly deleted: boolean;
};

export type DocumentHistoryResponse = {
  readonly history: readonly DocumentHistoryEntry[];
};

export type DocumentBatchOperation = {
  readonly op: "put" | "patch" | "delete";
  readonly id: string;
  readonly value?: Models.JsonValue;
};

export type DocumentBatchRequest = {
  readonly operations: readonly DocumentBatchOperation[];
};

export type DocumentBatchResponse = {
  /** The producer publishes this as an int64; unsafe values are bigint. */
  readonly committed: number | bigint;
  readonly count: number;
};

/**
 * Typed access to the public collection document routes. Values are sent as
 * the JSON value itself; this client never introduces a `{ body: ... }`
 * wrapper around a document or patch.
 */
export class DocumentsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(
    database: string,
    collection: string,
    options: DocumentListOptions = {},
  ): Promise<TerraScaleResult<DocumentsResponse>> {
    const path = documentsPath(database, collection);
    validateDocumentListOptions(options);
    return directRequest(this.#http, "databases.listDocuments", {
      method: "GET",
      path,
      query: {
        ...(options.limit === undefined ? {} : { limit: options.limit }),
        ...(options.after === undefined ? {} : { after: options.after }),
      },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }

  get<TValue extends Models.DocumentValue = Models.DocumentValue>(
    database: string,
    collection: string,
    id: string,
    options: DocumentReadOptions = {},
  ): Promise<TerraScaleResult<TValue>> {
    const path = documentPath(database, collection, id);
    validateDocumentReadOptions(options);
    return directRequest(this.#http, "databases.getDocument", {
      method: "GET",
      path,
      query: options.at === undefined ? undefined : { at: options.at },
      ...documentReadRequestOptions(options),
    });
  }

  put<TValue extends Models.JsonValue>(
    database: string,
    collection: string,
    id: string,
    value: TValue,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<DocumentWriteResponse>> {
    const path = documentPath(database, collection, id);
    validateDocumentOperationOptions(options, true);
    validateJsonPayload(value, "document value");
    return directRequest(this.#http, "databases.putDocument", {
      method: "PUT",
      path,
      body: value,
      ...documentRequestOptions(options),
    });
  }

  /** Alias for the create-or-replace PUT operation. */
  create<TValue extends Models.JsonValue>(
    database: string,
    collection: string,
    id: string,
    value: TValue,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<DocumentWriteResponse>> {
    return this.put(database, collection, id, value, options);
  }

  /** Alias making the PUT operation's upsert semantics explicit. */
  upsert<TValue extends Models.JsonValue>(
    database: string,
    collection: string,
    id: string,
    value: TValue,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<DocumentWriteResponse>> {
    return this.put(database, collection, id, value, options);
  }

  patch<TPatch extends Models.JsonValue>(
    database: string,
    collection: string,
    id: string,
    patch: TPatch,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<DocumentWriteResponse>> {
    const path = documentPath(database, collection, id);
    validateDocumentOperationOptions(options);
    validateJsonPayload(patch, "merge patch");
    return directRequest(this.#http, "databases.patchDocument", {
      method: "PATCH",
      path,
      body: patch,
      ...documentRequestOptions(options),
    });
  }

  /** Alias for PATCH, retained for callers that name the operation update. */
  update<TPatch extends Models.JsonValue>(
    database: string,
    collection: string,
    id: string,
    patch: TPatch,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<DocumentWriteResponse>> {
    return this.patch(database, collection, id, patch, options);
  }

  delete(
    database: string,
    collection: string,
    id: string,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<void>> {
    const path = documentPath(database, collection, id);
    validateDocumentOperationOptions(options);
    return directRequest(this.#http, "databases.deleteDocument", {
      method: "DELETE",
      path,
      ...documentRequestOptions(options),
    });
  }

  history(
    database: string,
    collection: string,
    id: string,
    options: DocumentListOptions = {},
  ): Promise<TerraScaleResult<DocumentHistoryResponse>> {
    const path = `${documentPath(database, collection, id)}/history`;
    validateDocumentListOptions(options);
    return directRequest(this.#http, "databases.documentHistory", {
      method: "GET",
      path,
      query: {
        ...(options.limit === undefined ? {} : { limit: options.limit }),
        ...(options.after === undefined ? {} : { after: options.after }),
      },
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }

  batch(
    database: string,
    collection: string,
    request: DocumentBatchRequest,
    options: DocumentOperationOptions = {},
  ): Promise<TerraScaleResult<DocumentBatchResponse>> {
    const path = `${documentsPath(database, collection)}/batch`;
    validateDocumentOperationOptions(options);
    validateDocumentBatchRequest(request);
    return directRequest(this.#http, "databases.batchDocuments", {
      method: "POST",
      path,
      body: request,
      ...documentRequestOptions(options),
    });
  }
}

export type SqlParameters = { readonly [name: string]: Models.JsonValue };

export type SqlExecuteRequest = {
  readonly sql: string;
  readonly parameters?: SqlParameters | undefined;
  readonly explain?: boolean | undefined;
  readonly timeout_ms?: number | undefined;
  /** JavaScript spelling accepted as a convenience; the wire key is `timeout_ms`. */
  readonly timeoutMs?: number | undefined;
  readonly cancelled?: boolean | undefined;
};

export type SqlExecuteOptions = {
  readonly signal?: AbortSignal | undefined;
  readonly explain?: boolean | undefined;
  readonly timeout_ms?: number | undefined;
  readonly timeoutMs?: number | undefined;
  readonly cancelled?: boolean | undefined;
};

export type SqlRow = {
  readonly id: string;
  readonly value: Models.SqlValue;
};

export type SqlResult = {
  readonly matched: number;
  readonly rows: readonly SqlRow[];
};

export type SqlExplainResult = {
  readonly dialect: "sql/sqlite-readonly";
  readonly collection: string | null;
  readonly order: readonly {
    readonly field: string;
    readonly direction: "asc" | "desc";
  }[];
  readonly access: "scan";
};

export type SqlResponse = SqlResult | SqlExplainResult;

/** Read-only, parameterized SQL access to a named database. */
export class SqlClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  execute(
    database: string,
    request: SqlExecuteRequest,
    options?: SqlExecuteOptions,
  ): Promise<TerraScaleResult<SqlResponse>>;
  execute(
    database: string,
    sql: string,
    parameters?: SqlParameters,
    options?: SqlExecuteOptions,
  ): Promise<TerraScaleResult<SqlResponse>>;
  execute(
    database: string,
    requestOrSql: SqlExecuteRequest | string,
    parametersOrOptions?: SqlParameters | SqlExecuteOptions,
    maybeOptions: SqlExecuteOptions = {},
  ): Promise<TerraScaleResult<SqlResponse>> {
    requireDatabaseIdentifier(database);
    const third = parametersOrOptions ?? {};
    // The string overload's third argument is unambiguously the parameter
    // object; options belong in the fourth argument. Treating parameter names
    // such as `signal` or `explain` as option markers silently drops valid SQL
    // bindings. The request-object overload remains intentionally convenient.
    const optionValues = typeof requestOrSql === "string"
      ? maybeOptions
      : isSqlOptions(third) ? third : maybeOptions;
    const request: SqlExecuteRequest = typeof requestOrSql === "string"
      ? {
          sql: requestOrSql,
          ...(parametersOrOptions === undefined ? {} : { parameters: third as SqlParameters }),
          ...optionValues,
        }
      : { ...requestOrSql, ...third as SqlExecuteOptions };
    validateSqlRequest(request);
    const signal = typeof requestOrSql === "string"
      ? maybeOptions.signal
      : isSqlOptions(third) ? third.signal : maybeOptions.signal;
    return directRequest(this.#http, "databases.executeSql", {
      method: "POST",
      path: `${apiRoot}/databases/${idPathSegment(database)}/sql`,
      body: {
        sql: request.sql,
        ...(request.parameters === undefined ? {} : { parameters: request.parameters }),
        ...(request.explain === undefined ? {} : { explain: request.explain }),
        ...(request.timeout_ms === undefined && request.timeoutMs === undefined
          ? {}
          : { timeout_ms: request.timeout_ms ?? request.timeoutMs }),
        ...(request.cancelled === undefined ? {} : { cancelled: request.cancelled }),
      },
      signal,
    });
  }
}

export class OnboardingClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  getState(): Promise<TerraScaleResult<Models.OnboardingState>> {
    return this.#http.request({
      method: "GET",
      path: sdkRouteManifest.onboarding.state,
      routeName: "onboarding.getState",
    });
  }

  createOrganization(
    request: Models.CreateOnboardingOrganizationRequest,
  ): Promise<TerraScaleResult<Models.OnboardingState>> {
    return this.#http.request({
      method: "POST",
      path: sdkRouteManifest.onboarding.organization,
      routeName: "onboarding.organization.create",
      body: request,
    });
  }

  createProject(
    request: Models.CreateOnboardingProjectRequest,
  ): Promise<TerraScaleResult<Models.OnboardingState>> {
    return this.#http.request({
      method: "POST",
      path: sdkRouteManifest.onboarding.project,
      routeName: "onboarding.project.create",
      body: {
        ...request,
        regions: normalizeRegionCodes(request.regions),
        backupRegion: parseRegionCode(request.backupRegion, "backupRegion"),
      },
    });
  }
}

export class RegionsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  async list(): Promise<TerraScaleResult<Models.RegionsResponse>> {
    const result = await this.#http.request<unknown>({
      method: "GET",
      path: `${apiRoot}/regions`,
      routeName: "regions.list",
    });
    return decodeRegionResult(result, decodeRegionsResponse);
  }

  /** Returns the serving member's health-shaped response. */
  async health(): Promise<TerraScaleResult<Models.HealthResponse>> {
    const result = await this.#http.request<unknown>({
      method: "GET",
      path: `${apiRoot}/health`,
      routeName: "health.health",
    });
    return decodeHealthResult(result, decodeHealthResponse);
  }

}

export class OrganizationsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(query?: Models.PageRequest): Promise<TerraScaleResult<readonly Models.Organization[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations`,
      routeName: "organizations.list",
      query,
    });
  }

  create(
    request: Models.CreateOrganizationRequest,
    options?: Models.OrganizationMutationOptions,
  ): Promise<TerraScaleResult<Models.Organization>> {
    const mutation = options === undefined ? undefined : organizationMutationOptions(options);
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations`,
      routeName: "organizations.create",
      body: request,
      ...(mutation === undefined
        ? {}
        : { idempotencyKey: mutation.idempotencyKey, signal: mutation.signal }),
    });
  }

  get(organizationId: string): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}`,
      routeName: "organizations.get",
    });
  }

  update(
    organizationId: string,
    request: Models.UpdateOrganizationRequest,
    options?: Models.OrganizationMutationOptions,
  ): Promise<TerraScaleResult<Models.Organization>> {
    const mutation = options === undefined ? undefined : organizationMutationOptions(options);
    return this.#http.request({
      method: "PATCH",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}`,
      routeName: "organizations.update",
      body: request,
      ...(mutation === undefined
        ? {}
        : { idempotencyKey: mutation.idempotencyKey, signal: mutation.signal }),
    });
  }

  requestDeletion(
    organizationId: string,
    request: Models.RequestOrganizationDeletionRequest,
    options: Models.OrganizationMutationOptions,
  ): Promise<TerraScaleResult<Models.Organization>> {
    const mutation = organizationMutationOptions(options);
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}:request-deletion`,
      routeName: "organizations.deletion.request",
      body: request,
      idempotencyKey: mutation.idempotencyKey,
      signal: mutation.signal,
    });
  }

  leave(
    organizationId: string,
    options: Models.OrganizationMutationOptions,
  ): Promise<TerraScaleResult<void>> {
    const mutation = organizationMutationOptions(options);
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}:leave`,
      routeName: "organizations.leave",
      idempotencyKey: mutation.idempotencyKey,
      signal: mutation.signal,
    });
  }

  listProjects(
    organizationId: string,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.Project[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/projects`,
      routeName: "organizations.projects.list",
      query,
    });
  }

  listMembers(
    organizationId: string,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.OrganizationMember[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/members`,
      routeName: "organizations.members.list",
      query,
    });
  }

  updateMemberRole(
    organizationId: string,
    userId: string,
    request: Models.UpdateOrganizationMemberRoleRequest,
  ): Promise<TerraScaleResult<Models.OrganizationMember>> {
    return this.#http.request({
      method: "PATCH",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/members/${idPathSegment(userId)}`,
      routeName: "organizations.members.updateRole",
      body: request,
    });
  }

  removeMember(
    organizationId: string,
    userId: string,
    request: Models.RemoveOrganizationMemberRequest = { confirm: true },
  ): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/members/${idPathSegment(userId)}:remove`,
      routeName: "organizations.members.remove",
      body: request,
    });
  }

  createProject(
    organizationId: string,
    request: Models.CreateProjectRequest,
  ): Promise<TerraScaleResult<Models.Project>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/projects`,
      routeName: "organizations.projects.create",
      body: {
        ...request,
        backupRegion: parseRegionCode(request.backupRegion, "backupRegion"),
        regions: normalizeRegionCodes(request.regions),
      },
    });
  }

  getProject(projectId?: string): Promise<TerraScaleResult<Models.Project>> {
    return this.#http.request({
      method: "GET",
      path: projectRoot(this.#http, projectId),
      routeName: "organizations.projects.get",
    });
  }

  listProjectRegionPlacements(
    projectId?: string,
  ): Promise<TerraScaleResult<readonly Models.ProjectRegionPlacement[]>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/regions`,
      routeName: "organizations.projects.regions.list",
    });
  }

  updateProject(
    projectId: string,
    request: Models.UpdateProjectRequest,
  ): Promise<TerraScaleResult<Models.Project>> {
    return this.#http.request({
      method: "PATCH",
      path: projectRoot(this.#http, projectId),
      routeName: "organizations.projects.update",
      body: {
        ...request,
        backupRegion:
          request.backupRegion === undefined
            ? undefined
            : parseRegionCode(request.backupRegion, "backupRegion"),
        regions: request.regions === undefined ? undefined : normalizeRegionCodes(request.regions),
      },
    });
  }

  deleteProject(
    projectId: string,
    request: Models.DeleteProjectRequest,
    options: Models.OrganizationMutationOptions,
  ): Promise<TerraScaleResult<Models.DeletedProject>> {
    const mutation = organizationMutationOptions(options);
    return this.#http.request({
      method: "DELETE",
      path: projectRoot(this.#http, projectId),
      routeName: "organizations.projects.delete",
      body: request,
      idempotencyKey: mutation.idempotencyKey,
      signal: mutation.signal,
    });
  }

  listInvitations(
    organizationId: string,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.Invitation[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/invitations`,
      routeName: "organizations.invitations.list",
      query,
    });
  }

  createInvitation(
    organizationId: string,
    request: Models.CreateInvitationRequest,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/invitations`,
      routeName: "organizations.invitations.create",
      body: request,
    });
  }

  acceptInvitation(
    organizationId: string,
    invitationId: string,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/invitations/${idPathSegment(invitationId)}:accept`,
      routeName: "organizations.invitations.accept",
    });
  }

  declineInvitation(
    organizationId: string,
    invitationId: string,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/invitations/${idPathSegment(invitationId)}:decline`,
      routeName: "organizations.invitations.decline",
    });
  }

  resendInvitation(
    organizationId: string,
    invitationId: string,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/invitations/${idPathSegment(invitationId)}:resend`,
      routeName: "organizations.invitations.resend",
    });
  }

  cancelInvitation(
    organizationId: string,
    invitationId: string,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/invitations/${idPathSegment(invitationId)}:cancel`,
      routeName: "organizations.invitations.cancel",
    });
  }
}

export class SavedQueriesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(
    projectId?: string,
    query?: Models.ProjectSavedQueryPageRequest,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryPageResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/saved-queries`,
      routeName: "savedQueries.list",
      query: {
        ...(query?.cursor === undefined ? {} : { cursor: query.cursor }),
        skip: query?.skip ?? 0,
        take: query?.take ?? 100,
      },
    });
  }

  get(
    savedQueryId: string,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/saved-queries/${idPathSegment(savedQueryId)}`,
      routeName: "savedQueries.get",
    });
  }

  create(
    request: Models.ProjectSavedQueryRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/saved-queries`,
      routeName: "savedQueries.create",
      body: request,
    });
  }

  update(
    savedQueryId: string,
    request: Models.UpdateProjectSavedQueryRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "PUT",
      path: `${projectRoot(this.#http, projectId)}/saved-queries/${idPathSegment(savedQueryId)}`,
      routeName: "savedQueries.update",
      body: request,
    });
  }

  delete(
    savedQueryId: string,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.DeletedProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "DELETE",
      path: `${projectRoot(this.#http, projectId)}/saved-queries/${idPathSegment(savedQueryId)}`,
      routeName: "savedQueries.delete",
    });
  }
}

export class DatabasesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(
    projectId?: string,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.ProjectDatabase[]>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/databases`,
      routeName: "databases.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }

  create(
    request: Models.CreateDatabaseRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/databases`,
      routeName: "databases.create",
      body: request,
    });
  }

  get(databaseId: string, projectId?: string): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.#http.request({
      method: "GET",
      path: databaseRoot(this.#http, { projectId, databaseId }),
      routeName: "databases.get",
    });
  }

  delete(
    databaseId: string,
    options: Models.DeleteProjectDatabaseOptions,
  ): Promise<TerraScaleResult<Models.DeletedProjectDatabaseResponse>> {
    if (options?.confirm !== true) {
      throw new TypeError("Database deletion requires options.confirm to be true.");
    }
    if (
      typeof options.idempotencyKey !== "string" ||
      !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/u.test(options.idempotencyKey)
    ) {
      throw new TypeError("Database deletion requires a stable options.idempotencyKey.");
    }

    return this.#http.request({
      method: "DELETE",
      path: databaseRoot(this.#http, { projectId: options.projectId, databaseId }),
      routeName: "databases.delete",
      headers: { "x-terrascale-delete-confirmation": databaseId },
      idempotencyKey: options.idempotencyKey,
    });
  }
}

export class DatabaseBranchesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  getCapability(
    projectId?: string,
  ): Promise<TerraScaleResult<Models.DatabaseBranchCapabilityResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/database-branches/capability`,
      routeName: "databaseBranches.getCapability",
    });
  }

  requestClone(
    request: Models.CreateDatabaseBranchRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<never>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/database-branches`,
      routeName: "databaseBranches.requestClone",
      body: request,
      idempotencyKey: false,
    });
  }
}

export class BackupsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  configurePolicy(
    databaseId: string,
    request: Models.ConfigureBackupPolicyRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.BackupPolicyResponse>> {
    return this.#http.request({
      method: "PUT",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups/policy`,
      routeName: "backups.policy.configure",
      body: request,
    });
  }

  getPolicy(
    databaseId: string,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.BackupPolicyResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups/policy`,
      routeName: "backups.policy.get",
    });
  }

  planBackup(
    databaseId: string,
    request: Models.DatabaseBackupConfirmationPlanRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.DatabaseBackupConfirmationPlanResponse>> {
    validateBackupConfirmationValue(request?.operationSessionId, "operationSessionId", 128);

    return this.#http.request({
      method: "POST",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups:plan`,
      routeName: "backups.plan",
      body: request,
    });
  }

  executeBackup(
    databaseId: string,
    request: Models.DatabaseBackupConfirmationExecuteRequest,
    idempotencyKey: string,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.BackupJobResponse>> {
    validateBackupConfirmationValue(request?.operationSessionId, "operationSessionId", 128);
    validateBackupConfirmationValue(request?.confirmationToken, "confirmationToken", 8192);
    validateBackupConfirmationValue(idempotencyKey, "idempotencyKey", 128);

    return this.#http.request({
      method: "POST",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups:execute`,
      routeName: "backups.execute",
      body: request,
      idempotencyKey,
    });
  }

  listBackups(
    databaseId: string,
    query?: Models.BackupListQuery,
    projectId?: string,
  ): Promise<TerraScaleResult<readonly Models.BackupJobResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups`,
      routeName: "backups.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }

  getBackup(
    databaseId: string,
    backupId: string,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.BackupJobResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups/${idPathSegment(backupId)}`,
      routeName: "backups.get",
    });
  }

  listRestorePoints(
    databaseId: string,
    projectId?: string,
  ): Promise<TerraScaleResult<readonly Models.RestorePointResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restore-points`,
      routeName: "backups.restorePoints.list",
    });
  }

  startRestore(
    databaseId: string,
    request: Models.StartRestoreRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.RestoreJobResponse>> {
    return this.#http.request({
      method: "POST",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restores`,
      routeName: "backups.restores.start",
      body: request,
    });
  }

  getRestore(
    databaseId: string,
    restoreId: string,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.RestoreJobResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restores/${idPathSegment(restoreId)}`,
      routeName: "backups.restores.get",
    });
  }

  listRestores(
    databaseId: string,
    query?: Models.RestoreListQuery,
    projectId?: string,
  ): Promise<TerraScaleResult<readonly Models.RestoreJobResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restores`,
      routeName: "backups.restores.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }
}

export class AccessClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  listTeamMembers(
    projectId?: string,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.TeamMember>>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/access/team-members`,
      routeName: "access.teamMembers.list",
      query,
    });
  }

  addTeamMember(
    request: Models.AddTeamMemberRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.TeamMember>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/access/team-members`,
      routeName: "access.teamMembers.add",
      body: request,
    });
  }

  updateTeamMember(
    userId: string,
    request: Models.UpdateTeamMemberRequest,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.TeamMember>> {
    return this.#http.request({
      method: "PATCH",
      path: `${projectRoot(this.#http, projectId)}/access/team-members/${idPathSegment(userId)}`,
      routeName: "access.teamMembers.update",
      body: request,
    });
  }

  removeTeamMember(userId: string, projectId?: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `${projectRoot(this.#http, projectId)}/access/team-members/${idPathSegment(userId)}`,
      routeName: "access.teamMembers.remove",
    });
  }
}

export class UsageClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  getProjectUsage(projectId?: string): Promise<TerraScaleResult<Models.UsageSummary>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/usage`,
      routeName: "usage.project.get",
    });
  }

  getOrganizationUsage(
    organizationId: string,
  ): Promise<TerraScaleResult<Models.OrganizationUsage>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}/usage`,
      routeName: "usage.organization.get",
    });
  }

  getProjectUsageWorkload(
    range: Models.UsageWorkloadRange,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.UsageWorkload>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/usage/workload`,
      routeName: "usage.project.workload",
      query: { range },
    });
  }

  getProjectLatency(
    projectId?: string,
    query?: Models.ProjectLatencyRequest,
  ): Promise<TerraScaleResult<Models.ProjectLatency>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/latency`,
      routeName: "usage.project.latency",
      query,
    });
  }

  getProjectUsageHistory(
    range: Models.UsageHistoryRange,
    projectId?: string,
  ): Promise<TerraScaleResult<Models.ProjectUsageHistory>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/usage/history`,
      routeName: "usage.project.history",
      query: { range },
    });
  }
}

export class AuditClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(
    projectId?: string,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.AuditEvent>>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/audit`,
      routeName: "audit.list",
      query,
    });
  }

  get(auditEventId: string, projectId?: string): Promise<TerraScaleResult<Models.AuditEvent>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/audit/${idPathSegment(auditEventId)}`,
      routeName: "audit.get",
    });
  }
}

export class NotificationsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(
    query?: Models.PageRequest & {
      readonly unreadOnly?: boolean;
      readonly projectId?: string;
      readonly organizationId?: string;
      readonly includeOrganizationNotifications?: boolean;
    },
  ): Promise<TerraScaleResult<readonly Models.Notification[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/users/me/notifications`,
      routeName: "notifications.list",
      query,
    });
  }

  markRead(notificationId: string): Promise<TerraScaleResult<Models.Notification>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/users/me/notifications/${idPathSegment(notificationId)}:mark-read`,
      routeName: "notifications.markRead",
    });
  }

  markAllRead(query?: {
    readonly organizationId?: string;
  }): Promise<TerraScaleResult<Models.MarkAllNotificationsReadResponse>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/users/me/notifications:mark-all-read`,
      routeName: "notifications.markAllRead",
      query,
    });
  }
}

export class AdminClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  overview(): Promise<TerraScaleResult<Models.AdminPlatformUsage>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin`,
      routeName: "admin.overview",
    });
  }

  listUsers(
    query?: Models.PageRequest & { readonly search?: string },
  ): Promise<TerraScaleResult<Models.Page<Models.AdminUser>>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/users`,
      routeName: "admin.users.list",
      query,
    });
  }

  getUser(userId: string): Promise<TerraScaleResult<Models.AdminUser>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/users/${idPathSegment(userId)}`,
      routeName: "admin.users.get",
    });
  }

  setUserEnabled(userId: string, enabled: boolean): Promise<TerraScaleResult<Models.AdminUser>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/admin/users/${idPathSegment(userId)}:${enabled ? "enable" : "disable"}`,
      routeName: enabled ? "admin.users.enable" : "admin.users.disable",
    });
  }

  deleteUser(userId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `${apiRoot}/admin/users/${idPathSegment(userId)}`,
      routeName: "admin.users.delete",
    });
  }

  createSupportNote(
    request: Models.AdminCreateSupportNoteRequest,
  ): Promise<TerraScaleResult<Models.AdminSupportNote>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/admin/support-notes`,
      routeName: "admin.supportNotes.create",
      body: request,
    });
  }

  listOrganizations(
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Organization>>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/organizations`,
      routeName: "admin.organizations.list",
      query,
    });
  }

  listProjects(query?: Models.PageRequest): Promise<TerraScaleResult<Models.Page<Models.Project>>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/projects`,
      routeName: "admin.projects.list",
      query,
    });
  }

  listDatabases(
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.ProjectDatabase>>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/databases`,
      routeName: "admin.databases.list",
      query,
    });
  }

  getUsage(): Promise<TerraScaleResult<Models.AdminPlatformUsage>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/usage`,
      routeName: "admin.usage.get",
    });
  }

  listAudit(query?: Models.PageRequest): Promise<TerraScaleResult<Models.Page<Models.AuditEvent>>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/audit`,
      routeName: "admin.audit.list",
      query,
    });
  }

  listNotifications(
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Notification>>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/notifications`,
      routeName: "admin.notifications.list",
      query,
    });
  }
}

function validateBackupConfirmationValue(
  value: unknown,
  name: string,
  maximumLength: number,
): asserts value is string {
  if (typeof value !== "string" || value.trim().length === 0 || value.length > maximumLength) {
    throw new TypeError(`${name} must be nonblank and contain at most ${maximumLength} characters.`);
  }
}

function organizationMutationOptions(
  options: Models.OrganizationMutationOptions,
): Models.OrganizationMutationOptions {
  if (options === null || typeof options !== "object") {
    throw new TypeError("organization mutation options must be an object.");
  }
  if (typeof options.idempotencyKey !== "string" || options.idempotencyKey.trim().length === 0 || options.idempotencyKey.length > 128) {
    throw new TypeError("idempotencyKey must be nonblank and contain at most 128 characters.");
  }
  if (options.idempotencyKey !== options.idempotencyKey.trim()) {
    throw new TypeError("idempotencyKey cannot have leading or trailing whitespace.");
  }
  return options;
}

type DirectRequestOptions = {
  readonly method: "DELETE" | "GET" | "PATCH" | "POST" | "PUT";
  readonly path: string;
  readonly query?: object | undefined;
  readonly body?: unknown;
  readonly headers?: HeadersInit;
  readonly idempotencyKey?: string | false | undefined;
  readonly signal?: AbortSignal | undefined;
};

function documentsPath(database: string, collection: string): string {
  requireDatabaseIdentifier(database);
  requireDocumentIdentifier(collection, "collection");
  return `${apiRoot}/databases/${idPathSegment(database)}/collections/${idPathSegment(collection)}/documents`;
}

function documentPath(database: string, collection: string, id: string): string {
  return `${documentsPath(database, collection)}/${idPathSegment(requireDocumentIdentifier(id, "document identifier"))}`;
}

function requireDatabaseIdentifier(value: string): string {
  return requireDocumentIdentifier(value, "database");
}

function requireDocumentIdentifier(value: string, name: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw new TypeError(`${name} cannot be blank.`);
  }
  if (value !== value.trim()) {
    throw new TypeError(`${name} cannot have leading or trailing whitespace.`);
  }
  if ([...value].some((character) => character.charCodeAt(0) < 0x20 || character.charCodeAt(0) === 0x7f)) {
    throw new TypeError(`${name} cannot contain control characters.`);
  }
  if (new TextEncoder().encode(value).byteLength > 255) {
    throw new TypeError(`${name} cannot exceed 255 UTF-8 bytes.`);
  }
  return value;
}

function validateDocumentListOptions(options: DocumentListOptions): void {
  if (options === null || typeof options !== "object") throw new TypeError("operation options must be an object.");
  if (options.limit !== undefined &&
    (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 1_000)) {
    throw new TypeError("limit must be an integer between 1 and 1000.");
  }
  if (options.after !== undefined) requireDocumentIdentifier(options.after, "after");
}

function validateDocumentBatchRequest(request: DocumentBatchRequest): void {
  if (request === null || typeof request !== "object" || !Array.isArray(request.operations) ||
    request.operations.length < 1 || request.operations.length > 1_000) {
    throw new TypeError("operations must contain between 1 and 1000 items.");
  }
  for (const [index, operation] of request.operations.entries()) {
    if (operation === null || typeof operation !== "object") {
      throw new TypeError(`operations[${index}] must be an object.`);
    }
    requireDocumentIdentifier(operation.id, `operations[${index}].id`);
    if (operation.op !== "put" && operation.op !== "patch" && operation.op !== "delete") {
      throw new TypeError(`operations[${index}].op must be put, patch, or delete.`);
    }
    if (operation.op === "delete") {
      if (operation.value !== undefined) throw new TypeError(`operations[${index}].value is not allowed for delete.`);
    } else if (operation.op === "put") {
      if (operation.value === undefined) throw new TypeError(`operations[${index}].value is required for put.`);
      validateJsonPayload(operation.value, `operations[${index}].value`);
    } else if (operation.value !== undefined) {
      validateJsonPayload(operation.value, `operations[${index}].value`);
    }
  }
}

function validateDocumentReadOptions(options: DocumentReadOptions): void {
  if (options === null || typeof options !== "object") throw new TypeError("operation options must be an object.");
  if (options.at !== undefined &&
    (!Number.isSafeInteger(options.at) || options.at < 0)) {
    throw new TypeError("at must be a non-negative safe integer of Unix epoch milliseconds.");
  }
  if (options.ifNoneMatch !== undefined &&
    (typeof options.ifNoneMatch !== "string" || options.ifNoneMatch.trim().length === 0 || options.ifNoneMatch.length > 1024)) {
    throw new TypeError("ifNoneMatch must be a nonblank string of at most 1024 characters.");
  }
  if (options.session !== undefined && !isOpaqueSessionToken(options.session)) {
    throw new TypeError("session must be a canonical unpadded base64url token of at most 16384 characters.");
  }
}

function isOpaqueSessionToken(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 16_384 || value.length % 4 === 1) {
    return false;
  }
  return /^[A-Za-z0-9_-]+$/u.test(value);
}

function validateDocumentOperationOptions(options: DocumentOperationOptions, allowCreateOnly = false): void {
  if (options === null || typeof options !== "object") {
    throw new TypeError("operation options must be an object.");
  }
  if (options.ifNoneMatch !== undefined && !allowCreateOnly) {
    throw new TypeError("write ifNoneMatch is supported only for document PUT.");
  }
  if (options.ifNoneMatch !== undefined && options.ifNoneMatch !== "*") {
    throw new TypeError('write ifNoneMatch must be "*".');
  }
  if (options.ifNoneMatch !== undefined && options.ifMatch !== undefined) {
    throw new TypeError("ifNoneMatch and ifMatch cannot be combined.");
  }
  if (options.ifMatch !== undefined &&
    (typeof options.ifMatch !== "string" || options.ifMatch.trim().length === 0 || options.ifMatch.length > 1024)) {
    throw new TypeError("ifMatch must be a nonblank string of at most 1024 characters.");
  }
  if (options.durability !== undefined &&
    options.durability !== "regional" && options.durability !== "global") {
    throw new TypeError('durability must be either "regional" or "global".');
  }
}

function documentRequestOptions(options: DocumentOperationOptions): {
  readonly headers?: HeadersInit;
  readonly idempotencyKey?: string | false | undefined;
  readonly signal?: AbortSignal | undefined;
} {
  return {
    ...(options.ifMatch === undefined && options.ifNoneMatch === undefined && options.durability === undefined ? {} : {
      headers: {
        ...(options.ifMatch === undefined ? {} : { "if-match": options.ifMatch }),
        ...(options.ifNoneMatch === undefined ? {} : { "if-none-match": options.ifNoneMatch }),
        ...(options.durability === undefined ? {} : { "x-terrascale-durability": options.durability }),
      },
    }),
    ...(options.idempotencyKey === undefined ? {} : { idempotencyKey: options.idempotencyKey }),
    ...(options.signal === undefined ? {} : { signal: options.signal }),
  };
}

function documentReadRequestOptions(options: DocumentReadOptions): {
  readonly headers?: HeadersInit;
  readonly signal?: AbortSignal | undefined;
} {
  return {
    ...(options.ifNoneMatch === undefined && options.session === undefined ? {} : {
      headers: {
        ...(options.ifNoneMatch === undefined ? {} : { "if-none-match": options.ifNoneMatch }),
        ...(options.session === undefined ? {} : { "x-terrascale-session": options.session }),
      },
    }),
    ...(options.signal === undefined ? {} : { signal: options.signal }),
  };
}

function validateJsonPayload(value: unknown, name: string, rejectUnsafeIntegers = false): void {
  validateJsonValue(value, name, new WeakSet<object>(), rejectUnsafeIntegers);
  let encoded: string | undefined;
  try {
    encoded = JSON.stringify(value) as string | undefined;
  } catch (error) {
    throw new TypeError(
      `${name} must be JSON serializable${error instanceof Error ? `: ${error.message}` : "."}`,
    );
  }
  if (encoded === undefined) throw new TypeError(`${name} must be a JSON value.`);
  if (new TextEncoder().encode(encoded).byteLength > 1_048_576) {
    throw new TypeError(`${name} exceeds the 1 MiB document limit.`);
  }
}

function validateJsonValue(value: unknown, path: string, seen: WeakSet<object>, rejectUnsafeIntegers = false): void {
  if (value === null || typeof value === "string" || typeof value === "boolean") return;
  if (typeof value === "number") {
    if (Number.isFinite(value)) {
      if (rejectUnsafeIntegers && Number.isInteger(value) && !Number.isSafeInteger(value)) {
        throw new TypeError(`${path} must not contain unsafe integer numbers.`);
      }
      return;
    }
    throw new TypeError(`${path} must contain only finite numbers.`);
  }
  if (typeof value !== "object") throw new TypeError(`${path} must be a JSON value.`);
  if (seen.has(value)) throw new TypeError(`${path} must not contain cyclic references.`);
  seen.add(value);
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) {
      if (!Object.hasOwn(value, index)) throw new TypeError(`${path}[${index}] must be defined.`);
      validateJsonValue(value[index], `${path}[${index}]`, seen, rejectUnsafeIntegers);
    }
  } else {
    if (typeof (value as { readonly toJSON?: unknown }).toJSON === "function") {
      throw new TypeError(`${path} must not define toJSON.`);
    }
    for (const [key, item] of Object.entries(value)) {
      validateJsonValue(item, `${path}.${key}`, seen, rejectUnsafeIntegers);
    }
  }
  seen.delete(value);
}

function validateSqlRequest(request: SqlExecuteRequest): void {
  if (typeof request.sql !== "string" || request.sql.trim().length === 0) {
    throw new TypeError("sql must be a nonblank string.");
  }
  if (new TextEncoder().encode(request.sql).byteLength > 1_048_576) {
    throw new TypeError("sql exceeds the 1 MiB query limit.");
  }
  if (request.parameters !== undefined) {
    if (typeof request.parameters !== "object" || request.parameters === null || Array.isArray(request.parameters)) {
      throw new TypeError("parameters must be an object.");
    }
    validateJsonPayload(request.parameters, "parameters", true);
  }
  if (request.explain !== undefined && typeof request.explain !== "boolean") {
    throw new TypeError("explain must be a boolean.");
  }
  if (request.cancelled !== undefined && typeof request.cancelled !== "boolean") {
    throw new TypeError("cancelled must be a boolean.");
  }
  const timeout = request.timeout_ms ?? request.timeoutMs;
  if (timeout !== undefined &&
    (!Number.isInteger(timeout) || timeout < 1 || timeout > 30_000)) {
    throw new TypeError("timeout_ms must be an integer between 1 and 30000.");
  }
}

function isSqlOptions(value: SqlParameters | SqlExecuteOptions): value is SqlExecuteOptions {
  return typeof value === "object" && value !== null && (
          Object.hasOwn(value, "signal") ||
          Object.hasOwn(value, "explain") ||
          Object.hasOwn(value, "timeout_ms") ||
          Object.hasOwn(value, "timeoutMs") ||
          Object.hasOwn(value, "cancelled")
  );
}

async function directRequest<TResponse>(
  http: TerraScaleHttp,
  routeName: string,
  options: DirectRequestOptions,
): Promise<TerraScaleResult<TResponse>> {
  const signal = options.signal;
  if (signal?.aborted === true) return cancelledResult(routeName);
  const request = {
    method: options.method,
    path: options.path,
    routeName: routeName as TerraScaleRouteName,
    query: options.query,
    body: options.body,
    headers: options.headers,
    idempotencyKey: options.idempotencyKey,
    signal,
  } satisfies Parameters<TerraScaleHttp["request"]>[0];
  const pending = http.request<TResponse>(request);
  if (signal === undefined) return pending;
  return new Promise<TerraScaleResult<TResponse>>((resolve, reject) => {
    const onAbort = (): void => {
      cleanup();
      resolve(cancelledResult(routeName));
    };
    const cleanup = (): void => signal.removeEventListener("abort", onAbort);
    signal.addEventListener("abort", onAbort, { once: true });
    pending.then(
      (result) => {
        cleanup();
        resolve(isCancelledResult(result) && signal.aborted ? cancelledResult(routeName) : result);
      },
      (error: unknown) => {
        cleanup();
        if (signal.aborted && isAbortError(error)) {
          resolve(cancelledResult(routeName));
        } else {
          reject(error);
        }
      },
    );
  });
}

function cancelledResult(routeName: string): TerraScaleFailure {
  return err(routeName, {
    kind: "transport",
    code: "request_cancelled",
    message: "The TerraScale request was cancelled.",
  });
}

function isCancelledResult<T>(result: TerraScaleResult<T>): boolean {
  return !result.ok && (
    result.error.code === "request_cancelled" ||
    result.error.code === "aborted" ||
    isAbortError(result.error.details)
  );
}

function isAbortError(value: unknown): boolean {
  return value instanceof Error && value.name === "AbortError";
}

function projectRootId(http: TerraScaleHttp, projectId: string | undefined): string {
  const selectedProjectId = projectId ?? http.config.projectId;

  if (selectedProjectId === undefined) {
    throw new TypeError(
      "A projectId must be supplied either in the request or TerraScaleClient runtime config.",
    );
  }

  return selectedProjectId;
}

function projectRoot(http: TerraScaleHttp, projectId: string | undefined): string {
  return `${apiRoot}/projects/${idPathSegment(projectRootId(http, projectId))}`;
}

function databaseRoot(http: TerraScaleHttp, scope: Models.DatabaseScoped): string {
  return `${projectRoot(http, scope.projectId)}/databases/${idPathSegment(scope.databaseId)}`;
}

function idPathSegment(value: string): string {
  return encodeURIComponent(value);
}

function decodeRegionResult<TValue>(
  result: TerraScaleResult<unknown>,
  decode: (value: unknown) => TValue,
): TerraScaleResult<TValue> {
  if (!result.ok) {
    return result;
  }

  try {
    return { ...result, value: decode(result.value) };
  } catch (error) {
    return invalidRegionResponse(result, error);
  }
}

function invalidRegionResponse<TValue>(
  result: TerraScaleSuccess<unknown>,
  error: unknown,
): TerraScaleResult<TValue> {
  const requestId = result.headers.get("x-request-id") ?? undefined;
  return {
    ok: false,
    routeName: result.routeName,
    status: result.status,
    headers: result.headers,
    error: {
      kind: "invalid-response",
      code: "invalid_response",
      message: error instanceof Error ? error.message : "The Region response is invalid.",
      status: result.status,
      ...(requestId === undefined ? {} : { requestId }),
    },
    body: result.value,
    ...(result.routing === undefined ? {} : { routing: result.routing }),
  };
}

function decodeRegionsResponse(value: unknown): Models.RegionsResponse {
  const response = requireRecord(value, "response");
  if (!Array.isArray(response.regions)) {
    throw new TypeError("response.regions must be an array.");
  }
  if (response.regions.length < 1) {
    throw new TypeError("response.regions must contain at least 1 region.");
  }
  if (response.regions.length > 20) {
    throw new TypeError("response.regions must contain at most 20 regions.");
  }

  const regions = response.regions.map((region, index) =>
    decodeRegionSummary(region, `response.regions[${index}]`));
  const uniqueCodes = new Set(regions.map((region) => region.code));
  if (uniqueCodes.size !== regions.length) {
    throw new TypeError("response.regions must not contain duplicate RegionCode values.");
  }

  const hosts = requireRecord(response.hosts, "response.hosts");
  const decodedHosts: Record<string, string> = {};
  for (const [code, host] of Object.entries(hosts)) {
    decodedHosts[parseRegionCode(code, `response.hosts.${code}`)] = requireString(
      host,
      `response.hosts.${code}`,
    );
  }
  for (const region of regions) {
    if (!Object.hasOwn(decodedHosts, region.code)) {
      throw new TypeError(`response.hosts.${region.code} is required for every response region.`);
    }
  }
  return { regions, hosts: decodedHosts };
}

function decodeHealthResponse(value: unknown): Models.HealthResponse {
  const response = requireRecord(value, "response");
  const region = parseRegionCode(response.region, "response.region");
  if (typeof response.healthy !== "boolean") {
    throw new TypeError("response.healthy must be a boolean.");
  }
  const version = requireString(response.version, "response.version");
  if (version.length === 0) {
    throw new TypeError("response.version must not be empty.");
  }
  return { region, healthy: response.healthy, version };
}

function decodeHealthResult(
  result: TerraScaleResult<unknown>,
  decoder: (value: unknown) => Models.HealthResponse,
): TerraScaleResult<Models.HealthResponse> {
  if (!result.ok) return result;
  try {
    return { ...result, value: decoder(result.value) };
  } catch (error) {
    return invalidRegionResponse(result, error);
  }
}

function decodeRegionSummary(value: unknown, path: string): Models.RegionSummary {
  const region = requireRecord(value, path);
  for (const field of ["ociRegionCode", "clusterNodeTag"] as const) {
    if (Object.hasOwn(region, field)) {
      throw new TypeError(`${path} contains forbidden provider field ${field}.`);
    }
  }

  const code = parseRegionCode(region.code, `${path}.code`);
  const name = requireString(region.name, `${path}.name`);
  if ([...name].length < 1 || [...name].length > 128) {
    throw new TypeError(`${path}.name must contain between 1 and 128 characters.`);
  }
  const country = requireString(region.country, `${path}.country`);
  if (!/^[A-Z]{2}$/u.test(country)) {
    throw new TypeError(`${path}.country must contain exactly two uppercase letters.`);
  }
  const latitude = requireBoundedNumber(region.latitude, `${path}.latitude`, -90, 90);
  const longitude = requireBoundedNumber(region.longitude, `${path}.longitude`, -180, 180);
  if (typeof region.available !== "boolean") {
    throw new TypeError(`${path}.available must be a boolean.`);
  }

  return { code, name, country, latitude, longitude, available: region.available };
}

function requireRecord(value: unknown, path: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new TypeError(`${path} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function requireString(value: unknown, path: string): string {
  if (typeof value !== "string") {
    throw new TypeError(`${path} must be a string.`);
  }
  return value;
}

function requireFiniteNumber(value: unknown, path: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new TypeError(`${path} must be a finite number.`);
  }
  return value;
}

function requireBoundedNumber(value: unknown, path: string, minimum: number, maximum: number): number {
  const number = requireFiniteNumber(value, path);
  if (number < minimum || number > maximum) {
    throw new TypeError(`${path} must be between ${minimum} and ${maximum}.`);
  }
  return number;
}
