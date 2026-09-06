import type { TerraScaleRegion, TerraScaleRegionCode } from "./regions.js";

/** Structural brands remain available for generated schema IDs. */
declare const terraScaleBrand: unique symbol;
export type Brand<TValue, TBrand extends string> = TValue & { readonly [terraScaleBrand]?: TBrand };

/** Public API resource identifiers are opaque strings, not ULID wrappers. */
export type OrganizationId = string;
export type ProjectId = string;
export type DatabaseId = string;
export type UserId = string;
export type InvitationId = string;
export type BackupId = string;
export type BackupPolicyId = string;
export type NotificationId = string;
export type AuditEventId = string;
export type RestoreId = string;

export type Timestamp = string;
export type JsonPrimitive = boolean | null | number | string;
export type JsonValue = JsonPrimitive | JsonObject | readonly JsonValue[];
export type JsonObject = { readonly [key: string]: JsonValue };

/** Document reads preserve integer literals outside JavaScript's safe range as bigint. */
export type DocumentValue = JsonPrimitive | bigint | { readonly [key: string]: DocumentValue } | readonly DocumentValue[];
export type SqlValue = DocumentValue;

export type PageRequest = {
  readonly skip?: number;
  readonly take?: number;
};

export type RegionSummary = {
  readonly code: TerraScaleRegionCode;
  readonly name: string;
  readonly country: string;
  readonly latitude: number;
  readonly longitude: number;
  readonly available: boolean;
};

export type RegionDetails = RegionSummary;

/** Serving member observation published by the direct TerraBase API. */
export type MemberRegionStatus = {
  readonly region: string;
  readonly healthy: boolean;
};

export type RegionsResponse = {
  readonly regions: readonly RegionSummary[];
};

export type Page<TItem> = {
  readonly items: readonly TItem[];
  readonly totalCount?: number;
  readonly nextCursor?: string | null;
};

export type ProjectScoped = {
  readonly projectId?: ProjectId | undefined;
};

export type DatabaseScoped = ProjectScoped & {
  readonly databaseId: DatabaseId;
};

export type Organization = {
  readonly organizationId: OrganizationId;
  readonly name: string;
  readonly ownerUserIds: readonly UserId[];
  readonly createdByUserId?: UserId | null;
  readonly status: OrganizationStatus;
  readonly deletionRequestedAt?: Timestamp | null;
  readonly deletionRequestedByUserId?: UserId | null;
  readonly createdAt: Timestamp;
};

export type OrganizationStatus = "Active" | "DeletionPending";

export type OrganizationMemberRole = "Member" | "OrganizationOwner";

export type OrganizationMember = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName: string;
  readonly role: OrganizationMemberRole;
  readonly joinedAt: Timestamp;
};

export type CreateOrganizationRequest = {
  readonly name: string;
};

export type UpdateOrganizationRequest = {
  readonly name: string;
};

export type RequestOrganizationDeletionRequest = {
  readonly confirm: true;
  readonly organizationName: string;
};

export type UpdateOrganizationMemberRoleRequest = {
  readonly role: OrganizationMemberRole;
};

export type RemoveOrganizationMemberRequest = {
  readonly confirm: true;
};

export type Project = {
  readonly projectId: ProjectId;
  readonly organizationId: OrganizationId;
  readonly name: string;
  readonly ownerUserIds: readonly UserId[];
  readonly regions: readonly TerraScaleRegionCode[];
  readonly backupRegion: TerraScaleRegionCode;
  readonly placementStatus: ProjectPlacementStatus;
  readonly placementGeneration: number;
  readonly slug?: string;
  readonly createdAt: Timestamp;
};

export type ProjectPlacementStatus =
  | "Provisioning"
  | "PartiallyReady"
  | "Ready"
  | "Updating"
  | "Degraded"
  | "Deleting"
  | "Failed";

export type ProjectRegionPlacement = {
  readonly region: TerraScaleRegionCode;
  readonly placementGeneration: number;
  readonly status: string;
  readonly availability: "available" | "unavailable" | "unknown";
  readonly isBackupRegion: boolean;
  readonly replicationLagMilliseconds?: number | null;
  readonly error?: string | null;
  readonly updatedAt: Timestamp | null;
};

export type CreateProjectRequest = {
  readonly name: string;
  readonly backupRegion: TerraScaleRegionCode;
  readonly defaultPermissionKeys?: readonly string[];
  readonly regions: readonly TerraScaleRegionCode[];
  readonly slug?: string;
};

export type OnboardingStep = "organization" | "project" | "invitation" | "complete";

export type OnboardingJourneyType = "developer" | "invitation";

export type OnboardingStatus = "not-started" | "in-progress" | "ready-for-project" | "completed";

