import type {
  DashboardLogicalDatabaseKey,
  ResolvedTerraScaleRuntimeConfig,
  TerraScaleRuntimeConfig,
} from "./config.js";
import { TerraScaleHttp, type QueryRecord, type TerraScaleRequestOptions } from "./http.js";
import { type Ulid, ulidPathSegment } from "./ids.js";
import type * as Models from "./models.js";
import { normalizeRegionCodes, type TerraScaleRegion, type TerraScaleRegionCode } from "./regions.js";
import type { TerraScaleResult } from "./result.js";

const apiRoot = "/api/v1";

export const sdkRouteManifest = {
  auth: {
    me: `${apiRoot}/auth/me`,
    billing: `${apiRoot}/auth/me/billing`,
    billingGuard: `${apiRoot}/auth/me/billing/guard`,
    register: `${apiRoot}/auth/password/signup`,
    verifyEmail: `${apiRoot}/auth/password/verify-email`,
    resendVerification: `${apiRoot}/auth/password/resend-verification`,
    signIn: `${apiRoot}/auth/password/login`,
    forgotPassword: `${apiRoot}/auth/password/forgot-password`,
    resetPassword: `${apiRoot}/auth/password/reset-password`,
    magicLinkRequest: `${apiRoot}/auth/passwordless/magic-link`,
    magicLinkComplete: `${apiRoot}/auth/passwordless/magic-link/complete`,
    refresh: `${apiRoot}/auth/refresh`,
    signOut: `${apiRoot}/auth/logout`,
    accountSessions: `${apiRoot}/account/sessions`,
    accountSession: (sessionId: Ulid) =>
      `${apiRoot}/account/sessions/${ulidPathSegment(sessionId)}`,
    project: {
      register: (projectId: Ulid) => `${projectAuthRoot(projectId)}/signup`,
      verifyEmail: (projectId: Ulid) => `${projectAuthRoot(projectId)}/verify-email`,
      resendVerification: (projectId: Ulid) => `${projectAuthRoot(projectId)}/resend-verification`,
      signIn: (projectId: Ulid) => `${projectAuthRoot(projectId)}/login`,
      forgotPassword: (projectId: Ulid) => `${projectAuthRoot(projectId)}/forgot-password`,
      resetPassword: (projectId: Ulid) => `${projectAuthRoot(projectId)}/reset-password`,
      magicLinkRequest: (projectId: Ulid) =>
        `${projectAuthRoot(projectId)}/passwordless/magic-link`,
      magicLinkComplete: (projectId: Ulid) =>
        `${projectAuthRoot(projectId)}/passwordless/magic-link/complete`,
      refresh: (projectId: Ulid) => `${projectAuthRoot(projectId)}/refresh`,
      signOut: (projectId: Ulid) => `${projectAuthRoot(projectId)}/logout`,
    },
  },
} as const;

export class TerraScaleClient {
  private readonly http: TerraScaleHttp;

  readonly auth: AuthClient;
  readonly organizations: OrganizationsClient;
  readonly databases: DatabasesClient;
  readonly repositories: RepositoriesClient;
  readonly documents: DocumentsClient;
  readonly query: QueryClient;
  readonly schema: SchemaClient;
  readonly savedQueries: SavedQueriesClient;
  readonly history: QueryHistoryClient;
  readonly apiKeys: ApiKeysClient;
  readonly machineClients: MachineClientsClient;
  readonly access: AccessClient;
  readonly usage: UsageClient;
  readonly billing: BillingClient;
  readonly pricing: PricingClient;
  readonly audit: AuditClient;
  readonly notifications: NotificationsClient;
  readonly settings: SettingsClient;
  readonly security: SecurityClient;
  readonly admin: AdminClient;
  readonly dashboard: DashboardClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.http = new TerraScaleHttp(config);
    this.auth = new AuthClient(this.http);
    this.organizations = new OrganizationsClient(this.http);
    this.databases = new DatabasesClient(this.http);
    this.repositories = new RepositoriesClient(this.http);
    this.documents = new DocumentsClient(this.http);
    this.query = new QueryClient(this.http);
    this.schema = new SchemaClient(this.http);
    this.savedQueries = new SavedQueriesClient(this.http);
    this.history = new QueryHistoryClient(this.http);
    this.apiKeys = new ApiKeysClient(this.http);
    this.machineClients = new MachineClientsClient(this.http);
    this.access = new AccessClient(this.http);
    this.usage = new UsageClient(this.http);
    this.billing = new BillingClient(this.http);
    this.pricing = new PricingClient(this.http);
    this.audit = new AuditClient(this.http);
    this.notifications = new NotificationsClient(this.http);
    this.settings = new SettingsClient(this.http);
    this.security = new SecurityClient(this.http);
    this.admin = new AdminClient(this.http);
    this.dashboard = new DashboardClient(this.http);
  }

  get config(): ResolvedTerraScaleRuntimeConfig {
    return this.http.config;
  }

  request<TResponse, TBody = unknown>(
    options: TerraScaleRequestOptions<TBody>,
  ): Promise<TerraScaleResult<TResponse>> {
    return this.http.request<TResponse, TBody>(options);
  }

  dashboardDatabaseId(name: DashboardLogicalDatabaseKey | string): Ulid | undefined {
    return this.http.config.dashboardDatabaseIds[name];
  }
}

