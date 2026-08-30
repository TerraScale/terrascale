import type { ResolvedTerraScaleRuntimeConfig, TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import { type Ulid, ulidPathSegment } from "./ids.js";
import type * as Models from "./models.js";
import {
  normalizeRegionCodes,
  parseRegionCode,
  type TerraScaleRegionCode,
} from "./regions.js";
import type { TerraScaleResult, TerraScaleSuccess } from "./result.js";

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
  readonly queues: QueuesClient;
  readonly repositories: RepositoriesClient;
  readonly backups: BackupsClient;
  readonly documents: DocumentsClient;
  readonly query: QueryClient;
  readonly schema: SchemaClient;
  readonly access: AccessClient;
  readonly usage: UsageClient;
  readonly audit: AuditClient;
  readonly notifications: NotificationsClient;

  constructor(config: TerraScaleRuntimeConfig) {
    this.#http = new TerraScaleHttp(config);
    this.onboarding = new OnboardingClient(this.#http);
    this.organizations = new OrganizationsClient(this.#http);
    this.regions = new RegionsClient(this.#http);
    this.savedQueries = new SavedQueriesClient(this.#http);
    this.databases = new DatabasesClient(this.#http);
    this.databaseBranches = new DatabaseBranchesClient(this.#http);
    this.queues = new QueuesClient(this.#http);
    this.repositories = new RepositoriesClient(this.#http);
    this.backups = new BackupsClient(this.#http);
    this.documents = new DocumentsClient(this.#http);
    this.query = new QueryClient(this.#http);
    this.schema = new SchemaClient(this.#http);
    this.access = new AccessClient(this.#http);
    this.usage = new UsageClient(this.#http);
    this.audit = new AuditClient(this.#http);
    this.notifications = new NotificationsClient(this.#http);
  }

  get config(): ResolvedTerraScaleRuntimeConfig {
    return this.#http.config;
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

  async get(
    regionCode: TerraScaleRegionCode,
  ): Promise<TerraScaleResult<Models.RegionDetails>> {
    const exactRegionCode = parseRegionCode(regionCode, "regionCode");
    const result = await this.#http.request<unknown>({
      method: "GET",
      path: `${apiRoot}/regions/${encodeURIComponent(exactRegionCode)}`,
      routeName: "regions.get",
    });
    return decodeRegionResult(result, (value) => decodeRegionSummary(value, "region"));
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
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations`,
      routeName: "organizations.create",
      body: request,
    });
  }

  get(organizationId: Ulid): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}`,
      routeName: "organizations.get",
    });
  }

  update(
    organizationId: Ulid,
    request: Models.UpdateOrganizationRequest,
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "PATCH",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}`,
      routeName: "organizations.update",
      body: request,
    });
  }

  requestDeletion(
    organizationId: Ulid,
    request: Models.RequestOrganizationDeletionRequest,
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}:request-deletion`,
      routeName: "organizations.deletion.request",
      body: request,
    });
  }

  leave(organizationId: Ulid): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}:leave`,
      routeName: "organizations.leave",
    });
  }

  listProjects(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.Project[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/projects`,
      routeName: "organizations.projects.list",
      query,
    });
  }

  listMembers(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.OrganizationMember[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/members`,
      routeName: "organizations.members.list",
      query,
    });
  }

  updateMemberRole(
    organizationId: Ulid,
    userId: Ulid,
    request: Models.UpdateOrganizationMemberRoleRequest,
  ): Promise<TerraScaleResult<Models.OrganizationMember>> {
    return this.#http.request({
      method: "PATCH",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/members/${ulidPathSegment(userId)}`,
      routeName: "organizations.members.updateRole",
      body: request,
    });
  }

  removeMember(
    organizationId: Ulid,
    userId: Ulid,
    request: Models.RemoveOrganizationMemberRequest = { confirm: true },
  ): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/members/${ulidPathSegment(userId)}:remove`,
      routeName: "organizations.members.remove",
      body: request,
    });
  }

  createProject(
    organizationId: Ulid,
    request: Models.CreateProjectRequest,
  ): Promise<TerraScaleResult<Models.Project>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/projects`,
      routeName: "organizations.projects.create",
      body: {
        ...request,
        backupRegion: parseRegionCode(request.backupRegion, "backupRegion"),
        regions: normalizeRegionCodes(request.regions),
      },
    });
  }

  getProject(projectId?: Ulid): Promise<TerraScaleResult<Models.Project>> {
    return this.#http.request({
      method: "GET",
      path: projectRoot(this.#http, projectId),
      routeName: "organizations.projects.get",
    });
  }

  listProjectRegionPlacements(
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.ProjectRegionPlacement[]>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/regions`,
      routeName: "organizations.projects.regions.list",
    });
  }

  updateProject(
    projectId: Ulid,
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
    projectId: Ulid,
    request: Models.DeleteProjectRequest,
  ): Promise<TerraScaleResult<Models.DeletedProject>> {
    return this.#http.request({
      method: "DELETE",
      path: projectRoot(this.#http, projectId),
      routeName: "organizations.projects.delete",
      body: request,
    });
  }

  listInvitations(
    organizationId: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<readonly Models.Invitation[]>> {
    return this.#http.request({
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
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations`,
      routeName: "organizations.invitations.create",
      body: request,
    });
  }

  acceptInvitation(
    organizationId: Ulid,
    invitationId: Ulid,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations/${ulidPathSegment(invitationId)}:accept`,
      routeName: "organizations.invitations.accept",
    });
  }

  declineInvitation(
    organizationId: Ulid,
    invitationId: Ulid,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations/${ulidPathSegment(invitationId)}:decline`,
      routeName: "organizations.invitations.decline",
    });
  }

  resendInvitation(
    organizationId: Ulid,
    invitationId: Ulid,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations/${ulidPathSegment(invitationId)}:resend`,
      routeName: "organizations.invitations.resend",
    });
  }

  cancelInvitation(
    organizationId: Ulid,
    invitationId: Ulid,
  ): Promise<TerraScaleResult<Models.Invitation>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/invitations/${ulidPathSegment(invitationId)}:cancel`,
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
    projectId?: Ulid,
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
    savedQueryId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/saved-queries/${ulidPathSegment(savedQueryId)}`,
      routeName: "savedQueries.get",
    });
  }

  create(
    request: Models.ProjectSavedQueryRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/saved-queries`,
      routeName: "savedQueries.create",
      body: request,
    });
  }

  update(
    savedQueryId: Ulid,
    request: Models.UpdateProjectSavedQueryRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "PUT",
      path: `${projectRoot(this.#http, projectId)}/saved-queries/${ulidPathSegment(savedQueryId)}`,
      routeName: "savedQueries.update",
      body: request,
    });
  }

  delete(
    savedQueryId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DeletedProjectSavedQueryResponse>> {
    return this.#http.request({
      method: "DELETE",
      path: `${projectRoot(this.#http, projectId)}/saved-queries/${ulidPathSegment(savedQueryId)}`,
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
    projectId?: Ulid,
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
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/databases`,
      routeName: "databases.create",
      body: request,
    });
  }

  get(databaseId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.ProjectDatabase>> {
    return this.#http.request({
      method: "GET",
      path: databaseRoot(this.#http, { projectId, databaseId }),
      routeName: "databases.get",
    });
  }

  delete(
    databaseId: Ulid,
    options: Models.DeleteProjectDatabaseOptions,
  ): Promise<TerraScaleResult<Models.DeletedProjectDatabaseResponse>> {
    if (options?.confirm !== true) {
      throw new TypeError("Database deletion requires options.confirm to be true.");
    }

    return this.#http.request({
      method: "DELETE",
      path: databaseRoot(this.#http, { projectId: options.projectId, databaseId }),
      routeName: "databases.delete",
      headers: { "x-terrascale-delete-confirmation": databaseId },
    });
  }

  listEnvironments(
    databaseId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.DatabaseEnvironment[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/environments`,
      routeName: "databases.environments.list",
      query: { skip: 0, take: 100 },
    });
  }

  createEnvironment(
    databaseId: Ulid,
    request: Models.CreateDatabaseEnvironmentRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DatabaseEnvironment>> {
    return this.#http.request({
      method: "POST",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/environments`,
      routeName: "databases.environments.create",
      body: request,
    });
  }

  getEnvironment(
    databaseId: Ulid,
    environmentId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DatabaseEnvironment>> {
    return this.#http.request({
      method: "GET",
      path: `${environmentRoot(this.#http, { projectId, databaseId, environmentId })}`,
      routeName: "databases.environments.get",
    });
  }

  deleteEnvironment(
    databaseId: Ulid,
    environmentId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DeletedProjectDatabaseEnvironmentResponse>> {
    return this.#http.request({
      method: "DELETE",
      path: environmentRoot(this.#http, { projectId, databaseId, environmentId }),
      routeName: "databases.environments.delete",
    });
  }
}