export type OnboardingStepState = {
  readonly key: Exclude<OnboardingStep, "complete">;
  readonly state: "pending" | "current" | "completed" | "blocked";
};

export type OnboardingState = {
  readonly userId: UserId;
  readonly email: string;
  readonly journeyType: OnboardingJourneyType;
  readonly locale: string;
  readonly invitationId?: string | null;
  readonly invitationOrganizationId?: OrganizationId | null;
  readonly invitationProjectId?: ProjectId | null;
  readonly invitationAccepted: boolean;
  readonly organizationId?: OrganizationId | null;
  readonly organizationOwnerAssigned: boolean;
  readonly projectId?: ProjectId | null;
  readonly projectOwnerAssigned: boolean;
  readonly projectPrimaryRegions: readonly TerraScaleRegionCode[];
  readonly projectBackupRegion: TerraScaleRegionCode | "";
  readonly completed: boolean;
  readonly status: OnboardingStatus;
  readonly nextStep: OnboardingStep;
  readonly steps: readonly OnboardingStepState[];
  readonly updatedAt: Timestamp;
};

export type CreateOnboardingOrganizationRequest = {
  readonly name: string;
};

export type CreateOnboardingProjectRequest = {
  readonly name: string;
  readonly regions: readonly TerraScaleRegionCode[];
  readonly backupRegion: TerraScaleRegionCode;
};

export type UpdateProjectRequest = Partial<CreateProjectRequest> & {
  readonly placementGeneration?: number;
};

export type DeleteProjectRequest = {
  readonly confirm: true;
  readonly projectName: string;
};

export type DeletedProject = {
  readonly projectId: ProjectId;
  readonly organizationId: OrganizationId;
  readonly name: string;
  readonly deleted: boolean;
  readonly databasesDeleted: number;
  readonly physicalStorageDeleted: boolean;
};

export type Invitation = {
  readonly invitationId: InvitationId;
  readonly organizationId: OrganizationId;
  readonly projectId?: ProjectId | null;
  readonly email: string;
  readonly role: "Member";
  readonly status: InvitationStatus;
  readonly invitedByEmail?: string;
  readonly expiresAt: Timestamp;
  readonly acceptedAt?: Timestamp | null;
  readonly acceptedByUserId?: UserId | null;
  readonly declinedAt?: Timestamp | null;
  readonly canceledAt?: Timestamp | null;
  readonly lastSentAt: Timestamp;
  readonly sentCount: number;
  readonly createdAt?: Timestamp;
};

export type InvitationStatus = "Accepted" | "Canceled" | "Declined" | "Expired" | "Pending";

export type CreateInvitationRequest = {
  readonly email: string;
  readonly projectId?: ProjectId;
  readonly responseUrlTemplate: string;
};

export type ProjectDatabase = {
  readonly databaseId: DatabaseId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly regions: readonly TerraScaleRegionCode[];
  readonly backupRegion: TerraScaleRegionCode;
  readonly createdAt: Timestamp;
  readonly version: number;
};

export type CreateDatabaseRequest = {
  readonly name: string;
};

export type DatabaseBranchCapabilityResponse = {
  readonly projectId: ProjectId;
  readonly admissionState: string;
  readonly physicalCloneAvailable: boolean;
  readonly promotionAvailable: boolean;
  readonly mergeAvailable: boolean;
  readonly detail: string;
};

export type CreateDatabaseBranchRequest = {
  readonly sourceDatabaseId?: DatabaseId;
};

export type DeleteProjectDatabaseOptions = {
  readonly confirm: true;
  readonly projectId?: ProjectId | undefined;
};

export type ProjectSavedQueryRequest = {
  readonly name: string;
  readonly query: string;
};

export type ProjectSavedQueryResponse = {
  readonly savedQueryId: string;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly query: string;
  readonly createdAt: Timestamp;
  readonly updatedAt: Timestamp;
  /** Opaque resource-bound revision used for conditional updates. */
  readonly revision: string;
};

export type UpdateProjectSavedQueryRequest = ProjectSavedQueryRequest & {
  readonly revision: string;
};

export type ProjectSavedQueryPageRequest = {
  /** Continuation offset returned by the previous page. */
  readonly cursor?: string;
  readonly skip?: number;
  readonly take?: number;
};

export type ProjectSavedQueryPageResponse = {
  readonly items: readonly ProjectSavedQueryResponse[];
  readonly totalCount: number;
  readonly nextCursor: string | null;
};

export type DeletedProjectSavedQueryResponse = {
  readonly savedQueryId: string;
  readonly projectId: ProjectId;
  readonly deleted: boolean;
};