export class DashboardClient {
  constructor(private readonly http: TerraScaleHttp) {}

  getContext(): Promise<TerraScaleResult<Models.DashboardContext>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/dashboard/context`,
      routeName: "dashboard.context.get",
    });
  }

  switchProject(projectId: Ulid): Promise<TerraScaleResult<Models.DashboardContext>> {
    return this.http.request({
      method: "PUT",
      path: `${apiRoot}/dashboard/context/project`,
      routeName: "dashboard.context.switchProject",
      body: { projectId },
    });
  }
}

export class AuthClient {
  constructor(private readonly http: TerraScaleHttp) {}

  getSession(): Promise<TerraScaleResult<Models.AuthMeResponse>> {
    return this.http.request({
      base: "auth",
      method: "GET",
      path: sdkRouteManifest.auth.me,
      routeName: "auth.session.get",
    });
  }

  getCurrentBilling(): Promise<TerraScaleResult<Models.CurrentOrganizationBilling>> {
    return this.http.request({
      base: "auth",
      method: "GET",
      path: sdkRouteManifest.auth.billing,
      routeName: "auth.billing.get",
    });
  }

  evaluateBillingGuard(
    request: Models.BillingGuardRequest,
  ): Promise<TerraScaleResult<Models.BillingGuardResponse>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: sdkRouteManifest.auth.billingGuard,
      routeName: "auth.billing.guard",
      body: request,
    });
  }

  register(
    request: Models.RegisterRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.SignupResponse>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.register,
        sdkRouteManifest.auth.project.register,
      ),
      routeName: "auth.register",
      body: toSignupRequest(request),
    });
  }

  verifyEmail(
    request: Models.VerifyEmailRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.SignupResponse>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.verifyEmail,
        sdkRouteManifest.auth.project.verifyEmail,
      ),
      routeName: "auth.verifyEmail",
      body: request,
    });
  }

  resendVerification(
    pendingAuthenticationToken: string,
    verifyUrl = "",
    projectId?: Ulid,
  ): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.resendVerification,
        sdkRouteManifest.auth.project.resendVerification,
      ),
      routeName: "auth.resendVerification",
      body: { pendingAuthenticationToken, verifyUrl },
    });
  }

  signIn(
    request: Models.SignInRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.PasswordLoginResponse>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.signIn,
        sdkRouteManifest.auth.project.signIn,
      ),
      routeName: "auth.signIn",
      body: {
        email: request.email,
        password: request.password,
        deviceFingerprint: request.deviceFingerprint ?? "",
      },
    });
  }

  forgotPassword(
    request: Models.PasswordResetRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.forgotPassword,
        sdkRouteManifest.auth.project.forgotPassword,
      ),
      routeName: "auth.forgotPassword",
      body: { email: request.email, resetUrl: request.resetUrl ?? request.redirectUrl ?? "" },
    });
  }

  resetPassword(
    request: Models.CompletePasswordResetRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.resetPassword,
        sdkRouteManifest.auth.project.resetPassword,
      ),
      routeName: "auth.resetPassword",
      body: { token: request.token, newPassword: request.newPassword ?? request.password },
    });
  }

  requestMagicLink(
    request: Models.MagicLinkRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.magicLinkRequest,
        sdkRouteManifest.auth.project.magicLinkRequest,
      ),
      routeName: "auth.magicLink.request",
      body: { email: request.email, loginUrl: request.loginUrl ?? request.redirectUrl },
    });
  }

  completeMagicLink(
    request: Models.CompleteMagicLinkRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.AuthTokenResponse>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.magicLinkComplete,
        sdkRouteManifest.auth.project.magicLinkComplete,
      ),
      routeName: "auth.magicLink.complete",
      body: request,
    });
  }

  refresh(
    refreshToken: string,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.AuthTokenResponse>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.refresh,
        sdkRouteManifest.auth.project.refresh,
      ),
      routeName: "auth.token.refresh",
      body: { refreshToken },
    });
  }

  signOut(projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "POST",
      path: authRoute(
        this.http,
        projectId,
        sdkRouteManifest.auth.signOut,
        sdkRouteManifest.auth.project.signOut,
      ),
      routeName: "auth.signOut",
      body: { revokeAll: true },
    });
  }

  listSessions(query?: {
    readonly cursor?: string;
    readonly limit?: number;
  }): Promise<TerraScaleResult<Models.ListAccountSessionsResponse>> {
    return this.http.request({
      base: "auth",
      method: "GET",
      path: sdkRouteManifest.auth.accountSessions,
      routeName: "auth.sessions.list",
      query,
    });
  }

  revokeSession(sessionId: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "DELETE",
      path: sdkRouteManifest.auth.accountSession(sessionId),
      routeName: "auth.sessions.revoke",
    });
  }

  revokeOtherSessions(): Promise<TerraScaleResult<void>> {
    return this.http.request({
      base: "auth",
      method: "DELETE",
      path: sdkRouteManifest.auth.accountSessions,
      routeName: "auth.sessions.revokeOther",
    });
  }
}

export class OrganizationsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(query?: Models.PageRequest): Promise<TerraScaleResult<Models.Page<Models.Organization>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations`,
      routeName: "organizations.list",
      query,
    });
  }

  create(
    request: Models.CreateOrganizationRequest,
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/organizations`,
      routeName: "organizations.create",
      body: request,
    });
  }

  get(organizationId: Ulid): Promise<TerraScaleResult<Models.Organization>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}`,
      routeName: "organizations.get",
    });
  }

  update(
    organizationId: Ulid,
    request: Models.UpdateOrganizationRequest,
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.http.request({
      method: "PATCH",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}`,
      routeName: "organizations.update",
      body: request,
    });
  }

  listProjects(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Project>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/projects`,
      routeName: "organizations.projects.list",
      query,
    });
  }

  listUsers(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.OrganizationUser>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/users`,
      routeName: "organizations.users.list",
      query,
    });
  }

  createProject(
    organizationId: Ulid,
    request: Models.CreateProjectRequest,
  ): Promise<TerraScaleResult<Models.Project>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/projects`,
      routeName: "organizations.projects.create",
      body: request,
    });
  }

  getProject(projectId?: Ulid): Promise<TerraScaleResult<Models.Project>> {
    return this.http.request({
      method: "GET",
      path: projectRoot(this.http, projectId),
      routeName: "organizations.projects.get",
    });
  }

  updateProject(
    projectId: Ulid,
    request: Models.UpdateProjectRequest,
  ): Promise<TerraScaleResult<Models.Project>> {
    return this.http.request({
      method: "PATCH",
      path: projectRoot(this.http, projectId),
      routeName: "organizations.projects.update",
      body: request,
    });
  }

  switchProject(projectId: Ulid): Promise<TerraScaleResult<Models.ProjectSwitchResponse>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}:switch`,
      routeName: "organizations.projects.switch",
    });
  }

  listInvitations(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Invitation>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations`,
      routeName: "organizations.invitations.list",
      query,
    });
  }

  createInvitation(
    organizationId: Ulid,
    request: Models.CreateInvitationRequest,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations`,
      routeName: "organizations.invitations.create",
      body: request,
    });
  }

  acceptInvitation(invitationId: Ulid): Promise<TerraScaleResult<Models.Invitation>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/invitations/${ulidPathSegment(invitationId)}:accept`,
      routeName: "organizations.invitations.accept",
    });
  }

  revokeInvitation(
    organizationId: Ulid,
    invitationId: Ulid,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations/${ulidPathSegment(invitationId)}:revoke`,
      routeName: "organizations.invitations.revoke",
    });
  }
}

export class DatabasesClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.ProjectDatabase>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/databases`,
      routeName: "databases.list",
      query,
    });
  }

  create(
    request: Models.CreateDatabaseRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/databases`,
      routeName: "databases.create",
      body: {
        ...request,
        regions: normalizeRegionCodes(request.regions),
      },
    });
  }

  get(databaseId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.http.request({
      method: "GET",
      path: databaseRoot(this.http, { projectId, databaseId }),
      routeName: "databases.get",
    });
  }

  update(
    databaseId: Ulid,
    request: Models.UpdateDatabaseRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.http.request({
      method: "PATCH",
      path: databaseRoot(this.http, { projectId, databaseId }),
      routeName: "databases.update",
      body: normalizeDatabaseUpdate(request),
    });
  }

  delete(databaseId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: databaseRoot(this.http, { projectId, databaseId }),
      routeName: "databases.delete",
    });
  }

  listRegions(): Promise<TerraScaleResult<readonly TerraScaleRegion[]>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/regions`,
      routeName: "databases.regions.list",
    });
  }

  setRegions(
    databaseId: Ulid,
    regions: readonly TerraScaleRegionCode[],
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.http.request({
      method: "PUT",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/regions`,
      routeName: "databases.regions.set",
      body: { regions: normalizeRegionCodes(regions) },
    });
  }

  listEnvironments(
    databaseId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.DatabaseEnvironment[]>> {
    return this.http.request({
      method: "GET",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/environments`,
      routeName: "databases.environments.list",
      query: { skip: 0, take: 100 },
    });
  }

  getEnvironment(
    databaseId: Ulid,
    environmentId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DatabaseEnvironment>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, { projectId, databaseId, environmentId })}`,
      routeName: "databases.environments.get",
    });
  }

  switchEnvironment(
    databaseId: Ulid,
    environmentId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectSwitchResponse>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, { projectId, databaseId, environmentId })}:switch`,
      routeName: "databases.environments.switch",
    });
  }

  listBackups(
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Backup>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/backups`,
      routeName: "databases.backups.list",
      query,
    });
  }

  createBackup(
    databaseId: Ulid,
    request: Models.CreateBackupRequest = {},
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.Backup>> {
    return this.http.request({
      method: "POST",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/backups`,
      routeName: "databases.backups.create",
      body: request,
    });
  }

  restoreBackup(
    databaseId: Ulid,
    backupId: Ulid,
    request: Models.CreateRestoreRequest = {},
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.Restore>> {
    return this.http.request({
      method: "POST",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/backups/${ulidPathSegment(backupId)}:restore`,
      routeName: "databases.restores.create",
      body: request,
    });
  }

  listRestores(
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Restore>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/restores`,
      routeName: "databases.restores.list",
      query,
    });
  }
}

