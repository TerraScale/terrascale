import type { ResolvedTerraScaleRuntimeConfig, TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
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
  readonly backups: BackupsClient;
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
    this.backups = new BackupsClient(this.#http);
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
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "PATCH",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}`,
      routeName: "organizations.update",
      body: request,
    });
  }

  requestDeletion(
    organizationId: string,
    request: Models.RequestOrganizationDeletionRequest,
  ): Promise<TerraScaleResult<Models.Organization>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}:request-deletion`,
      routeName: "organizations.deletion.request",
      body: request,
    });
  }

  leave(organizationId: string): Promise<TerraScaleResult<void>> {
    return this.#http.request({
      method: "POST",
      path: `${apiRoot}/organizations/${idPathSegment(organizationId)}:leave`,
      routeName: "organizations.leave",
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
  ): Promise<TerraScaleResult<Models.DeletedProject>> {
    return this.#http.request({
      method: "DELETE",
      path: projectRoot(this.#http, projectId),
      routeName: "organizations.projects.delete",
      body: request,
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

    return this.#http.request({
      method: "DELETE",
      path: databaseRoot(this.#http, { projectId: options.projectId, databaseId }),
      routeName: "databases.delete",
      headers: { "x-terrascale-delete-confirmation": databaseId },
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