export class DatabaseBranchesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  getCapability(
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.DatabaseBranchCapabilityResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/database-branches/capability`,
      routeName: "databaseBranches.getCapability",
    });
  }

  requestClone(
    request: Models.CreateDatabaseBranchRequest,
    projectId?: Ulid,
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

export class QueuesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(projectId?: Ulid): Promise<TerraScaleResult<readonly Models.ProjectQueue[]>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/queues`,
      routeName: "queues.list",
    });
  }

  create(
    request: Models.CreateProjectQueueRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueue>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/queues`,
      routeName: "queues.create",
      body: request,
    });
  }

  get(queueId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.ProjectQueue>> {
    return this.#http.request({
      method: "GET",
      path: queueRoot(this.#http, queueId, projectId),
      routeName: "queues.get",
    });
  }

  delete(queueId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<boolean>> {
    return this.#http.request({
      method: "DELETE",
      path: queueRoot(this.#http, queueId, projectId),
      routeName: "queues.delete",
    });
  }

  purge(
    queueId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.QueuePurgeResult>> {
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:purge`,
      routeName: "queues.purge",
    });
  }

  publish(
    queueId: Ulid,
    request: Models.PublishProjectQueueMessageRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueuePublishResult>> {
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:publish`,
      routeName: "queues.publish",
      body: request,
    });
  }

  peek(
    queueId: Ulid,
    maxMessages = 10,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.ProjectQueuePeekMessage[]>> {
    return this.#http.request({
      method: "GET",
      path: `${queueRoot(this.#http, queueId, projectId)}:peek`,
      routeName: "queues.peek",
      query: { maxMessages },
    });
  }

  receive(
    queueId: Ulid,
    request: Models.ReceiveProjectQueueMessagesRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.ProjectQueueReceivedMessage[]>> {
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:receive`,
      routeName: "queues.receive",
      body: request,
    });
  }

  complete(
    queueId: Ulid,
    receipt: Models.ProjectQueueReceipt,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.ProjectQueueReceiptActionResult>> {
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:complete`,
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
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:abandon`,
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
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:dead-letter`,
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
    return this.#http.request({
      method: "POST",
      path: `${queueRoot(this.#http, queueId, projectId)}:renew-lock`,
      routeName: "queues.renewLock",
      body: { receipt: receipt.receipt },
      headers: preferredRegionHeaders(receipt.region),
    });
  }
}

