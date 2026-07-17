import type {
  DashboardLogicalDatabaseKey,
  ResolvedTerraScaleRuntimeConfig,
  TerraScaleRuntimeConfig,
} from "./config.js";
import { TerraScaleHttp, type QueryRecord, type TerraScaleRequestOptions } from "./http.js";
import { type Ulid, ulidPathSegment } from "./ids.js";
import type * as Models from "./models.js";
import { normalizeRegionCodes } from "./regions.js";
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
  readonly regions: RegionsClient;
  readonly databases: DatabasesClient;
  readonly queues: QueuesClient;
  readonly repositories: RepositoriesClient;
  readonly backups: BackupsClient;
  readonly documents: DocumentsClient;
  readonly query: QueryClient;
  readonly schema: SchemaClient;
  readonly apiKeys: ApiKeysClient;
  readonly m2m: M2MApplicationsClient;
  readonly access: AccessClient;
  readonly usage: UsageClient;
  readonly billing: BillingClient;
  readonly pricing: PricingClient;
  readonly audit: AuditClient;
  readonly notifications: NotificationsClient;
  readonly security: SecurityClient;
  readonly admin: AdminClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.http = new TerraScaleHttp(config);
    this.auth = new AuthClient(this.http);
    this.organizations = new OrganizationsClient(this.http);
    this.regions = new RegionsClient(this.http);
    this.databases = new DatabasesClient(this.http);
    this.queues = new QueuesClient(this.http);
    this.repositories = new RepositoriesClient(this.http);
    this.backups = new BackupsClient(this.http);
    this.documents = new DocumentsClient(this.http);
    this.query = new QueryClient(this.http);
    this.schema = new SchemaClient(this.http);
    this.apiKeys = new ApiKeysClient(this.http);
    this.m2m = new M2MApplicationsClient(this.http);
    this.access = new AccessClient(this.http);
    this.usage = new UsageClient(this.http);
    this.billing = new BillingClient(this.http);
    this.pricing = new PricingClient(this.http);
    this.audit = new AuditClient(this.http);
    this.notifications = new NotificationsClient(this.http);
    this.security = new SecurityClient(this.http);
    this.admin = new AdminClient(this.http);
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