export class RepositoriesClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    scope: Models.EnvironmentScoped,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.RepositoryDefinition>>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, scope)}/repositories`,
      routeName: "repositories.list",
      query,
    });
  }

  create(
    scope: Models.EnvironmentScoped,
    request: Models.CreateRepositoryRequest,
  ): Promise<TerraScaleResult<Models.RepositoryDefinition>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/repositories`,
      routeName: "repositories.create",
      body: request,
    });
  }

  get(scope: Models.RepositoryScoped): Promise<TerraScaleResult<Models.RepositoryDefinition>> {
    return this.http.request({
      method: "GET",
      path: repositoryRoot(this.http, scope),
      routeName: "repositories.get",
    });
  }

  update(
    scope: Models.RepositoryScoped,
    request: Models.UpdateRepositoryRequest,
  ): Promise<TerraScaleResult<Models.RepositoryDefinition>> {
    return this.http.request({
      method: "PATCH",
      path: repositoryRoot(this.http, scope),
      routeName: "repositories.update",
      body: request,
    });
  }

  delete(scope: Models.RepositoryScoped): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: repositoryRoot(this.http, scope),
      routeName: "repositories.delete",
    });
  }
}

export class DocumentsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list<TDocument extends Models.JsonObject = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    query?: Models.DocumentListQuery,
  ): Promise<TerraScaleResult<Models.Page<Models.DocumentEnvelope<TDocument>>>> {
    return this.http.request({
      method: "GET",
      path: `${repositoryRoot(this.http, scope)}/documents`,
      routeName: "documents.list",
      query,
    });
  }

  get<TDocument extends Models.JsonObject = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
  ): Promise<TerraScaleResult<Models.DocumentEnvelope<TDocument>>> {
    return this.http.request({
      method: "GET",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.get",
    });
  }

  put<TDocument extends Models.JsonObject = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    document: TDocument,
  ): Promise<TerraScaleResult<Models.DocumentEnvelope<TDocument>>> {
    return this.http.request({
      method: "PUT",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.put",
      body: document,
    });
  }

  patch<TDocument extends Models.JsonObject = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    operations: readonly Models.PatchOperation[],
  ): Promise<TerraScaleResult<Models.DocumentEnvelope<TDocument>>> {
    return this.http.request({
      method: "PATCH",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.patch",
      body: operations,
      headers: { "content-type": "application/json-patch+json" },
    });
  }

  delete(scope: Models.RepositoryScoped, documentId: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.delete",
    });
  }
}