export type DeletedProjectDatabaseResponse = {
  readonly databaseId: DatabaseId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly deleted: boolean;
  readonly physicalDatabasesDeleted: number;
};

export type ConfigureBackupPolicyRequest = {
  readonly fullBackupCron: string;
  readonly incrementalBackupCron?: string | null;
  readonly retentionDays?: number;
  readonly disabled?: boolean;
};

export type BackupPolicyResponse = {
  readonly backupPolicyId: BackupPolicyId;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly databaseName: string;
  readonly fullBackupCron: string;
  readonly incrementalBackupCron?: string | null;
  readonly retentionDays: number;
  readonly disabled: boolean;
  readonly destinationType: string;
  readonly status: string;
  readonly createdAt: Timestamp;
  readonly updatedAt: Timestamp;
};

export type DatabaseBackupConfirmationPlanRequest = {
  readonly operationSessionId: string;
};

export type DatabaseBackupConfirmationPlanResponse = {
  readonly action: string;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly databaseName: string;
  readonly requestDigest: string;
  readonly confirmationToken: string;
  readonly expiresAt: Timestamp;
};

export type DatabaseBackupConfirmationExecuteRequest = {
  readonly operationSessionId: string;
  readonly confirmationToken: string;
};

export type BackupJobResponse = {
  readonly backupId: BackupId;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly databaseName: string;
  readonly policy: string;
  readonly retention: string;
  readonly status: string;
  readonly destinationType: string;
  readonly hasRestorePoint: boolean;
  readonly requestedBy: string;
  readonly requestedAt: Timestamp;
  readonly updatedAt: Timestamp;
  readonly completedAt?: Timestamp | null;
  readonly errorMessage?: string | null;
};

export type BackupListQuery = PageRequest;

export type RestorePointResponse = {
  readonly name: string;
  readonly backupId?: BackupId | null;
  readonly createdAt: Timestamp;
  readonly isSnapshotRestore: boolean;
  readonly isEncrypted: boolean;
  readonly isIncremental: boolean;
  readonly filesToRestore: number;
};

export type StartRestoreRequest = {
  readonly backupId: BackupId;
  readonly idempotencyKey?: string | null;
};

export type RestoreJobResponse = {
  readonly restoreId: RestoreId;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly backupId: BackupId;
  readonly restoredDatabaseId: DatabaseId;
  readonly restoredDatabaseName: string;
  readonly status: string;
  readonly requestedBy: string;
  readonly requestedAt: Timestamp;
  readonly updatedAt: Timestamp;
  readonly completedAt?: Timestamp | null;
  readonly errorMessage?: string | null;
};

export type RestoreListQuery = PageRequest;

export type AccessRole = "member" | "owner" | "viewer";

export type TeamMember = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName: string;
  readonly role: AccessRole;
  readonly joinedAt: Timestamp | null;
};

export type AddTeamMemberRequest = {
  readonly userId: UserId;
  readonly role: AccessRole;
};

export type UpdateTeamMemberRequest = {
  readonly role: AccessRole;
};

export type ProjectUsageSourceStatus = {
  readonly source: string;
  readonly available: boolean;
  readonly provisional: boolean;
  readonly detail?: string | null;
};

export type UsageSummary = {
  readonly projectId: ProjectId;
  readonly periodStart: Timestamp;
  readonly periodEnd: Timestamp;
  readonly metrics: readonly UsageMetric[];
  readonly kpis: readonly UsageKpi[];
  readonly resourceDistribution: readonly UsageSlice[];
  readonly operationBreakdown: readonly UsageSlice[];
  readonly databaseStorage: readonly UsageDatabaseStorage[];
  readonly storage: UsageStorage;
  readonly operationUnits: number;
  readonly includedOperationUnits: number;
  readonly overageOperationUnits: number;
  readonly egressBytes: number;
  readonly updatedAt: Timestamp;
  readonly regionalTotals: Readonly<Record<string, number>>;
  readonly categoryTotals: Readonly<Record<string, number>>;
  readonly provisional: boolean;
  readonly sources: readonly ProjectUsageSourceStatus[];
};

export type OrganizationUsageLimit = {
  readonly resource: "storage" | "database_operations" | "egress" | string;
  readonly unit: string;
  readonly used: string;
  readonly included?: string | null;
  readonly remaining?: string | null;
  readonly excess?: string | null;
  readonly status: "approaching" | "exceeded" | "normal" | "not_applicable" | "reached" | string;
  readonly provisional: boolean;
};

export type OrganizationProjectUsage = {
  readonly projectId: ProjectId;
  readonly projectName: string;
  readonly storageBytes?: string | null;
  readonly operationUnits?: string | null;
  readonly egressBytes?: string | null;
  readonly updatedAt?: Timestamp | null;
  readonly provisional: boolean;
};