export class RepositoriesClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list(): Promise<TerraScaleResult<readonly Models.RepositoryDefinition[]>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/repositories`,
      routeName: "repositories.list",
    });
  }

  create(
    request: Models.CreateRepositoryRequest,
  ): Promise<TerraScaleResult<Models.RepositoryDefinition>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/repositories`,
      routeName: "repositories.create",
      body: request,
    });
  }

  get(scope: Models.RepositoryScoped): Promise<TerraScaleResult<Models.RepositoryDefinition>> {
    return this.#http.request({
      method: "GET",
      path: repositoryRoot(scope),
      routeName: "repositories.get",
    });
  }

  delete(
    scope: Models.RepositoryScoped,
    query?: { readonly deleteDocuments?: boolean },
  ): Promise<TerraScaleResult<Models.DeletedRepositoryResponse>> {
    return this.#http.request({
      method: "DELETE",
      path: repositoryRoot(scope),
      routeName: "repositories.delete",
      query,
    });
  }
}

export class BackupsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  configurePolicy(
    databaseId: Ulid,
    request: Models.ConfigureBackupPolicyRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.BackupPolicyResponse>> {
    return this.#http.request({
      method: "PUT",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups/policy`,
      routeName: "backups.policy.configure",
      body: request,
    });
  }

  planBackup(
    databaseId: Ulid,
    request: Models.DatabaseBackupConfirmationPlanRequest,
    projectId?: Ulid,
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
    databaseId: Ulid,
    request: Models.DatabaseBackupConfirmationExecuteRequest,
    idempotencyKey: string,
    projectId?: Ulid,
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
    databaseId: Ulid,
    query?: Models.BackupListQuery,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.BackupJobResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups`,
      routeName: "backups.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }

  getBackup(
    databaseId: Ulid,
    backupId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.BackupJobResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/backups/${ulidPathSegment(backupId)}`,
      routeName: "backups.get",
    });
  }

  listRestorePoints(
    databaseId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.RestorePointResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restore-points`,
      routeName: "backups.restorePoints.list",
    });
  }

  startRestore(
    databaseId: Ulid,
    request: Models.StartRestoreRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.RestoreJobResponse>> {
    return this.#http.request({
      method: "POST",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restores`,
      routeName: "backups.restores.start",
      body: request,
    });
  }

  getRestore(
    databaseId: Ulid,
    restoreId: Ulid,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.RestoreJobResponse>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restores/${ulidPathSegment(restoreId)}`,
      routeName: "backups.restores.get",
    });
  }

  listRestores(
    databaseId: Ulid,
    query?: Models.RestoreListQuery,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<readonly Models.RestoreJobResponse[]>> {
    return this.#http.request({
      method: "GET",
      path: `${databaseRoot(this.#http, { projectId, databaseId })}/restores`,
      routeName: "backups.restores.list",
      query: query ?? { skip: 0, take: 100 },
    });
  }
}