export class QueryClient {
  constructor(private readonly http: TerraScaleHttp) {}

  execute(
    scope: Models.EnvironmentScoped,
    request: Models.QueryRequest,
  ): Promise<TerraScaleResult<Models.QueryResult>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/query`,
      routeName: "query.execute",
      body: request,
    });
  }

  explain(
    scope: Models.EnvironmentScoped,
    request: Models.QueryRequest,
  ): Promise<TerraScaleResult<Models.QueryPlan>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/query:explain`,
      routeName: "query.explain",
      body: request,
    });
  }
}

export class SchemaClient {
  constructor(private readonly http: TerraScaleHttp) {}

  get(scope: Models.EnvironmentScoped): Promise<TerraScaleResult<Models.SchemaDocument>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, scope)}/schema`,
      routeName: "schema.get",
    });
  }

  validate(
    scope: Models.EnvironmentScoped,
    request: Models.ApplySchemaRequest,
  ): Promise<TerraScaleResult<Models.QueryPlan>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/schema:validate`,
      routeName: "schema.validate",
      body: request,
    });
  }

  apply(
    scope: Models.EnvironmentScoped,
    request: Models.ApplySchemaRequest,
  ): Promise<TerraScaleResult<Models.SchemaDocument>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/schema`,
      routeName: "schema.apply",
      body: request,
    });
  }
}

export class SavedQueriesClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    scope: Models.EnvironmentScoped,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.SavedQuery>>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, scope)}/saved-queries`,
      routeName: "savedQueries.list",
      query,
    });
  }

  create(
    scope: Models.EnvironmentScoped,
    request: Models.SaveQueryRequest,
  ): Promise<TerraScaleResult<Models.SavedQuery>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/saved-queries`,
      routeName: "savedQueries.create",
      body: request,
    });
  }

  get(
    scope: Models.EnvironmentScoped,
    savedQueryId: Ulid,
  ): Promise<TerraScaleResult<Models.SavedQuery>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, scope)}/saved-queries/${ulidPathSegment(savedQueryId)}`,
      routeName: "savedQueries.get",
    });
  }

  update(
    scope: Models.EnvironmentScoped,
    savedQueryId: Ulid,
    request: Partial<Models.SaveQueryRequest>,
  ): Promise<TerraScaleResult<Models.SavedQuery>> {
    return this.http.request({
      method: "PATCH",
      path: `${environmentRoot(this.http, scope)}/saved-queries/${ulidPathSegment(savedQueryId)}`,
      routeName: "savedQueries.update",
      body: request,
    });
  }

  delete(scope: Models.EnvironmentScoped, savedQueryId: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: `${environmentRoot(this.http, scope)}/saved-queries/${ulidPathSegment(savedQueryId)}`,
      routeName: "savedQueries.delete",
    });
  }

  run(
    scope: Models.EnvironmentScoped,
    savedQueryId: Ulid,
  ): Promise<TerraScaleResult<Models.QueryResult>> {
    return this.http.request({
      method: "POST",
      path: `${environmentRoot(this.http, scope)}/saved-queries/${ulidPathSegment(savedQueryId)}:run`,
      routeName: "savedQueries.run",
    });
  }
}

