import type {
  ApiKeyId,
  AuditEventId,
  BackupId,
  DatabaseId,
  DocumentId,
  EnvironmentId,
  InvitationId,
  MachineClientId,
  NotificationId,
  OrganizationId,
  ProjectId,
  QueryHistoryId,
  RepositoryId,
  RestoreId,
  SavedQueryId,
  Ulid,
  UserId,
  WebhookId,
} from "./ids.js";
import type { TerraScaleRegion, TerraScaleRegionCode } from "./regions.js";

export type Timestamp = string;
export type JsonPrimitive = boolean | null | number | string;
export type JsonValue = JsonPrimitive | JsonObject | readonly JsonValue[];
export type JsonObject = { readonly [key: string]: JsonValue };
export type SortDirection = "asc" | "desc";

export type PageRequest = {
  readonly skip?: number;
  readonly take?: number;
};

export type Page<TItem> = {
  readonly items: readonly TItem[];
  readonly totalCount?: number;
  readonly nextCursor?: string;
};

export type ProjectScoped = {
  readonly projectId?: ProjectId | undefined;
};

export type DatabaseScoped = ProjectScoped & {
  readonly databaseId: DatabaseId;
};

export type EnvironmentScoped = DatabaseScoped & {
  readonly environmentId: EnvironmentId;
};

export type RepositoryScoped = EnvironmentScoped & {
  readonly repositoryId: RepositoryId;
};

export type AuthSession = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName?: string;
  readonly projectId?: ProjectId;
  readonly organizationIds: readonly OrganizationId[];
  readonly expiresAt: Timestamp;
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
  readonly rememberDevice?: boolean;
  readonly deviceFingerprint?: string;
};

export type AuthTokenResponse = {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly tokenType: string;
  readonly expiresIn: number;
  readonly userId: UserId;
  readonly email: string;
  readonly name: string;
  readonly avatarUrl: string;
  readonly personalOrgId: OrganizationId;
  readonly activeOrgId: OrganizationId;
  readonly permissions: readonly string[];
  readonly roles: readonly string[];
  readonly shouldPromptMfa: boolean;
  readonly mfaEnabled: boolean;
};

export type VerifyEmailRequest = {
  readonly pendingAuthenticationToken: string;
  readonly code: string;
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
  readonly slug?: string;
  readonly createdAt: Timestamp;
};

export type OrganizationUser = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName: string;
  readonly organizationIds: readonly OrganizationId[];
  readonly createdAt?: Timestamp;
};

export type CreateOrganizationRequest = {
  readonly name: string;
  readonly adminUserIds?: readonly UserId[];
  readonly billingAccountId?: string;
  readonly slug?: string;
};

export type UpdateOrganizationRequest = Partial<CreateOrganizationRequest>;

export type Project = {
  readonly projectId: ProjectId;
  readonly organizationId: OrganizationId;
  readonly name: string;
  readonly slug?: string;
  readonly createdAt: Timestamp;
};

export type CreateProjectRequest = {
  readonly name: string;
  readonly defaultPermissionKeys?: readonly string[];
  readonly slug?: string;
};

export type UpdateProjectRequest = Partial<CreateProjectRequest>;

export type ProjectSwitchResponse = {
  readonly projectId: ProjectId;
  readonly selectedEnvironmentIds: Readonly<Record<string, EnvironmentId>>;
};

export type DashboardContextUser = {
  readonly userId: UserId;
  readonly email: string;
  readonly displayName: string;
};

export type DashboardContextOrganization = {
  readonly organizationId: OrganizationId;
  readonly name: string;
};

export type DashboardContextProject = {
  readonly projectId: ProjectId;
  readonly organizationId: OrganizationId;
  readonly name: string;
  readonly isCurrent: boolean;
};

export type DashboardContextDatabase = {
  readonly databaseId: DatabaseId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly regions: readonly TerraScaleRegionCode[];
  readonly createdAt: Timestamp;
};

export type DashboardContext = {
  readonly user: DashboardContextUser;
  readonly organization: DashboardContextOrganization;
  readonly projects: readonly DashboardContextProject[];
  readonly currentProjectId: ProjectId;
  readonly databases: readonly DashboardContextDatabase[];
};

export type Invitation = {
  readonly invitationId: InvitationId;
  readonly organizationId: OrganizationId;
  readonly email: string;
  readonly role: string;
  readonly status: string;
  readonly invitedByEmail?: string;
  readonly expiresAt: Timestamp;
  readonly acceptedAt?: Timestamp | null;
  readonly createdAt?: Timestamp;
};