export type OrganizationUsage = {
  readonly organizationId: OrganizationId;
  readonly periodStart: Timestamp;
  readonly periodEnd: Timestamp;
  readonly updatedAt: Timestamp;
  readonly provisional: boolean;
  readonly resources: readonly OrganizationUsageLimit[];
  readonly projects: readonly OrganizationProjectUsage[];
};

export type UsageMetric = {
  readonly name: string;
  readonly quantity: number;
  readonly unit: string;
};

export type UsageKpi = {
  readonly name: string;
  readonly value: number;
  readonly formattedValue: string;
  readonly detail: string;
  readonly status: string;
};

export type UsageSlice = {
  readonly name: string;
  readonly quantity: number;
};

export type UsageDatabaseStorage = {
  readonly databaseId: DatabaseId;
  readonly name: string;
  readonly documents: number;
  readonly storageBytes: number;
};

export type UsageStorage = {
  readonly usedBytes: number;
  readonly limitBytes: number;
  readonly percentUsed: number;
  readonly status: string;
};

export type UsageWorkloadRange = "24h" | "7d" | "30d";

export type UsageWorkload = {
  readonly projectId: ProjectId;
  readonly range: UsageWorkloadRange;
  readonly periodStart: Timestamp;
  readonly periodEnd: Timestamp;
  readonly bucketSize: string;
  readonly points: readonly UsageWorkloadPoint[];
};

export type UsageWorkloadPoint = {
  readonly start: Timestamp;
  readonly end: Timestamp;
  readonly label: string;
  readonly value: number;
};

export type ProjectRegionLatency = {
  readonly fromRegion: string;
  readonly toRegion: string;
  readonly estimatedRoundTripMilliseconds: number;
};

export type ProjectNearestRegionLatency = {
  readonly region: string;
  readonly estimatedRoundTripMilliseconds: number;
};

export type ProjectLatency = {
  readonly projectId: ProjectId;
  readonly capturedAt: Timestamp;
  readonly referencePeriodStart: Timestamp;
  readonly referencePeriodEnd: Timestamp;
  readonly source: "geographic_estimate" | string;
  readonly interRegion: readonly ProjectRegionLatency[];
  readonly nearestRegion?: ProjectNearestRegionLatency | null;
};

export type ProjectLatencyRequest = {
  readonly latitude?: number;
  readonly longitude?: number;
};

export type UsageHistoryRange = "24h" | "7d" | "30d" | "90d" | "365d";

export type ProjectUsageHistoryPoint = {
  readonly observedAt: Timestamp;
  readonly storageBytes: string;
  readonly operationUnits: string;
  readonly egressBytes: string;
};

export type ProjectUsageHistory = {
  readonly projectId: ProjectId;
  readonly range: UsageHistoryRange;
  readonly requestedStart: Timestamp;
  readonly accessibleStart: Timestamp;
  readonly end: Timestamp;
  readonly limitedByRetention: boolean;
  readonly hasData: boolean;
  readonly points: readonly ProjectUsageHistoryPoint[];
};

export type AuditEvent = {
  readonly auditEventId: AuditEventId;
  readonly projectId?: ProjectId;
  readonly actorUserId?: UserId;
  readonly action: string;
  readonly targetType: string;
  readonly targetId?: string;
  readonly occurredAt: Timestamp;
  readonly metadata?: JsonObject;
};

export type Notification = {
  readonly notificationId: NotificationId;
  readonly title: string;
  readonly body?: string;
  readonly severity: "critical" | "info" | "warning";
  readonly readAt?: Timestamp;
  readonly createdAt: Timestamp;
  readonly category: string;
  readonly organizationId?: OrganizationId | null;
  readonly projectId?: ProjectId | null;
};

export type MarkAllNotificationsReadResponse = {
  readonly markedRead: number;
};

export type AdminUser = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName?: string;
  readonly enabled: boolean;
  readonly owner: boolean;
  readonly organizationIds: readonly OrganizationId[];
  readonly createdAt: Timestamp;
};

export type AdminSupportNote = {
  readonly supportNoteId: string;
  readonly userId?: UserId;
  readonly organizationId?: OrganizationId;
  readonly projectId?: ProjectId;
  readonly note: string;
  readonly createdAt: Timestamp;
};

export type AdminCreateSupportNoteRequest = Omit<AdminSupportNote, "createdAt" | "supportNoteId">;

export type AdminPlatformUsage = {
  readonly organizations: number;
  readonly projects: number;
  readonly databases: number;
  readonly regions: readonly TerraScaleRegion[];
  readonly usage: readonly UsageMetric[];
};
