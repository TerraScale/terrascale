import type {
  AuditEventId,
  BackupId,
  BackupPolicyId,
  DatabaseId,
  DocumentId,
  EnvironmentId,
  InvitationId,
  NotificationId,
  OrganizationId,
  ProjectId,
  QueueId,
  RepositoryId,
  RestoreId,
  Ulid,
  UserId,
} from "./ids.js";
import type { TerraScaleRegion, TerraScaleRegionCode } from "./regions.js";

export type Timestamp = string;
export type JsonPrimitive = boolean | null | number | string;
export type JsonValue = JsonPrimitive | JsonObject | readonly JsonValue[];
export type JsonObject = { readonly [key: string]: JsonValue };

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

export type RegionsResponse = {
  readonly regions: readonly RegionSummary[];
};

export type Page<TItem> = {
  readonly items: readonly TItem[];
  readonly totalCount?: number;
  readonly nextCursor?: string | null;
};

export type Int64String = `${bigint}`;

export type ProjectScoped = {
  readonly projectId?: ProjectId | undefined;
};

export type DatabaseScoped = ProjectScoped & {
  readonly databaseId: DatabaseId;
};

export type EnvironmentScoped = DatabaseScoped & {
  readonly environmentId: EnvironmentId;
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
  readonly invitationId?: Ulid | null;
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
  readonly queuesDeleted: number;
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

export type EnvironmentKind = "Custom" | "Dev" | "Preview" | "Prod";
export type EnvironmentStatus =
  | "Active"
  | "Deleting"
  | "Deleted"
  | "Expired"
  | "Merged"
  | "Merging";
export type EnvironmentDeletionStatus = "Completed" | "Requested" | "RetryScheduled";

export type ProjectDatabase = {
  readonly databaseId: DatabaseId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly regions: readonly TerraScaleRegionCode[];
  readonly backupRegion: TerraScaleRegionCode;
  readonly createdAt: Timestamp;
  readonly version: number;
};

export type DatabaseEnvironment = {
  readonly environmentId: EnvironmentId;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly name: string;
  readonly kind: EnvironmentKind;
  readonly status: EnvironmentStatus;
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
  readonly repositoryId?: RepositoryId | null;
};

export type ProjectSavedQueryResponse = {
  readonly savedQueryId: Ulid;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly query: string;
  readonly repositoryId: RepositoryId | null;
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
  readonly savedQueryId: Ulid;
  readonly projectId: ProjectId;
  readonly deleted: boolean;
};

export type DeletedProjectDatabaseResponse = {
  readonly databaseId: DatabaseId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly deleted: boolean;
  readonly environmentsDeleted: number;
  readonly physicalDatabasesDeleted: number;
  readonly repositoriesDeleted: number;
};

export type ProjectQueue = {
  readonly queueId: QueueId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly kind: "Queue" | "Topic";
  readonly messages: number;
  readonly storageBytes: number;
  readonly consumerCount: number;
  readonly replicas: number;
  readonly fifo: boolean;
  readonly partitionCount: number;
  readonly retentionSeconds: number | null;
  readonly createdAt: Timestamp;
};

export type CreateProjectQueueRequest = {
  readonly name: string;
  readonly kind: "queue" | "topic";
  readonly description?: string;
  readonly retentionSeconds?: number;
  readonly maxMessageSizeBytes?: number;
  readonly fifo?: boolean;
  readonly partitionCount?: number;
  readonly duplicateDetectionSeconds?: number;
};

export type PublishProjectQueueMessageRequest = {
  readonly data: JsonValue;
  readonly idempotencyKey?: string;
  readonly partitionKey?: string;
  readonly delaySeconds?: number;
  readonly timeToLiveSeconds?: number;
};

export type ProjectQueuePublishResult = {
  readonly messageId: string;
  readonly sequence: number;
  readonly publishedAt: Timestamp;
  readonly duplicate: boolean;
};

export type ProjectQueuePeekMessage = {
  readonly messageId: string;
  readonly data: JsonValue;
  readonly contentType: string;
  readonly headers: Readonly<Record<string, string>>;
  readonly sequence: number;
  readonly createdAt: Timestamp | null;
};

export type ProjectQueueDeliveryGuarantee = "at-least-once";

export type ReceiveProjectQueueMessagesRequest = {
  readonly consumerName: string;
  readonly maxMessages?: number;
  readonly waitSeconds?: number;
  readonly lockSeconds?: number;
};

export type ProjectQueueReceipt = {
  readonly receipt: string;
  readonly region: TerraScaleRegionCode;
};

export type ProjectQueueReceivedMessage = ProjectQueueReceipt & {
  readonly messageId: string;
  readonly data: JsonValue;
  readonly contentType: string;
  readonly headers: Readonly<Record<string, string>>;
  readonly streamSequence: number;
  readonly consumerSequence: number;
  readonly deliveryAttempt: number;
  readonly receivedAt: Timestamp;
  readonly lockedUntil: Timestamp;
  readonly deliveryGuarantee: ProjectQueueDeliveryGuarantee;
};

export type ProjectQueueReceiptActionResult = {
  readonly messageId: string;
  readonly operation: "abandon" | "complete" | "dead-letter";
  readonly accepted: boolean;
  readonly safeToRetry: boolean;
  readonly region: TerraScaleRegionCode;
  readonly deliveryGuarantee: ProjectQueueDeliveryGuarantee;
};

export type ProjectQueueRenewLockResult = ProjectQueueReceipt & {
  readonly messageId: string;
  readonly lockedUntil: Timestamp;
  readonly deliveryGuarantee: ProjectQueueDeliveryGuarantee;
};

export type QueuePurgeResult = {
  readonly queueName: string;
  readonly purgedMessages: number;
};

export type CreateDatabaseEnvironmentRequest = {
  readonly name: string;
  readonly kind?: EnvironmentKind;
};

export type DeletedProjectDatabaseEnvironmentResponse = {
  readonly environmentId: EnvironmentId;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly name: string;
  readonly deleted: boolean;
  readonly physicalDatabaseDeleted: boolean;
  readonly repositoriesDeleted: number;
  readonly operationId?: Ulid;
  readonly status?: EnvironmentDeletionStatus;
  readonly retryAt?: Timestamp | null;
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

export type RepositoryScoped = {
  readonly repositoryId: RepositoryId;
};

export type RepositoryDefinition = {
  readonly repositoryId: RepositoryId;
  readonly name: string;
  readonly databaseId: DatabaseId;
  readonly environmentId?: EnvironmentId | null;
  readonly collection: string;
  readonly schemaValidationEnabled: boolean;
  readonly createdAt: Timestamp;
  readonly updatedAt: Timestamp;
};

export type CreateRepositoryRequest = {
  readonly name: string;
  readonly databaseId?: DatabaseId;
  readonly environmentId?: EnvironmentId;
  readonly collection?: string;
  readonly schemaValidationEnabled?: boolean;
  readonly schema?: string;
};

export type DeletedRepositoryResponse = {
  readonly repositoryId: RepositoryId;
  readonly name: string;
  readonly deleted: boolean;
  readonly documentsDeleted: number;
};

export type DocumentResponse<TDocument extends JsonValue = JsonObject> = {
  readonly repositoryId: RepositoryId;
  readonly repository: string;
  readonly id: DocumentId;
  readonly data: TDocument;
  readonly lastModified?: Timestamp | null;
  readonly changeToken?: string | null;
  readonly sessionConsistencyToken?: string | null;
};

export type DocumentReadOptions = {
  readonly allowStale?: boolean;
  readonly signal?: AbortSignal;
};

export type DocumentMutationOptions = {
  readonly expectedChangeToken?: string;
  readonly signal?: AbortSignal;
};

export type DocumentPageResponse<TDocument extends JsonValue = JsonObject> = {
  readonly repositoryId: RepositoryId;
  readonly repository: string;
  readonly skip: number;
  readonly take: number;
  readonly total: number;
  readonly items: readonly DocumentResponse<TDocument>[];
};

export type DocumentListQuery = PageRequest;

export type DocumentBatchOperationRequest<TDocument extends JsonValue = JsonObject> = {
  readonly operation: "create" | "delete" | "patch" | "upsert" | string;
  readonly documentId?: DocumentId;
  readonly data?: TDocument;
};

export type DocumentBatchRequest<TDocument extends JsonValue = JsonObject> = {
  readonly operations: readonly DocumentBatchOperationRequest<TDocument>[];
};

export type DocumentBatchOperationResponse<TDocument extends JsonValue = JsonObject> = {
  readonly index: number;
  readonly operation: string;
  readonly id: DocumentId;
  readonly document?: DocumentResponse<TDocument> | null;
  readonly deleted?: DeletedDocumentResponse | null;
};

export type DeletedDocumentResponse = {
  readonly repositoryId: RepositoryId;
  readonly repository: string;
  readonly id: DocumentId;
  readonly deleted: boolean;
  readonly sessionConsistencyToken?: string | null;
};

/** The durability classes published by the Database API document contract. */
export type DatabaseDocumentDurability = "regional" | "global";

/** The two path dimensions shared by the Database API document operations. */
export type DatabaseDocumentScope = {
  readonly database: string;
  readonly collection: string;
};

/** Options shared by Database API document mutations. */
export type DatabaseDocumentMutationOptions = {
  /** A canonical uppercase ULID reused for every retry of this logical mutation. */
  readonly idempotencyKey: Ulid;
  readonly signal?: AbortSignal | undefined;
};

export type DatabaseDocumentDeleteOptions = DatabaseDocumentMutationOptions & {
  readonly durability?: DatabaseDocumentDurability | undefined;
};

export type DatabaseDocumentWriteRequest<
  TDocument extends JsonValue = JsonValue,
> = {
  readonly body: TDocument;
  readonly durability?: DatabaseDocumentDurability | undefined;
};

export type DatabaseDocumentReadOptions = {
  readonly signal?: AbortSignal | undefined;
};

/** The exact successful body returned by `documents.read`. */
export type DatabaseDocumentReadResponse<
  TDocument extends JsonValue = JsonValue,
> = {
  readonly database: string;
  readonly collection: string;
  readonly id: string;
  /** A gateway JSON integer. The client rejects values outside JavaScript's safe range. */
  readonly version: number;
  readonly body: TDocument;
  /** A gateway JSON integer. The client rejects values outside JavaScript's safe range. */
  readonly position: number | null;
};

/** The exact successful body returned by Database API document mutations. */
export type DatabaseDocumentWriteResponse = {
  readonly database: string;
  readonly collection: string;
  readonly id: string;
  /** A gateway JSON integer. The client rejects values outside JavaScript's safe range. */
  readonly version: number;
  /** A gateway JSON integer. The client rejects values outside JavaScript's safe range. */
  readonly sequence: number;
  readonly operationId: string;
  readonly acknowledgement: "regional" | "global" | "backup" | null;
  readonly backup: "none" | "full" | "partial" | null;
};

export type DocumentBatchResponse<TDocument extends JsonValue = JsonObject> = {
  readonly repositoryId: RepositoryId;
  readonly repository: string;
  readonly results: readonly DocumentBatchOperationResponse<TDocument>[];
};

export type QueryRequest = {
  readonly query: string;
  readonly skip?: number;
  readonly take?: number;
};

export type RepositorySchema = {
  readonly repositoryId: RepositoryId;
  readonly repository: string;
  readonly databaseId: DatabaseId;
  readonly environmentId?: EnvironmentId | null;
  readonly collection: string;
  readonly enabled: boolean;
  readonly schema: string;
};

export type ConfigureRepositorySchemaRequest = {
  readonly enabled?: boolean;
  readonly schema?: string | null;
};

export type ValidateRepositorySchemaRequest = {
  readonly maxErrorMessages?: number;
  readonly maxDocumentsToValidate?: number;
};

export type RepositorySchemaValidationResponse = {
  readonly repositoryId: RepositoryId;
  readonly repository: string;
  readonly databaseId: DatabaseId;
  readonly environmentId?: EnvironmentId | null;
  readonly collection: string;
  readonly operationId: Ulid;
};

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
  readonly used: Int64String;
  readonly included?: Int64String | null;
  readonly remaining?: Int64String | null;
  readonly excess?: Int64String | null;
  readonly status: "approaching" | "exceeded" | "normal" | "not_applicable" | "reached" | string;
  readonly provisional: boolean;
};

export type OrganizationProjectUsage = {
  readonly projectId: ProjectId;
  readonly projectName: string;
  readonly storageBytes?: Int64String | null;
  readonly operationUnits?: Int64String | null;
  readonly egressBytes?: Int64String | null;
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
  readonly storageBytes: Int64String;
  readonly operationUnits: Int64String;
  readonly egressBytes: Int64String;
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
  readonly targetId?: Ulid;
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
  readonly supportNoteId: Ulid;
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