export type CreateInvitationRequest = {
  readonly email: string;
  readonly role: string;
  readonly redirectUrl?: string;
};

export type DatabaseStatus = "available" | "creating" | "deleting" | "failed" | "updating";
export type EnvironmentKind = "development" | "preview" | "production";

export type ProjectDatabase = {
  readonly databaseId: DatabaseId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly status: DatabaseStatus;
  readonly regions: readonly TerraScaleRegionCode[];
  readonly environments: readonly DatabaseEnvironment[];
  readonly createdAt: Timestamp;
};

export type DatabaseEnvironment = {
  readonly environmentId: EnvironmentId;
  readonly databaseId: DatabaseId;
  readonly name: string;
  readonly kind: EnvironmentKind;
  readonly status: DatabaseStatus;
  readonly selected: boolean;
};

export type CreateDatabaseRequest = {
  readonly name: string;
  readonly regions?: readonly TerraScaleRegionCode[];
};

export type UpdateDatabaseRequest = {
  readonly name?: string;
  readonly regions?: readonly TerraScaleRegionCode[];
};

export type Backup = {
  readonly backupId: BackupId;
  readonly projectId: ProjectId;
  readonly databaseId: DatabaseId;
  readonly environmentId?: EnvironmentId;
  readonly status: "completed" | "failed" | "queued" | "running";
  readonly createdAt: Timestamp;
  readonly completedAt?: Timestamp;
};

export type CreateBackupRequest = {
  readonly environmentId?: EnvironmentId;
  readonly description?: string;
};

export type Restore = {
  readonly restoreId: RestoreId;
  readonly backupId: BackupId;
  readonly databaseId: DatabaseId;
  readonly environmentId?: EnvironmentId;
  readonly status: "completed" | "failed" | "queued" | "running";
  readonly createdAt: Timestamp;
  readonly completedAt?: Timestamp;
};

export type CreateRestoreRequest = {
  readonly environmentId?: EnvironmentId;
  readonly targetEnvironmentId?: EnvironmentId;
};

export type RepositoryDefinition = {
  readonly repositoryId: RepositoryId;
  readonly databaseId: DatabaseId;
  readonly environmentId: EnvironmentId;
  readonly name: string;
  readonly collection: string;
  readonly physicalCollection?: never;
  readonly schemaId?: Ulid;
  readonly createdAt: Timestamp;
};

export type CreateRepositoryRequest = {
  readonly name: string;
  readonly collection: string;
  readonly schemaId?: Ulid;
};

export type UpdateRepositoryRequest = Partial<CreateRepositoryRequest>;

export type DocumentEnvelope<TDocument extends JsonObject = JsonObject> = {
  readonly documentId: DocumentId;
  readonly repositoryId: RepositoryId;
  readonly etag?: string;
  readonly createdAt?: Timestamp;
  readonly updatedAt?: Timestamp;
  readonly document: TDocument;
};

export type DocumentListQuery = PageRequest & {
  readonly search?: string;
};

export type PatchOperation = {
  readonly op: "add" | "copy" | "move" | "remove" | "replace" | "test";
  readonly path: string;
  readonly from?: string;
  readonly value?: JsonValue;
};

export type QueryRequest = {
  readonly query: string;
  readonly parameters?: JsonObject;
  readonly take?: number;
};

export type QueryResult = {
  readonly rows: readonly JsonObject[];
  readonly elapsedMilliseconds?: number;
  readonly continuationToken?: string;
};

export type QueryPlan = {
  readonly query: string;
  readonly operations: readonly string[];
  readonly warnings?: readonly string[];
};

export type SchemaDocument = {
  readonly schemaId: Ulid;
  readonly name: string;
  readonly version: number;
  readonly document: JsonObject;
  readonly createdAt: Timestamp;
};

export type ApplySchemaRequest = {
  readonly name: string;
  readonly document: JsonObject;
};

export type SavedQuery = {
  readonly savedQueryId: SavedQueryId;
  readonly name: string;
  readonly query: string;
  readonly parameters?: JsonObject;
  readonly createdAt: Timestamp;
};

export type SaveQueryRequest = {
  readonly name: string;
  readonly query: string;
  readonly parameters?: JsonObject;
};