export class RegionsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(): Promise<TerraScaleResult<Models.RegionsResponse>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/regions`,
      routeName: "regions.list",
    });
  }

  get(regionCode: string): Promise<TerraScaleResult<Models.RegionDetails>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/regions/${encodeURIComponent(regionCode.trim().toLowerCase())}`,
      routeName: "regions.get",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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
      headerPolicy: "anonymous",
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

  list(query?: Models.PageRequest): Promise<TerraScaleResult<readonly Models.Organization[]>> {
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

  listProjects(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.Project[]>> {
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
  ): Promise<TerraScaleResult<readonly Models.OrganizationUser[]>> {
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
      body: {
        ...request,
        regions: normalizeRegionCodes(request.regions),
      },
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
      body: {
        ...request,
        regions: request.regions === undefined ? undefined : normalizeRegionCodes(request.regions),
      },
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

export class QueuesClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(projectId?: Ulid): Promise<TerraScaleResult<readonly Models.ProjectQueue[]>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/queues`,
      routeName: "queues.list",
    });
  }

  create(
    request: Models.CreateProjectQueueRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueue>> {
    return this.http.request({
      method: "POST",
      path: `${projectRoot(this.http, projectId)}/queues`,
      routeName: "queues.create",
      body: request,
    });
  }

  get(queueId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.ProjectQueue>> {
    return this.http.request({
      method: "GET",
      path: queueRoot(this.http, queueId, projectId),
      routeName: "queues.get",
    });
  }

  delete(queueId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<boolean>> {
    return this.http.request({
      method: "DELETE",
      path: queueRoot(this.http, queueId, projectId),
      routeName: "queues.delete",
    });
  }

  purge(
    queueId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<{ readonly queueName: string; readonly purgedMessages: number }>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:purge`,
      routeName: "queues.purge",
    });
  }

  publish(
    queueId: Ulid,
    request: Models.PublishProjectQueueMessageRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueuePublishResult>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:publish`,
      routeName: "queues.publish",
      body: request,
    });
  }

  peek(
    queueId: Ulid,
    maxMessages = 10,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.ProjectQueuePeekMessage[]>> {
    return this.http.request({
      method: "GET",
      path: `${queueRoot(this.http, queueId, projectId)}:peek`,
      routeName: "queues.peek",
      query: { maxMessages },
    });
  }

  receive(
    queueId: Ulid,
    request: Models.ReceiveProjectQueueMessagesRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.ProjectQueueReceivedMessage[]>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:receive`,
      routeName: "queues.receive",
      body: request,
    });
  }

  complete(
    queueId: Ulid,
    receipt: Models.ProjectQueueReceipt,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueueReceiptActionResult>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:complete`,
      routeName: "queues.complete",
      body: { receipt: receipt.receipt },
      headers: preferredRegionHeaders(receipt.region),
    });
  }

  abandon(
    queueId: Ulid,
    receipt: Models.ProjectQueueReceipt,
    delaySeconds?: number,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueueReceiptActionResult>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:abandon`,
      routeName: "queues.abandon",
      body: { receipt: receipt.receipt, delaySeconds },
      headers: preferredRegionHeaders(receipt.region),
    });
  }

  deadLetter(
    queueId: Ulid,
    receipt: Models.ProjectQueueReceipt,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueueReceiptActionResult>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:dead-letter`,
      routeName: "queues.deadLetter",
      body: { receipt: receipt.receipt },
      headers: preferredRegionHeaders(receipt.region),
    });
  }

  renewLock(
    queueId: Ulid,
    receipt: Models.ProjectQueueReceipt,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueueRenewLockResult>> {
    return this.http.request({
      method: "POST",
      path: `${queueRoot(this.http, queueId, projectId)}:renew-lock`,
      routeName: "queues.renewLock",
      body: { receipt: receipt.receipt },
      headers: preferredRegionHeaders(receipt.region),
    });
  }

}

export class DatabasesClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.ProjectDatabase[]>> {
    return this.http.request({
      method: "GET",
      path: `${projectRoot(this.http, projectId)}/databases`,
      routeName: "databases.list",
      query: query ?? { skip: 0, take: 100 },
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
      body: request,
    });
  }

  get(databaseId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.http.request({
      method: "GET",
      path: databaseRoot(this.http, { projectId, databaseId }),
      routeName: "databases.get",
    });
  }

  delete(
    databaseId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DeletedProjectDatabaseResponse>> {
    return this.http.request({
      method: "DELETE",
      path: databaseRoot(this.http, { projectId, databaseId }),
      routeName: "databases.delete",
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

  createEnvironment(
    databaseId: Ulid,
    request: Models.CreateDatabaseEnvironmentRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DatabaseEnvironment>> {
    return this.http.request({
      method: "POST",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/environments`,
      routeName: "databases.environments.create",
      body: request,
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

  deleteEnvironment(
    databaseId: Ulid,
    environmentId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DeletedProjectDatabaseEnvironmentResponse>> {
    return this.http.request({
      method: "DELETE",
      path: environmentRoot(this.http, { projectId, databaseId, environmentId }),
      routeName: "databases.environments.delete",
    });
  }
}

export class RepositoriesClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(): Promise<TerraScaleResult<readonly Models.RepositoryDefinition[]>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/repositories`,
      routeName: "repositories.list",
    });
  }

  create(
    request: Models.CreateRepositoryRequest,
  ): Promise<TerraScaleResult<Models.RepositoryDefinition>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/repositories`,
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

  delete(
    scope: Models.RepositoryScoped,
    query?: { readonly deleteDocuments?: boolean },
  ): Promise<TerraScaleResult<Models.DeletedRepositoryResponse>> {
    return this.http.request({
      method: "DELETE",
      path: repositoryRoot(this.http, scope),
      routeName: "repositories.delete",
      query,
    });
  }
}