export class QueryHistoryClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    scope: Models.EnvironmentScoped,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.QueryHistoryEntry>>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, scope)}/query-history`,
      routeName: "history.list",
      query,
    });
  }

  get(
    scope: Models.EnvironmentScoped,
    queryHistoryId: Ulid,
  ): Promise<TerraScaleResult<Models.QueryHistoryEntry>> {
    return this.http.request({
      method: "GET",
      path: `${environmentRoot(this.http, scope)}/query-history/${ulidPathSegment(queryHistoryId)}`,
      routeName: "history.get",
    });
  }
}

export class ApiKeysClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(projectId?: Ulid): Promise<TerraScaleResult<Models.Page<Models.ApiKey>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/api-keys`,
      routeName: "apiKeys.list",
    });
  }

  create(
    request: Models.CreateApiKeyRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.CreatedApiKey>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/api-keys`,
      routeName: "apiKeys.create",
      body: request,
    });
  }

  revoke(apiKeyId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/api-keys/${ulidPathSegment(apiKeyId)}:revoke`,
      routeName: "apiKeys.revoke",
    });
  }
}

export class MachineClientsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(projectId?: Ulid): Promise<TerraScaleResult<Models.Page<Models.MachineClient>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/machine-clients`,
      routeName: "machineClients.list",
    });
  }

  create(
    request: Models.CreateMachineClientRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.MachineClient>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/machine-clients`,
      routeName: "machineClients.create",
      body: request,
    });
  }

  rotateSecret(
    machineClientId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.RotatedMachineClientSecret>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/machine-clients/${ulidPathSegment(machineClientId)}:rotate-secret`,
      routeName: "machineClients.rotateSecret",
    });
  }

  revoke(machineClientId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/machine-clients/${ulidPathSegment(machineClientId)}:revoke`,
      routeName: "machineClients.revoke",
    });
  }
}