export class DocumentsClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  list<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    query?: Models.DocumentListQuery,
  ): Promise<TerraScaleResult<Models.DocumentPageResponse<TDocument>>> {
    return this.#http.request({
      method: "GET",
      path: `${repositoryRoot(scope)}/documents`,
      routeName: "documents.list",
      query,
      consistency: "read",
    });
  }

  create<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    data: TDocument,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.#http.request({
      method: "POST",
      path: `${repositoryRoot(scope)}/documents`,
      routeName: "documents.create",
      body: { data },
      consistency: "write",
    });
  }

  get<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    options?: Models.DocumentReadOptions,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.#http.request({
      method: "GET",
      path: `${repositoryRoot(scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.get",
      headers: documentReadHeaders(options),
      signal: options?.signal,
      consistency: "read",
    });
  }

  put<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    data: TDocument,
    options?: Models.DocumentMutationOptions,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.#http.request({
      method: "PUT",
      path: `${repositoryRoot(scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.put",
      body: { data },
      headers: documentMutationHeaders(options),
      signal: options?.signal,
      consistency: "write",
    });
  }

  patch<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    data: TDocument,
    options?: Models.DocumentMutationOptions,
  ): Promise<TerraScaleResult<Models.DocumentResponse<TDocument>>> {
    return this.#http.request({
      method: "PATCH",
      path: `${repositoryRoot(scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.patch",
      body: { data },
      headers: documentMutationHeaders(options),
      signal: options?.signal,
      consistency: "write",
    });
  }

  batch<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    request: Models.DocumentBatchRequest<TDocument>,
  ): Promise<TerraScaleResult<Models.DocumentBatchResponse<TDocument>>> {
    return this.#http.request({
      method: "POST",
      path: `${repositoryRoot(scope)}/documents/batch`,
      routeName: "documents.batch",
      body: request,
      consistency: "write",
    });
  }

  delete(
    scope: Models.RepositoryScoped,
    documentId: Ulid,
    options?: Models.DocumentMutationOptions,
  ): Promise<TerraScaleResult<Models.DeletedDocumentResponse>> {
    return this.#http.request({
      method: "DELETE",
      path: `${repositoryRoot(scope)}/documents/${ulidPathSegment(documentId)}`,
      routeName: "documents.delete",
      headers: documentMutationHeaders(options),
      signal: options?.signal,
      consistency: "write",
    });
  }
}

export class QueryClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  execute<TDocument extends Models.JsonValue = Models.JsonObject>(
    scope: Models.RepositoryScoped,
    request: Models.QueryRequest,
  ): Promise<TerraScaleResult<Models.DocumentPageResponse<TDocument>>> {
    return this.#http.request({
      method: "POST",
      path: `${repositoryRoot(scope)}/tql`,
      routeName: "query.execute",
      body: request,
      consistency: "read",
    });
  }
}

export class SchemaClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  get(scope: Models.RepositoryScoped): Promise<TerraScaleResult<Models.RepositorySchema>> {
    return this.#http.request({
      method: "GET",
      path: `${repositoryRoot(scope)}/schema`,
      routeName: "schema.get",
    });
  }

  validate(
    scope: Models.RepositoryScoped,
    request: Models.ValidateRepositorySchemaRequest = {},
  ): Promise<TerraScaleResult<Models.RepositorySchemaValidationResponse>> {
    return this.#http.request({
      method: "POST",
      path: `${repositoryRoot(scope)}/schema/validate`,
      routeName: "schema.validate",
      body: request,
    });
  }

  configure(
    scope: Models.RepositoryScoped,
    request: Models.ConfigureRepositorySchemaRequest,
  ): Promise<TerraScaleResult<Models.RepositorySchema>> {
    return this.#http.request({
      method: "PUT",
      path: `${repositoryRoot(scope)}/schema`,
      routeName: "schema.configure",
      body: request,
    });
  }
}

export class AccessClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  listTeamMembers(
    projectId?: Ulid,
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
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.TeamMember>> {
    return this.#http.request({
      method: "POST",
      path: `${projectRoot(this.#http, projectId)}/access/team-members`,
      routeName: "access.teamMembers.add",
      body: request,
    });
  }

  updateTeamMember(
    userId: Ulid,
    request: Models.UpdateTeamMemberRequest,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.TeamMember>> {
    return this.#http.request({
      method: "PATCH",
      path: `${projectRoot(this.#http, projectId)}/access/team-members/${ulidPathSegment(userId)}`,
      routeName: "access.teamMembers.update",
      body: request,
    });
  }

  removeTeamMember(userId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `${projectRoot(this.#http, projectId)}/access/team-members/${ulidPathSegment(userId)}`,
      routeName: "access.teamMembers.remove",
    });
  }
}