export type QueryHistoryEntry = {
  readonly queryHistoryId: QueryHistoryId;
  readonly query: string;
  readonly status: "failed" | "succeeded";
  readonly startedAt: Timestamp;
  readonly elapsedMilliseconds?: number;
};

export type ApiKey = {
  readonly apiKeyId: ApiKeyId;
  readonly name: string;
  readonly scopes: readonly string[];
  readonly expiresAt?: Timestamp;
  readonly createdAt: Timestamp;
};

export type CreateApiKeyRequest = {
  readonly name: string;
  readonly scopes: readonly string[];
  readonly expiresAt?: Timestamp;
};

export type CreatedApiKey = ApiKey & {
  readonly token: string;
};

export type MachineClient = {
  readonly machineClientId: MachineClientId;
  readonly name: string;
  readonly scopes: readonly string[];
  readonly enabled: boolean;
  readonly createdAt: Timestamp;
};

export type CreateMachineClientRequest = {
  readonly name: string;
  readonly scopes: readonly string[];
};

export type RotatedMachineClientSecret = {
  readonly machineClientId: MachineClientId;
  readonly secret: string;
};

export type AccessRole = "admin" | "billing" | "member" | "owner" | "viewer";

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

export type BillingAccount = {
  readonly billingAccountId: Ulid;
  readonly organizationId: OrganizationId;
  readonly planId: Ulid;
  readonly status: "active" | "canceled" | "past_due" | "trialing";
};

export type CurrentOrganizationFeatureEntitlement = {
  readonly key: string;
  readonly enabled: boolean;
  readonly limit?: number;
  readonly used?: number;
};

export type CurrentOrganizationLimits = {
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
  readonly priceId?: string;
  readonly successUrl?: string;
  readonly cancelUrl?: string;
  readonly customerEmail?: string;
  readonly quantity?: number;
};

export type CheckoutSession = {
  readonly sessionId: string;
  readonly url: string;
};

export type Invoice = {
  readonly invoiceId: Ulid;
  readonly billingAccountId: Ulid;
  readonly amountCents: number;
  readonly currency: string;
  readonly status: "draft" | "open" | "paid" | "void";
  readonly issuedAt: Timestamp;
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
  readonly key?: string;
  readonly monthlyStripePriceId?: string | null;
  readonly annualStripePriceId?: string | null;
  readonly visible?: boolean;
};

export type AdminPricingPlanRequest = {
  readonly key?: string;
  readonly name?: string;
  readonly currency?: string;
  readonly monthlyBaseCents?: number;
  readonly annualBaseCents?: number;
  readonly monthlyStripePriceId?: string;
  readonly annualStripePriceId?: string;
  readonly visible?: boolean;
  readonly defaultCheckout?: boolean;
  readonly displayOrder?: number;
  readonly maxDatabases?: number;
  readonly maxStorageGb?: number;
  readonly maxRequestsPerMonth?: number;
  readonly maxTeamMembers?: number;
  readonly maxRepositories?: number;
  readonly maxApiKeys?: number;
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
  readonly userId: UserId;
  readonly title: string;
  readonly body?: string;
  readonly severity: "critical" | "info" | "warning";
  readonly readAt?: Timestamp;
  readonly createdAt: Timestamp;
};

export type ProjectSettings = {
  readonly projectId: ProjectId;
  readonly displayName?: string;
  readonly defaultRegions: readonly TerraScaleRegionCode[];
};

export type SecuritySettings = {
  readonly projectId: ProjectId;
  readonly mfaRequired: boolean;
  readonly sessionTimeoutMinutes: number;
};

export type Webhook = {
  readonly webhookId: WebhookId;
  readonly projectId: ProjectId;
  readonly name: string;
  readonly url: string;
  readonly events: readonly string[];
  readonly enabled: boolean;
};

export type CreateWebhookRequest = {
  readonly name: string;
  readonly url: string;
  readonly events: readonly string[];
};

export type UpdateWebhookRequest = Partial<CreateWebhookRequest> & {
  readonly enabled?: boolean;
};

export type SsoSettings = {
  readonly projectId: ProjectId;
  readonly enabled: boolean;
  readonly domains: readonly string[];
  readonly metadataUrl?: string;
};

export type ScimSettings = {
  readonly projectId: ProjectId;
  readonly enabled: boolean;
  readonly baseUrl?: string;
};

export type CreatedScimToken = {
  readonly tokenId: Ulid;
  readonly token: string;
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