export class AccessClient {
  constructor(private readonly http: TerraScaleHttp) {}

  listTeamMembers(
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.TeamMember>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/access/team-members`,
      routeName: "access.teamMembers.list",
      query,
    });
  }

  addTeamMember(
    request: Models.AddTeamMemberRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.TeamMember>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/access/team-members`,
      routeName: "access.teamMembers.add",
      body: request,
    });
  }

  updateTeamMember(
    userId: Ulid,
    request: Models.UpdateTeamMemberRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.TeamMember>> {
    return this.http.request({
      method: "PATCH",
      path: `${projectRoot(this.http, projectId)}/access/team-members/${ulidPathSegment(userId)}`,
      routeName: "access.teamMembers.update",
      body: request,
    });
  }

  removeTeamMember(userId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: `${projectRoot(this.http, projectId)}/access/team-members/${ulidPathSegment(userId)}`,
      routeName: "access.teamMembers.remove",
    });
  }
}

export class UsageClient {
  constructor(private readonly http: TerraScaleHttp) {}

  getProjectUsage(projectId?: Ulid): Promise<TerraScaleResult<Models.UsageSummary>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/usage`,
      routeName: "usage.project.get",
    });
  }

  getProjectUsageWorkload(
    range: Models.UsageWorkloadRange,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.UsageWorkload>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/usage/workload`,
      routeName: "usage.project.workload",
      query: { range },
    });
  }
}

export class BillingClient {
  constructor(private readonly http: TerraScaleHttp) {}

  getAccount(organizationId: Ulid): Promise<TerraScaleResult<Models.BillingAccount>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/billing`,
      routeName: "billing.account.get",
    });
  }

  listInvoices(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Invoice>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/billing/invoices`,
      routeName: "billing.invoices.list",
      query,
    });
  }

  createCheckoutSession(
    organizationId: Ulid,
    request: Models.CreateCheckoutSessionRequest,
  ): Promise<TerraScaleResult<Models.CheckoutSession>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/billing/checkout-sessions`,
      routeName: "billing.checkoutSessions.create",
      body: request,
    });
  }

  createPortalSession(
    organizationId: Ulid,
    returnUrl: string,
  ): Promise<TerraScaleResult<{ readonly url: string }>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/billing/portal-sessions`,
      routeName: "billing.portalSessions.create",
      body: { returnUrl },
    });
  }
}

export class PricingClient {
  constructor(private readonly http: TerraScaleHttp) {}

  listPlans(): Promise<TerraScaleResult<readonly Models.PricingPlan[]>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/pricing/plans`,
      routeName: "pricing.plans.list",
    });
  }
}

export class AuditClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.AuditEvent>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/audit`,
      routeName: "audit.list",
      query,
    });
  }

  get(auditEventId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.AuditEvent>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/audit/${ulidPathSegment(auditEventId)}`,
      routeName: "audit.get",
    });
  }
}

export class NotificationsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    query?: Models.PageRequest & { readonly unreadOnly?: boolean },
  ): Promise<TerraScaleResult<Models.Page<Models.Notification>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/dashboard/notifications`,
      routeName: "notifications.list",
      query,
    });
  }

  markRead(notificationId: Ulid): Promise<TerraScaleResult<Models.Notification>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/dashboard/notifications/${ulidPathSegment(notificationId)}:mark-read`,
      routeName: "notifications.markRead",
    });
  }

  markAllRead(): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/dashboard/notifications:mark-all-read`,
      routeName: "notifications.markAllRead",
    });
  }
}