export class UsageClient {
  readonly #http: TerraScaleHttp;

  constructor(http: TerraScaleHttp) {
    this.#http = http;
  }

  getProjectUsage(projectId?: Ulid): Promise<TerraScaleResult<Models.UsageSummary>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/usage`,
      routeName: "usage.project.get",
    });
  }

  getOrganizationUsage(
    organizationId: Ulid,
  ): Promise<TerraScaleResult<Models.OrganizationUsage>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/organizations/${ulidPathSegment(organizationId)}/usage`,
      routeName: "usage.organization.get",
    });
  }

  getProjectUsageWorkload(
    range: Models.UsageWorkloadRange,
    projectId?: Ulid,
  ): Promise<TerraScaleResult<Models.UsageWorkload>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/usage/workload`,
      routeName: "usage.project.workload",
      query: { range },
    });
  }

  getProjectLatency(
    projectId?: Ulid,
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
    projectId?: Ulid,
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
    projectId?: Ulid,
    query?: Models.PageRequest,
  ): Promise<TerraScaleResult<Models.Page<Models.AuditEvent>>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/audit`,
      routeName: "audit.list",
      query,
    });
  }

  get(auditEventId: Ulid, projectId?: Ulid): Promise<TerraScaleResult<Models.AuditEvent>> {
    return this.#http.request({
      method: "GET",
      path: `${projectRoot(this.#http, projectId)}/audit/${ulidPathSegment(auditEventId)}`,
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
      readonly projectId?: Ulid;
      readonly organizationId?: Ulid;
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

  markRead(notificationId: Ulid): Promise<TerraScaleResult<Models.Notification>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/users/me/notifications/${ulidPathSegment(notificationId)}:mark-read`,
      routeName: "notifications.markRead",
    });
  }

  markAllRead(query?: {
    readonly organizationId?: Ulid;
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

  getUser(userId: Ulid): Promise<TerraScaleResult<Models.AdminUser>> {
    return this.#http.request({
      method: "GET",
      path: `${apiRoot}/admin/users/${ulidPathSegment(userId)}`,
      routeName: "admin.users.get",
    });
  }

  setUserEnabled(userId: Ulid, enabled: boolean): Promise<TerraScaleResult<Models.AdminUser>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/admin/users/${ulidPathSegment(userId)}:${enabled ? "enable" : "disable"}`,
      routeName: enabled ? "admin.users.enable" : "admin.users.disable",
    });
  }

  deleteUser(userId: Ulid): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "DELETE",
      path: `${apiRoot}/admin/users/${ulidPathSegment(userId)}`,
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

