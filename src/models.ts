import type {
  ApiKeyId,
  AuditEventId,
  BackupId,
  BackupPolicyId,
  DatabaseId,
  DocumentId,
  EnvironmentId,
  InvitationId,
  M2MClientId,
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

export type RegionDetails = RegionSummary & {
  readonly ociRegionCode: string;
  readonly clusterNodeTag: string;
};

export type RegionsResponse = {
  readonly regions: readonly RegionSummary[];
};

export type Page<TItem> = {
  readonly items: readonly TItem[];
  readonly totalCount?: number;
  readonly nextCursor?: string | null;
};

/** Exact signed 64-bit integer serialized as a base-10 JSON string. */
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

export type AuthMePreferences = {
  readonly theme: "dark" | "light" | "system" | string;
  readonly timezone: string;
  readonly emailNotifications: boolean;
  readonly language: string;
  readonly defaultRegion?: string;
  readonly notificationsEnabled?: boolean;
};

export type AuthMeResponse = {
  readonly userId: UserId;
  readonly email: string;
  readonly name: string;
  readonly phoneNumber: string;
  readonly avatarUrl: string;
  readonly personalOrgId: OrganizationId;
  readonly activeOrgId: OrganizationId;
  readonly currentOrgId?: OrganizationId;
  readonly permissions: readonly string[];
  readonly roles: readonly string[];
  readonly preferences?: AuthMePreferences;
};

export type AccountSession = {
  readonly sessionId: Ulid;
  readonly deviceType: string;
  readonly browser: string;
  readonly os: string;
  readonly ipAddress: string;
  readonly location: string;
  readonly createdAt?: Timestamp | null;
  readonly lastActiveAt?: Timestamp | null;
  readonly isCurrent: boolean;
};

export type ListAccountSessionsResponse = {
  readonly sessions: readonly AccountSession[];
  readonly nextCursor?: string;
};

export type AccountSecurityPageRequest = {
  readonly cursor?: string;
  readonly limit?: number;
};

export type TrustedDevice = {
  readonly contextKey: string;
  readonly deviceName: string;
  readonly deviceType: string;
  readonly browser: string;
  readonly os: string;
  readonly trusted: boolean;
  readonly lastSeenAt?: Timestamp | null;
  readonly firstSeenAt?: Timestamp | null;
};

export type ListTrustedDevicesResponse = {
  readonly devices: readonly TrustedDevice[];
  readonly nextCursor?: string | null;
};

export type TrustedIp = {
  readonly ipAddress: string;
  readonly description: string;
  readonly addedAt?: Timestamp | null;
};

export type AddTrustedIpRequest = {
  readonly ipAddress: string;
  readonly description?: string;
};

export type ListTrustedIpsResponse = {
  readonly ipAddresses: readonly TrustedIp[];
};

export type TrustedIpMutationResponse = {
  readonly ipAddress: string;
  readonly removed: boolean;
};

export type Passkey = {
  readonly credentialId: string;
  readonly deviceName: string;
  readonly credentialType: string;
  readonly registeredAt?: Timestamp | null;
  readonly lastUsedAt?: Timestamp | null;
};

export type ListPasskeysResponse = {
  readonly passkeys: readonly Passkey[];
};

export type GetPasskeyAuthenticationOptionsRequest = {
  readonly email?: string;
};

export type PasskeyOptionsResponse = {
  readonly optionsJson: string;
};

export type AuthenticateWithPasskeyRequest = {
  readonly assertionResponseJson: string;
};

export type CompletePasskeyRegistrationRequest = {
  readonly attestationResponseJson: string;
  readonly deviceName?: string;
};

export type UpdatePasskeyRequest = {
  readonly deviceName: string;
};

export type DeletePasskeyResponse = {
  readonly credentialId: string;
  readonly deleted: boolean;
};

export type RefreshToken = {
  readonly tokenId: Ulid;
  readonly deviceType: string;
  readonly browser: string;
  readonly ipAddress: string;
  readonly createdAt?: Timestamp | null;
  readonly expiresAt?: Timestamp | null;
  readonly isCurrent: boolean;
};

export type ListAccountTokensResponse = {
  readonly tokens: readonly RefreshToken[];
  readonly nextCursor?: string | null;
};

export type RevokeAllAccountTokensResponse = {
  readonly revokedCount: number;
};

export type AccountExportData = {
  readonly projectId: ProjectId;
  readonly userId: UserId;
  readonly email: string;
  readonly name: string;
  readonly roles: readonly string[];
  readonly sessions: readonly AccountSession[];
  readonly tokens: readonly RefreshToken[];
  readonly exportedAt?: Timestamp | null;
};

export type InitiateAccountDeletionRequest = {
  readonly password: string;
  readonly reason?: string;
};

export type AccountDeletionResponse = {
  readonly success: boolean;
  readonly message: string;
  readonly scheduledAt?: Timestamp | null;
};

export type AccountDeletionStatusResponse = {
  readonly pendingDeletion: boolean;
  readonly reason: string;
  readonly requestedAt?: Timestamp | null;
  readonly scheduledAt?: Timestamp | null;
};

export type LoginHistoryEntry = {
  readonly loginId: Ulid;
  readonly ipAddress: string;
  readonly location: string;
  readonly deviceType: string;
  readonly browser: string;
  readonly os: string;
  readonly success: boolean;
  readonly failureReason: string;
  readonly suspicious: boolean;
  readonly timestamp?: Timestamp | null;
};

export type ListLoginHistoryResponse = {
  readonly entries: readonly LoginHistoryEntry[];
  readonly nextCursor?: string | null;
};

export type UpdateProfileRequest = {
  readonly displayName: string;
  readonly phoneNumber: string;
};

export type UpdateProfileResponse = {
  readonly success: boolean;
  readonly name: string;
  readonly phoneNumber: string;
  readonly updatedAt?: Timestamp;
};

export type ChangePasswordRequest = {
  readonly currentPassword: string;
  readonly newPassword: string;
};

export type ChangePasswordResponse = {
  readonly success: boolean;
  readonly error?: string;
};

export type MfaFactor = {
  readonly id: Ulid;
  readonly type: string;
  readonly createdAt?: Timestamp | null;
};

export type ListMfaFactorsResponse = {
  readonly factors: readonly MfaFactor[];
};

export type EnrollTotpResponse = {
  readonly factorId: Ulid;
  readonly type: string;
  readonly qrCode: string;
  readonly secret: string;
  readonly challengeId: Ulid;
};

export type VerifyTotpEnrollmentRequest = {
  readonly challengeId: Ulid;
  readonly code: string;
};

export type VerifyTotpEnrollmentResponse = {
  readonly success: boolean;
  readonly error?: string;
  readonly accessToken?: string;
  readonly expiresIn?: number;
  readonly verifiedAt?: Timestamp;
};

export type DeleteMfaFactorRequest = {
  readonly currentPassword?: string;
  readonly code?: string;
};

export type GenerateRecoveryCodesResponse = {
  readonly codes: readonly string[];
  readonly generatedAt?: Timestamp;
};

export type RecoveryCodeCountResponse = {
  readonly count: number;
};

export type VerifyRecoveryCodeRequest = {
  readonly email: string;
  readonly code: string;
};

export type VerifyRecoveryCodeResponse = {
  readonly success: boolean;
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly expiresIn: number;
};

export type RegisterRequest = {
  readonly email: string;
  readonly password: string;
  readonly displayName?: string;
  readonly redirectUrl?: string;
  readonly firstName?: string;
  readonly lastName?: string;
  readonly verifyUrl?: string;
};

export type SignInRequest = {
  readonly email: string;
  readonly password: string;
  readonly deviceFingerprint?: string;
};

export type PasswordMfaVerifyRequest = {
  readonly pendingAuthenticationToken: string;
  readonly challengeId: Ulid;
  readonly code: string;
};

export type AuthTokenResponse = {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly tokenType: string;
  readonly expiresIn: number;
  readonly userId: UserId;
  readonly email: string;
  readonly name: string;
  readonly phoneNumber?: string;
  readonly avatarUrl: string;
  readonly personalOrgId: OrganizationId;
  readonly activeOrgId: OrganizationId;
  readonly permissions: readonly string[];
  readonly roles: readonly string[];
  readonly shouldPromptMfa: boolean;
  readonly mfaEnabled: boolean;
  readonly requiresOrganizationSelection: boolean;
};

export type SwitchOrganizationRequest = {
  readonly organizationId: OrganizationId;
};

export type ExternalLoginBeginRequest = {
  readonly provider: "apple" | "github" | "google";
  readonly redirectUri: string;
  readonly state: string;
};

export type ExternalLoginBeginResponse = {
  readonly authorizeUrl: string;
};

export type ExternalLoginCompleteRequest = {
  readonly provider: "apple" | "github" | "google";
  readonly code: string;
  readonly idToken?: string;
  readonly redirectUri: string;
};

export type VerifyEmailRequest = {
  readonly pendingAuthenticationToken: string;
  readonly code: string;
};

export type InitiateEmailChangeRequest = {
  readonly newEmail: string;
  readonly password: string;
};

export type InitiateEmailChangeResponse = {
  readonly success: boolean;
  readonly message: string;
  readonly expiresAt?: Timestamp | null;
};

export type VerifyEmailChangeRequest = {
  readonly newEmail: string;
  readonly otpCode: string;
};

export type VerifyEmailChangeResponse = {
  readonly success: boolean;
  readonly error: string;
};

export type PasswordResetRequest = {
  readonly email: string;
  readonly redirectUrl?: string;
  readonly resetUrl?: string;
};

export type CompletePasswordResetRequest = {
  readonly token: string;
  readonly password: string;
  readonly newPassword?: string;
};

export type MagicLinkRequest = {
  readonly email: string;
  readonly redirectUrl: string;
  readonly loginUrl?: string;
};

export type CompleteMagicLinkRequest = {
  readonly token: string;
};

export type SignupResponse = {
  readonly success: boolean;
  readonly requiresEmailVerification: boolean;
  readonly pendingAuthenticationToken: string;
  readonly userId: UserId;
  readonly email: string;
  readonly error?: string;
};

export type PasswordLoginStatus =
  | "AccountLocked"
  | "EmailVerificationRequired"
  | "Error"
  | "InvalidCredentials"
  | "MfaRequired"
  | "SsoRequired"
  | "Success"
  | "Unspecified";

export type PendingAuthInfo = {
  readonly pendingAuthenticationToken: string;
  readonly email: string;
  readonly availableFactors?: readonly {
    readonly id: Ulid;
    readonly type: string;
  }[];
  readonly challenge?: {
    readonly id: Ulid;
    readonly factorId: Ulid;
    readonly expiresAt?: Timestamp;
  };
};

export type PasswordLoginResponse = {
  readonly status: PasswordLoginStatus;
  readonly authResponse?: AuthTokenResponse;
  readonly pendingInfo?: PendingAuthInfo;
  readonly error?: string;
  readonly organizationName?: string;
  readonly lockoutInfo?: {
    readonly isLocked: boolean;
    readonly failedAttempts: number;
    readonly maxAttempts: number;
    readonly lockoutEndsAt?: Timestamp;
    readonly remainingAttempts: number;
  };
};

export type Organization = {
  readonly organizationId: OrganizationId;
  readonly name: string;
  readonly ownerUserIds: readonly UserId[];
  readonly createdByUserId?: UserId | null;
  readonly billingAccountId: string;
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
  readonly billingAccountId?: string;
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

export type OnboardingStep =
  | "account-information"
  | "payment-method"
  | "payment-validation"
  | "mfa"
  | "organization"
  | "project"
  | "complete";

export type OnboardingMfaDecision = "pending" | "configured" | "later";

export type OnboardingPaymentValidationStatus =
  | "not-started"
  | "pending"
  | "approved"
  | "rejected";

export type OnboardingStatus =
  | "not-started"
  | "in-progress"
  | "awaiting-validation"
  | "blocked"
  | "ready-for-project"
  | "completed";

export type OnboardingStepState = {
  readonly key: Exclude<OnboardingStep, "complete">;
  readonly state: "pending" | "current" | "completed" | "blocked";
};

export type OnboardingState = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName: string;
  readonly phoneNumber: string;
  readonly accountInformationCompleted: boolean;
  readonly paymentMethodRegistered: boolean;
  readonly paymentMethodValidated: boolean;
  readonly paymentValidationStatus: OnboardingPaymentValidationStatus;
  readonly mfaDecision: OnboardingMfaDecision;
  readonly organizationId?: OrganizationId | null;
  readonly organizationOwnerAssigned: boolean;
  readonly paymentMethodLinkedToOrganization: boolean;
  readonly subscriptionOrTrialActive: boolean;
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

export type UpdateOnboardingAccountRequest = {
  readonly displayName: string;
  readonly phoneNumber: string;
};

export type CreateOnboardingPaymentSessionRequest = {
  readonly successUrl: string;
  readonly cancelUrl: string;
};

export type OnboardingPaymentSession = {
  readonly checkoutUrl: string;
  readonly sessionId: string;
};

export type ValidateOnboardingPaymentRequest = {
  readonly sessionId?: string;
  readonly developmentBypass?: boolean;
};

export type UpdateOnboardingMfaRequest = {
  readonly configureLater: boolean;
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
  readonly responseUrlTemplate: string;
};

export type EnvironmentKind = "Custom" | "Dev" | "Preview" | "Prod";
export type EnvironmentStatus = "Active" | "Deleting" | "Deleted" | "Expired" | "Merged" | "Merging";
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

/**
 * Physical database branch admission. The Platform currently returns
 * `"unavailable"` and all availability flags as false.
 */
export type DatabaseBranchCapabilityResponse = {
  readonly projectId: ProjectId;
  readonly admissionState: string;
  readonly physicalCloneAvailable: boolean;
  readonly promotionAvailable: boolean;
  readonly mergeAvailable: boolean;
  readonly detail: string;
};

/**
 * Requests a physical database clone. The Platform currently rejects every
 * request with its typed 503 error response; it does not create a branch.
 */
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
};

/** A v2 saved query with an opaque, resource-bound conditional revision token. */
export type ProjectSavedQueryV2Response = ProjectSavedQueryResponse & {
  readonly revision: string;
};

/** Conditional v2 saved-query update. Pass the revision returned by a v2 read or list. */
export type UpdateProjectSavedQueryV2Request = ProjectSavedQueryRequest & {
  readonly revision: string;
};

/** Cursor parameters for a mutation-safe v3 saved-query snapshot. */
export type ProjectSavedQuerySnapshotPageRequest = {
  /** Opaque, project-bound cursor returned by the preceding v3 page. */
  readonly cursor?: string;
  /** Number of results to return, from 1 through 500. */
  readonly take?: number;
};

/** A v3 snapshot page. Reload the collection if its opaque cursor expires with a conflict. */
export type ProjectSavedQuerySnapshotPageResponse = {
  readonly items: readonly ProjectSavedQueryV2Response[];
  readonly nextCursor?: string | null;
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
  readonly retentionSeconds?: number;
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
  readonly createdAt?: Timestamp;
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

export type ApiKey = {
  readonly keyId: ApiKeyId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly prefix: string;
  readonly lastFour: string;
  readonly scopes: readonly string[];
  readonly databaseId?: DatabaseId | null;
  readonly environmentId?: EnvironmentId | null;
  readonly createdByUserId?: UserId | null;
  readonly createdByEmail: string;
  readonly createdAt: Timestamp;
  readonly expiresAt?: Timestamp | null;
  readonly lastUsedAt?: Timestamp | null;
  readonly revokedAt?: Timestamp | null;
  readonly isActive: boolean;
};

export type CreateApiKeyRequest = {
  readonly name: string;
  readonly scopes: readonly string[];
  readonly databaseId?: DatabaseId;
  readonly environmentId?: EnvironmentId;
  readonly expiresAt?: Timestamp | null;
};

export type ListApiKeysRequest = {
  readonly cursor?: string;
  readonly limit?: number;
};

export type ListApiKeysResponse = {
  readonly apiKeys: readonly ApiKey[];
  readonly nextCursor?: string;
};

export type ProjectAuthProvider = {
  readonly projectId: ProjectId;
  readonly provider: string;
  readonly protocol: string;
  readonly displayName: string;
  readonly enabled: boolean;
  readonly builtIn: boolean;
  readonly clientId: string;
  readonly hasClientSecret: boolean;
  readonly authorizationEndpoint: string;
  readonly tokenEndpoint: string;
  readonly userInfoEndpoint: string;
  readonly redirectUri: string;
  readonly scopes: readonly string[];
  readonly samlServiceProviderEntityId: string;
  readonly samlIdentityProviderEntityId: string;
  readonly samlSingleSignOnUrl: string;
  readonly hasSamlSigningCertificate: boolean;
  readonly samlAssertionConsumerServiceUrl: string;
  readonly createdAt: Timestamp;
  readonly updatedAt: Timestamp;
};

export type ListProjectAuthProvidersRequest = {
  readonly cursor?: string;
  readonly limit?: number;
};

export type ListProjectAuthProvidersResponse = {
  readonly providers: readonly ProjectAuthProvider[];
  readonly nextCursor: string;
};

export type CreatedApiKey = {
  readonly apiKey: ApiKey;
  readonly secret: string;
};

export type RotatedApiKey = {
  readonly apiKey: ApiKey;
  readonly secret: string;
  readonly rotated: boolean;
};

export type RevokedApiKey = {
  readonly keyId: ApiKeyId;
  readonly revoked: boolean;
};

export type M2MApplication = {
  readonly clientId: M2MClientId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly prefix: string;
  readonly lastFour: string;
  readonly scopes: readonly string[];
  readonly databaseId?: DatabaseId | null;
  readonly environmentId?: EnvironmentId | null;
  readonly createdByUserId?: UserId | null;
  readonly createdByEmail: string;
  readonly createdAt: Timestamp;
  readonly expiresAt?: Timestamp | null;
  readonly lastUsedAt?: Timestamp | null;
  readonly revokedAt?: Timestamp | null;
  readonly isActive: boolean;
};

export type CreateM2MApplicationRequest = {
  readonly name: string;
  readonly scopes: readonly string[];
  readonly databaseId?: DatabaseId;
  readonly environmentId?: EnvironmentId;
  readonly expiresAt?: Timestamp | null;
};

export type ListM2MApplicationsRequest = {
  readonly cursor?: string;
  readonly limit?: number;
};

export type ListM2MApplicationsResponse = {
  readonly applications: readonly M2MApplication[];
  readonly nextCursor?: string;
};

export type CreatedM2MApplication = {
  readonly application: M2MApplication;
  readonly clientSecret: string;
};

export type RotatedM2MApplicationSecret = {
  readonly application: M2MApplication;
  readonly clientSecret: string;
  readonly rotated: boolean;
};

export type RevokedM2MApplication = {
  readonly clientId: M2MClientId;
  readonly revoked: boolean;
};

export type M2MTokenRequest = {
  readonly grantType?: "client_credentials" | string;
  readonly clientId: string;
  readonly clientSecret: string;
  readonly scope?: string;
};

export type M2MTokenResponse = {
  readonly access_token: string;
  readonly token_type: string;
  readonly expires_in: number;
  readonly scope: string;
  readonly project_id: ProjectId;
  readonly client_id: M2MClientId;
  readonly database_id?: DatabaseId | null;
  readonly environment_id?: EnvironmentId | null;
};

export type AccessRole = "member" | "owner" | "viewer";

export type TeamMember = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName?: string;
  readonly role: AccessRole;
  readonly joinedAt?: Timestamp;
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
  readonly planId: string;
  readonly planName: string;
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

export type BillingAccount = {
  readonly billingAccountId: Ulid;
  readonly organizationId: OrganizationId;
  readonly planId: Ulid;
  readonly provider: "polar" | "stripe";
  readonly status: "active" | "canceled" | "past_due" | "trialing";
  readonly billingInterval: "monthly" | "annual" | string | null;
};

export type CurrentOrganizationFeatureEntitlement = {
  readonly key: string;
  readonly enabled: boolean;
  readonly limit?: number;
  readonly used?: number;
};

export type CurrentOrganizationLimits = {
  readonly maxRegions?: number;
  readonly maxProjects?: number;
  readonly maxDatabases?: number;
  readonly maxTeamMembers?: number;
  readonly maxRequestsPerMonth?: number;
  readonly maxStorageGb?: number;
  readonly maxRepositories?: number;
  readonly maxApiKeys?: number;
};

export type CurrentOrganizationBilling = {
  readonly organizationId: OrganizationId;
  readonly status: string;
  readonly isActive: boolean;
  readonly isTrialing: boolean;
  readonly provider?: string | null;
  readonly customerId?: string | null;
  readonly subscriptionId?: string | null;
  readonly planId?: string | null;
  readonly planName?: string | null;
  readonly planType?: string | null;
  readonly amount: number;
  readonly currency?: string | null;
  readonly currentPeriodStart?: Timestamp | null;
  readonly currentPeriodEnd?: Timestamp | null;
  readonly billingInterval?: "monthly" | "annual" | string | null;
  readonly features: readonly CurrentOrganizationFeatureEntitlement[];
  readonly limits?: CurrentOrganizationLimits;
  readonly billingRoles: readonly string[];
};

export type BillingGuardRequest = {
  readonly requireActiveSubscription?: boolean;
  readonly allowTrialing?: boolean;
  readonly requireNoBillingAction?: boolean;
  readonly allowedPlanIds?: readonly string[];
  readonly allowedPlanTypes?: readonly string[];
  readonly requiredRoles?: readonly string[];
  readonly requiredFeatures?: readonly string[];
  readonly currentSeats?: number;
  readonly requestedSeats?: number;
  readonly currentRequests?: number;
  readonly requestedRequests?: number;
  readonly currentStorageGb?: number;
  readonly requestedStorageGb?: number;
};

export type BillingGuardResponse = {
  readonly allowed: boolean;
  readonly reason?: string;
  readonly reasons: readonly string[];
  readonly missingRoles: readonly string[];
  readonly missingFeatures: readonly string[];
  readonly billingActionRequired: boolean;
  readonly status: string;
  readonly planId?: string;
  readonly planType?: string;
};

export type CreateCheckoutSessionRequest = {
  /** @deprecated Prefer planId and billingInterval. */
  readonly priceId?: string;
  readonly planId?: Ulid;
  readonly billingInterval?: "monthly" | "annual";
  readonly provider?: "polar" | "stripe";
  /** @deprecated Retained for source compatibility; ignored and never serialized. Checkout redirects are deployment-configured. */
  readonly successUrl?: string;
  /** @deprecated Retained for source compatibility; ignored and never serialized. Checkout redirects are deployment-configured. */
  readonly cancelUrl?: string;
  /** @deprecated Retained for source compatibility; ignored and never serialized. The authenticated TerraScale user's email is used. */
  readonly customerEmail?: string;
  readonly quantity?: number;
};

export type CheckoutSession = {
  readonly sessionId: string;
  readonly url: string;
};

export type BillingAccessMember = {
  readonly userId: Ulid;
  readonly email: string;
  readonly displayName: string;
  readonly role: "billing_manager";
  readonly enabled: boolean;
  readonly grantedAt: Timestamp;
  readonly grantedByUserId: Ulid;
};

export type Invoice = {
  readonly invoiceId: Ulid;
  readonly billingAccountId: Ulid;
  readonly amountCents: number;
  readonly refundedAmountCents: number;
  readonly currency: string;
  readonly status:
    | "draft"
    | "open"
    | "paid"
    | "void"
    | "uncollectible"
    | "partially_refunded"
    | "refunded";
  readonly issuedAt: Timestamp;
  readonly periodStart?: Timestamp | null;
  readonly periodEnd?: Timestamp | null;
  readonly hostedInvoiceUrl?: string | null;
  readonly invoicePdf?: string | null;
};

export type PricingPlan = {
  readonly planId: Ulid;
  readonly name: string;
  readonly currency: string;
  readonly monthlyBaseCents: number;
  readonly annualBaseCents: number;
  readonly includedUsage: readonly UsageMetric[];
  readonly key: string;
  readonly provider: string;
  readonly monthlyPriceId: string | null;
  readonly annualPriceId: string | null;
  readonly monthlyPolarProductId: string | null;
  readonly annualPolarProductId: string | null;
  readonly monthlyStripePriceId: string | null;
  readonly annualStripePriceId: string | null;
  readonly monthlyTrialDays: number;
  readonly annualTrialDays: number;
  readonly visible: boolean;
  readonly maxRegions: number;
  readonly operationOverageCentsPerMillion: number;
  readonly storageOverageCentsPerGbRegion: number;
};

export type AdminPricingPlanRequest = {
  readonly key?: string;
  readonly name?: string;
  readonly currency?: string;
  readonly monthlyBaseCents?: number;
  readonly annualBaseCents?: number;
  readonly monthlyPolarProductId?: string;
  readonly annualPolarProductId?: string;
  readonly monthlyStripePriceId?: string;
  readonly annualStripePriceId?: string;
  readonly monthlyTrialDays?: number;
  readonly annualTrialDays?: number;
  readonly visible?: boolean;
  readonly defaultCheckout?: boolean;
  readonly displayOrder?: number;
  readonly maxDatabases?: number;
  readonly maxRegions?: number;
  readonly maxStorageGb?: number;
  readonly maxRequestsPerMonth?: number;
  readonly maxTeamMembers?: number;
  readonly maxRepositories?: number;
  readonly maxApiKeys?: number;
  readonly operationOverageCentsPerMillion?: number;
  readonly storageOverageCentsPerGbRegion?: number;
  readonly includedUsage?: readonly UsageMetric[];
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

export type SecuritySettings = {
  readonly projectId: ProjectId;
  readonly mfaPolicy: string;
  readonly ipAllowlist: string;
  readonly allowedEmailDomains: string;
  readonly dataResidency: string;
  readonly encryption: string;
  readonly authenticationEnabled: boolean;
  readonly updatedAt: Timestamp;
};

export type UpdateSecuritySettingsRequest = {
  readonly mfaPolicy?: string;
  readonly ipAllowlist?: string;
  readonly allowedEmailDomains?: string;
  readonly dataResidency?: string;
  readonly authenticationEnabled?: boolean;
  readonly confirmDisableAuthentication?: boolean;
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