export class BackupsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  configurePolicy(
    databaseId: Ulid,
    request: Models.ConfigureBackupPolicyRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.BackupPolicyResponse>> {
    return this.http.request({
      method: "PUT",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/backups/policy`,
      routeName: "backups.policy.configure",
      body: request,
    });
  }

  triggerBackup(
    databaseId: Ulid,
    request?: Models.TriggerBackupRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.BackupJobResponse>> {
    return this.http.request({
      method: "POST",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/backups`,
      routeName: "backups.trigger",
      body: request,
    });
  }

  listBackups(
    databaseId: Ulid,
    query?: Models.BackupListQuery,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.BackupJobResponse[]>> {
    return this.http.request({
      method: "GET",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/backups`,
      routeName: "backups.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }

  getBackup(
    databaseId: Ulid,
    backupId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.BackupJobResponse>> {
    return this.http.request({
      method: "GET",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/backups/${ulidPathSegment(backupId)}`,
      routeName: "backups.get",
    });
  }

  listRestorePoints(
    databaseId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.RestorePointResponse[]>> {
    return this.http.request({
      method: "GET",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/restore-points`,
      routeName: "backups.restorePoints.list",
    });
  }

  startRestore(
    databaseId: Ulid,
    request: Models.StartRestoreRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.RestoreJobResponse>> {
    return this.http.request({
      method: "POST",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/restores`,
      routeName: "backups.restores.start",
      body: request,
    });
  }

  getRestore(
    databaseId: Ulid,
    restoreId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.RestoreJobResponse>> {
    return this.http.request({
      method: "GET",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/restores/${ulidPathSegment(restoreId)}`,
      routeName: "backups.restores.get",
    });
  }

  listRestores(
    databaseId: Ulid,
    query?: Models.RestoreListQuery,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.RestoreJobResponse[]>> {
    return this.http.request({
      method: "GET",
      path: `${databaseRoot(this.http, { projectId, databaseId })}/restores`,
      routeName: "backups.restores.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }
}

export class DocumentsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    query?: Models.DocumentListQuery,
  ): Promise<TerraScaleResult<Models.DocumentPageResponse<TDocument>>> {
    return this.http.request({
      method: "GET",
      path: `${repositoryRoot(this.http, scope)}/documents`,
      routeName: "documents.list",
      query,
    });
  }

  create<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    data: TDocument,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.http.request({
      method: "POST",
      path: `${repositoryRoot(this.http, scope)}/documents`,
      routeName: "documents.create",
      body: { data },
    });
  }

  get<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.http.request({
      method: "GET",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.get",
    });
  }

  put<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    data: TDocument,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.http.request({
      method: "PUT",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.put",
      body: { data },
    });
  }

  patch<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    data: TDocument,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.http.request({
      method: "PATCH",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.patch",
      body: { data },
    });
  }

  batch<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    request: Models.DocumentBatchRequest<TDocument>,
  ): Promise<TerraScaleResult<Models.DocumentBatchResponse<TDocument>>> {
    return this.http.request({
      method: "POST",
      path: `${repositoryRoot(this.http, scope)}/documents/batch`,
      routeName: "documents.batch",
      body: request,
    });
  }

  delete(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
  ): Promise<TerraScaleResult<Models.DeletedDocumentResponse>> {
    return this.http.request({
      method: "DELETE",
      path: `${repositoryRoot(this.http, scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.delete",
    });
  }
}

export class QueryClient {
  constructor(private readonly http: TerraScaleHttp) {}

  execute<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    request: Models.QueryRequest,
  ): Promise<TerraScaleResult<Models.DocumentPageResponse<TDocument>>> {
    return this.http.request({
      method: "POST",
      path: `${repositoryRoot(this.http, scope)}/tql`,
      routeName: "query.execute",
      body: request,
    });
  }
}

export class SchemaClient {
  constructor(private readonly http: TerraScaleHttp) {}

  get(scope: Models.RepositoryScoped): Promise<TerraScaleResult<Models.RepositorySchema>> {
    return this.http.request({
      method: "GET",
      path: `${repositoryRoot(this.http, scope)}/schema`,
      routeName: "schema.get",
    });
  }

  validate(
    scope: Models.RepositoryScoped,
    request: Models.ValidateRepositorySchemaRequest = {},
  ): Promise<TerraScaleResult<Models.RepositorySchemaValidationResponse>> {
    return this.http.request({
      method: "POST",
      path: `${repositoryRoot(this.http, scope)}/schema/validate`,
      routeName: "schema.validate",
      body: request,
    });
  }

  configure(
    scope: Models.RepositoryScoped,
    request: Models.ConfigureRepositorySchemaRequest,
  ): Promise<TerraScaleResult<Models.RepositorySchema>> {
    return this.http.request({
      method: "PUT",
      path: `${repositoryRoot(this.http, scope)}/schema`,
      routeName: "schema.configure",
      body: request,
    });
  }
}

export class ApiKeysClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    projectId?: Ulid,
    query?: Models.ListApiKeysRequest,
  ): Promise<TerraScaleResult<Models.ListApiKeysResponse>> {
    return this.http.request({
      method: "GET",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/api-keys`,
      routeName: "apiKeys.list",
      query,
    });
  }

  create(
    request: Models.CreateApiKeyRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.CreatedApiKey>> {
    return this.http.request({
      method: "POST",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/api-keys`,
      routeName: "apiKeys.create",
      body: request,
    });
  }

  rotate(apiKeyId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.RotatedApiKey>> {
    return this.http.request({
      method: "POST",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/api-keys/${ulidPathSegment(apiKeyId)}/rotate`,
      routeName: "apiKeys.rotate",
    });
  }

  revoke(apiKeyId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.RevokedApiKey>> {
    return this.http.request({
      method: "DELETE",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/api-keys/${ulidPathSegment(apiKeyId)}`,
      routeName: "apiKeys.revoke",
    });
  }
}

export class M2MApplicationsClient {
  constructor(private readonly http: TerraScaleHttp) {}

  list(
    projectId?: Ulid,
    query?: Models.ListM2MApplicationsRequest,
  ): Promise<TerraScaleResult<Models.ListM2MApplicationsResponse>> {
    return this.http.request({
      method: "GET",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/m2m-applications`,
      routeName: "m2m.list",
      query,
    });
  }

  create(
    request: Models.CreateM2MApplicationRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.CreatedM2MApplication>> {
    return this.http.request({
      method: "POST",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/m2m-applications`,
      routeName: "m2m.create",
      body: request,
    });
  }

  rotate(
    clientId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.RotatedM2MApplicationSecret>> {
    return this.http.request({
      method: "POST",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/m2m-applications/${ulidPathSegment(clientId)}/rotate`,
      routeName: "m2m.rotate",
    });
  }

  revoke(clientId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.RevokedM2MApplication>> {
    return this.http.request({
      method: "DELETE",
      path: `${projectAuthRoot(projectRootId(this.http, projectId))}/m2m-applications/${ulidPathSegment(clientId)}`,
      routeName: "m2m.revoke",
    });
  }

  createToken(
    projectId: Ulid,
    request: Models.M2MTokenRequest,
  ): Promise<TerraScaleResult<Models.M2MTokenResponse>> {
    return this.http.request({
      method: "POST",
      path: `${projectAuthRoot(projectId)}/m2m/token`,
      routeName: "m2m.token.create",
      headerPolicy: "anonymous",
      body: {
        grant_type: request.grantType ?? "client_credentials",
        client_id: request.clientId,
        client_secret: request.clientSecret,
        scope: request.scope ?? "",
      },
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
  ): Promise<TerraScaleResult<readonly Models.Notification[]>> {
    return this.http.request({
      method: "GET",
      path: `${apiRoot}/users/me/notifications`,
      routeName: "notifications.list",
      query,
    });
  }

  markRead(notificationId: Ulid): Promise<TerraScaleResult<Models.Notification>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/users/me/notifications/${ulidPathSegment(notificationId)}:mark-read`,
      routeName: "notifications.markRead",
    });
  }

  markAllRead(): Promise<TerraScaleResult<Models.MarkAllDashboardNotificationsReadResponse>> {
    return this.http.request({
      method: "POST",
      path: `${apiRoot}/users/me/notifications:mark-all-read`,
      routeName: "notifications.markAllRead",
    });
  }
}

export class SecurityClient {
  constructor(private readonly http: TerraScaleHttp) {}

  updateSettings(
    request: Models.UpdateSecuritySettingsRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.SecuritySettings>> {
    return this.http.request({
      method: "PUT",
      path: `${projectRoot(this.http, projectId)}/security/settings`,
      routeName: "security.settings.update",
      body: request,
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

function projectRootId(http: TerraScaleHttp, projectId: Ulid | undefined): Ulid {
  const selectedProjectId = projectId ?? http.config.projectId;

  if (selectedProjectId === undefined) {
    throw new TypeError(
      "A projectId must be supplied either in the request or TerraScaleClient runtime config.",
    );
  }

  return selectedProjectId;
}

function projectRoot(http: TerraScaleHttp, projectId: Ulid | undefined): string {
  return `${apiRoot}/projects/${ulidPathSegment(projectRootId(http, projectId))}`;
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

function queueRoot(http: TerraScaleHttp, queueId: Ulid, projectId?: Ulid): string {
  return `${projectRoot(http, projectId)}/queues/${ulidPathSegment(queueId)}`;
}

function preferredRegionHeaders(region: string): HeadersInit {
  return { "x-terrascale-preferred-region": region.trim().toLowerCase() };
}

function environmentRoot(http: TerraScaleHttp, scope: Models.EnvironmentScoped): string {
  return `${databaseRoot(http, scope)}/environments/${ulidPathSegment(scope.environmentId)}`;
}

function repositoryRoot(_http: TerraScaleHttp, scope: Models.RepositoryScoped): string {
  return `${apiRoot}/repositories/${ulidPathSegment(scope.repositoryId)}`;
}

export function queryRecord(query: QueryRecord): QueryRecord {
  return query;
}