function databaseRoot(http: TerraScaleHttp, scope: Models.DatabaseScoped): string {
  return `${projectRoot(http, scope.projectId)}/databases/${ulidPathSegment(scope.databaseId)}`;
}

function queueRoot(http: TerraScaleHttp, queueId: Ulid, projectId?: Ulid): string {
  return `${projectRoot(http, projectId)}/queues/${ulidPathSegment(queueId)}`;
}

function preferredRegionHeaders(region: TerraScaleRegionCode): HeadersInit {
  return { "x-terrascale-preferred-region": parseRegionCode(region, "receipt.region") };
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
  if (response.regions.length > 20) {
    throw new TypeError("response.regions must contain at most 20 regions.");
  }

  const regions = response.regions.map((region, index) =>
    decodeRegionSummary(region, `response.regions[${index}]`));
  const uniqueCodes = new Set(regions.map((region) => region.code));
  if (uniqueCodes.size !== regions.length) {
    throw new TypeError("response.regions must not contain duplicate RegionCode values.");
  }

  return { regions };
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
  const country = requireString(region.country, `${path}.country`);
  const latitude = requireFiniteNumber(region.latitude, `${path}.latitude`);
  const longitude = requireFiniteNumber(region.longitude, `${path}.longitude`);
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

function documentReadHeaders(options: Models.DocumentReadOptions | undefined): HeadersInit | undefined {
  return options?.allowStale === true ? { "x-terrascale-allow-stale": "true" } : undefined;
}

function documentMutationHeaders(
  options: Models.DocumentMutationOptions | undefined,
): HeadersInit | undefined {
  const token = options?.expectedChangeToken?.trim();
  if (token === undefined) {
    return undefined;
  }

  if (token.length === 0 || token.length > 4096) {
    throw new TypeError("expectedChangeToken must be nonblank and contain at most 4096 characters.");
  }

  return { "if-match": token };
}

function environmentRoot(http: TerraScaleHttp, scope: Models.EnvironmentScoped): string {
  return `${databaseRoot(http, scope)}/environments/${ulidPathSegment(scope.environmentId)}`;
}

function repositoryRoot(scope: Models.RepositoryScoped): string {
  return `${apiRoot}/repositories/${ulidPathSegment(scope.repositoryId)}`;
}