export class SettingsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  getProject(projectId?: Ulid): Promise<TerraScaleResult<Models.ProjectSettings>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/settings`,
      routeName: "settings.project.get",
    });
  }

  updateProject(
    request: Partial<Models.ProjectSettings>,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectSettings>> {
    return this.http.request({
      method: "PATCH",
      path: `${projectRoot(this.http, projectId)}/settings`,
      routeName: "settings.project.update",
      body:
        request.defaultRegions === undefined
          ? request
          : { ...request, defaultRegions: normalizeRegionCodes(request.defaultRegions) },
    });
  }
}

export class SecurityClient {
  constructor(private readonly http: TerraScaleHttp) {}

  getSettings(projectId?: Ulid): Promise<TerraScaleResult<Models.SecuritySettings>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/security`,
      routeName: "security.settings.get",
    });
  }

  updateSettings(
    request: Partial<Models.SecuritySettings>,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.SecuritySettings>> {
    return this.http.request({
      method: "PATCH",
      path: `${projectRoot(this.http, projectId)}/security`,
      routeName: "security.settings.update",
      body: request,
    });
  }

  listWebhooks(projectId?: Ulid): Promise<TerraScaleResult<Models.Page<Models.Webhook>>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/webhooks`,
      routeName: "security.webhooks.list",
    });
  }

  createWebhook(
    request: Models.CreateWebhookRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.Webhook>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/webhooks`,
      routeName: "security.webhooks.create",
      body: request,
    });
  }

  updateWebhook(
    webhookId: Ulid,
    request: Models.UpdateWebhookRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.Webhook>> {
    return this.http.request({
      method: "PATCH",
      path: `${projectRoot(this.http, projectId)}/webhooks/${ulidPathSegment(webhookId)}`,
      routeName: "security.webhooks.update",
      body: request,
    });
  }

  deleteWebhook(webhookId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: `${projectRoot(this.http, projectId)}/webhooks/${ulidPathSegment(webhookId)}`,
      routeName: "security.webhooks.delete",
    });
  }

  testWebhook(webhookId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/webhooks/${ulidPathSegment(webhookId)}:test`,
      routeName: "security.webhooks.test",
    });
  }

  getSso(projectId?: Ulid): Promise<TerraScaleResult<Models.SsoSettings>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/security/sso`,
      routeName: "security.sso.get",
    });
  }

  updateSso(
    request: Partial<Models.SsoSettings>,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.SsoSettings>> {
    return this.http.request({
      method: "PATCH",
      path: `${projectRoot(this.http, projectId)}/security/sso`,
      routeName: "security.sso.update",
      body: request,
    });
  }

  getScim(projectId?: Ulid): Promise<TerraScaleResult<Models.ScimSettings>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/security/scim`,
      routeName: "security.scim.get",
    });
  }

  updateScim(
    request: Partial<Models.ScimSettings>,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ScimSettings>> {
    return this.http.request({
      method: "PATCH",
      path: `${projectRoot(this.http, projectId)}/security/scim`,
      routeName: "security.scim.update",
      body: request,
    });
  }

  createScimToken(projectId?: Ulid): Promise<TerraScaleResult<Models.CreatedScimToken>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/security/scim/tokens`,
      routeName: "security.scim.tokens.create",
    });
  }

  revokeScimToken(tokenId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/security/scim/tokens/${ulidPathSegment(tokenId)}:revoke`,
      routeName: "security.scim.tokens.revoke",
    });
  }
}

export class AdminClient {
  constructor(private readonly http: TerraScaleHttp) {}

  overview(): Promise<TerraScaleResult<Models.AdminPlatformUsage>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin`,
      routeName: "admin.overview",
    });
  }

  listUsers(
    query?: Models.PageRequest & { readonly search?: string },
  ): Promise<TerraScaleResult<Models.Page<Models.AdminUser>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/users`,
      routeName: "admin.users.list",
      query,
    });
  }

  getUser(userId: Ulid): Promise<TerraScaleResult<Models.AdminUser>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/users/${ulidPathSegment(userId)}`,
      routeName: "admin.users.get",
    });
  }

  setUserEnabled(userId: Ulid, enabled: boolean): Promise<TerraScaleResult<Models.AdminUser>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/admin/users/${ulidPathSegment(userId)}:${enabled ? "enable" : "disable"}`,
      routeName: enabled ? "admin.users.enable" : "admin.users.disable",
    });
  }

  deleteUser(userId: Ulid): Promise<TerraScaleResult<void>> {
    return this.http.request({
      method: "DELETE",
      path: `${apiRoot}/admin/users/${ulidPathSegment(userId)}`,
      routeName: "admin.users.delete",
    });
  }

  createSupportNote(
    request: Models.AdminCreateSupportNoteRequest,
  ): Promise<TerraScaleResult<Models.AdminSupportNote>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/admin/support-notes`,
      routeName: "admin.supportNotes.create",
      body: request,
    });
  }

  listOrganizations(
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Organization>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/organizations`,
      routeName: "admin.organizations.list",
      query,
    });
  }

  listProjects(query?: Models.PageRequest): Promise<TerraScaleResult<Models.Page<Models.Project>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/projects`,
      routeName: "admin.projects.list",
      query,
    });
  }

  listDatabases(
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.ProjectDatabase>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/databases`,
      routeName: "admin.databases.list",
      query,
    });
  }

  getUsage(): Promise<TerraScaleResult<Models.AdminPlatformUsage>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/usage`,
      routeName: "admin.usage.get",
    });
  }

  listPricingPlans(): Promise<TerraScaleResult<readonly Models.PricingPlan[]>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/plans`,
      routeName: "admin.pricing.list",
    });
  }

  createPricingPlan(
    request: Models.AdminPricingPlanRequest,
  ): Promise<TerraScaleResult<Models.PricingPlan>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/admin/plans`,
      routeName: "admin.pricing.create",
      body: request,
    });
  }

  updatePricingPlan(
    planId: Ulid,
    request: Models.AdminPricingPlanRequest,
  ): Promise<TerraScaleResult<Models.PricingPlan>> {
    return this.http.request({
      method: "PATCH",
      path: `${apiRoot}/admin/plans/${ulidPathSegment(planId)}`,
      routeName: "admin.pricing.update",
      body: request,
    });
  }

  deletePricingPlan(planId: Ulid): Promise<TerraScaleResult<boolean>> {
    return this.http.request({
      method: "DELETE",
      path: `${apiRoot}/admin/plans/${ulidPathSegment(planId)}`,
      routeName: "admin.pricing.delete",
    });
  }

  listAudit(query?: Models.PageRequest): Promise<TerraScaleResult<Models.Page<Models.AuditEvent>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/audit`,
      routeName: "admin.audit.list",
      query,
    });
  }

  listNotifications(
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.Notification>>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/admin/notifications`,
      routeName: "admin.notifications.list",
      query,
    });
  }
}

function normalizeDatabaseUpdate(
  request: Models.UpdateDatabaseRequest,
): Models.UpdateDatabaseRequest {
  return request.regions === undefined
    ? request
    : {
        ...request,
        regions: normalizeRegionCodes(request.regions),
      };
}

function toSignupRequest(request: Models.RegisterRequest) {
  const displayNameParts = (request.displayName ?? "").trim().split(/\s+/u).filter(Boolean);
  const firstName = request.firstName ?? displayNameParts[0] ?? "";
  const lastName = request.lastName ?? displayNameParts.slice(1).join(" ");

  return {
    email: request.email,
    password: request.password,
    firstName,
    lastName,
    verifyUrl: request.verifyUrl ?? request.redirectUrl ?? "",
  };
}

function projectRoot(http: TerraScaleHttp, projectId: Ulid | undefined): string {
  const selectedProjectId = projectId ?? http.config.projectId;

  if (selectedProjectId === undefined) {
    throw new TypeError(
      "A projectId must be supplied either in the request or TerraScaleClient runtime config.",
    );
  }

  return `${apiRoot}/projects/${ulidPathSegment(selectedProjectId)}`;
}

function projectAuthRoot(projectId: Ulid): string {
  return `${apiRoot}/projects/${ulidPathSegment(projectId)}/auth`;
}

function authRoute(
  http: TerraScaleHttp,
  projectId: Ulid | undefined,
  globalPath: string,
  projectPath: (projectId: Ulid) => string,
): string {
  const selectedProjectId = projectId ?? http.config.projectId;
  return selectedProjectId === undefined ? globalPath : projectPath(selectedProjectId);
}

function databaseRoot(http: TerraScaleHttp, scope: Models.DatabaseScoped): string {
  return `${projectRoot(http, scope.projectId)}/databases/${ulidPathSegment(scope.databaseId)}`;
}

function environmentRoot(http: TerraScaleHttp, scope: Models.EnvironmentScoped): string {
  return `${databaseRoot(http, scope)}/environments/${ulidPathSegment(scope.environmentId)}`;
}

function repositoryRoot(http: TerraScaleHttp, scope: Models.RepositoryScoped): string {
  return `${environmentRoot(http, scope)}/repositories/${ulidPathSegment(scope.repositoryId)}`;
}

export function queryRecord(query: QueryRecord): QueryRecord {
  return query;
}
