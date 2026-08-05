import { createTerraScaleClient, type TerraScaleClient } from "./factory.js";
import type { FetchLike } from "./config.js";
import {
  type TerraScaleBase,
  type TerraScaleRequestMetric,
  type TerraScaleRouteName,
} from "./http.js";
import { toUlid } from "./ids.js";

const sampleIds = {
  apiKeyId: toUlid("01BRZ3NDEKTSV4RRFFQ69G5FAV"),
  auditEventId: toUlid("01FRZ3NDEKTSV4RRFFQ69G5FAV"),
  backupId: toUlid("01NRZ3NDEKTSV4RRFFQ69G5FAV"),
  clientId: toUlid("01CRZ3NDEKTSV4RRFFQ69G5FAV"),
  databaseId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAW"),
  documentId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAZ"),
  environmentId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAX"),
  invitationId: toUlid("01GRZ3NDEKTSV4RRFFQ69G5FAV"),
  notificationId: toUlid("01DRZ3NDEKTSV4RRFFQ69G5FAV"),
  organizationId: toUlid("01ERZ3NDEKTSV4RRFFQ69G5FAV"),
  planId: toUlid("01HRZ3NDEKTSV4RRFFQ69G5FAV"),
  projectId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV"),
  queueId: toUlid("01PRZ3NDEKTSV4RRFFQ69G5FAV"),
  repositoryId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAY"),
  restoreId: toUlid("01MRZ3NDEKTSV4RRFFQ69G5FAV"),
  savedQueryId: toUlid("01SRZ3NDEKTSV4RRFFQ69G5FAV"),
  sessionId: toUlid("01JRZ3NDEKTSV4RRFFQ69G5FAV"),
  userId: toUlid("01KRZ3NDEKTSV4RRFFQ69G5FAV"),
} as const;

const pathTemplateSegments = new Map<string, string>(
  Object.entries(sampleIds).map(([name, value]) => [value, `{${name}}`]),
);
pathTemplateSegments.set("sfo", "{regionCode}");

export type CapturedSdkRoute = {
  readonly base: TerraScaleBase;
  readonly client: string;
  readonly method: string;
  readonly operation: string;
  readonly pathTemplate: string;
  readonly routeName: TerraScaleRouteName;
};

type CaptureOperation = {
  readonly client: string;
  readonly defaultProject?: boolean;
  readonly invoke: (client: TerraScaleClient) => Promise<unknown>;
  readonly operation: string;
  readonly variant?: string;
};

export async function capturePublicClientRoutes(): Promise<readonly CapturedSdkRoute[]> {
  assertCaptureOperationCoverage();
  return Promise.all(captureOperations.map(captureRoute));
}

async function captureRoute(operation: CaptureOperation): Promise<CapturedSdkRoute> {
  let capturedRequest: Request | undefined;
  let capturedMetric: TerraScaleRequestMetric | undefined;

  const captureFetch: FetchLike = async (input) => {
    capturedRequest = input instanceof Request ? input : new Request(input);

    return new Response("{}", {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  };

  const baseConfig = {
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    fetch: captureFetch,
    requestMetricRecorder: (metric: TerraScaleRequestMetric): void => {
      capturedMetric = metric;
    },
  };
  const client = createTerraScaleClient(
    operation.defaultProject === true
      ? { ...baseConfig, projectId: sampleIds.projectId }
      : baseConfig,
  );

  await operation.invoke(client);

  if (capturedRequest === undefined) {
    throw new Error(`Route capture for ${operation.client}.${operation.operation} made no request.`);
  }

  if (capturedMetric === undefined) {
    throw new Error(
      `Route capture for ${operation.client}.${operation.operation} recorded no route metric.`,
    );
  }

  return {
    base: capturedMetric.base,
    client: operation.client,
    method: capturedRequest.method,
    operation:
      operation.variant === undefined
        ? operation.operation
        : `${operation.operation}:${operation.variant}`,
    pathTemplate: pathToTemplate(new URL(capturedRequest.url).pathname),
    routeName: capturedMetric.routeName,
  };
}

function assertCaptureOperationCoverage(): void {
  const publicMethods = new Set(enumeratePublicClientMethods());
  const capturedKeys = new Set(
    captureOperations.map((operation) => `${operation.client}.${operation.operation}`),
  );
  const staleCaptureKeys = [...capturedKeys].filter((key) => !publicMethods.has(key)).sort();
  const uncoveredMethods = [...publicMethods].filter((key) => !capturedKeys.has(key));

  if (staleCaptureKeys.length === 0 && uncoveredMethods.length === 0) {
    return;
  }

  const lines = ["Public client route capture coverage is incomplete."];

  if (uncoveredMethods.length > 0) {
    lines.push("Uncovered public client methods:");
    lines.push(...uncoveredMethods.map((key) => `  - ${key}`));
  }

  if (staleCaptureKeys.length > 0) {
    lines.push("Capture operations reference methods that no longer exist on the public client surface:");
    lines.push(...staleCaptureKeys.map((key) => `  - ${key}`));
  }

  throw new Error(lines.join("\n"));
}

function enumeratePublicClientMethods(): readonly string[] {
  const client = createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    fetch: async () =>
      new Response("{}", {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
  });
  const methods: string[] = [];

  for (const [clientName, value] of Object.entries(client)) {
    enumerateMethods(clientName, value, methods);
  }

  return methods.sort();
}

function enumerateMethods(clientName: string, value: unknown, methods: string[]): void {
  if (typeof value !== "object" || value === null) {
    return;
  }

  for (const [name, descriptor] of Object.entries(
    Object.getOwnPropertyDescriptors(Object.getPrototypeOf(value) as object),
  )) {
    if (name !== "constructor" && typeof descriptor.value === "function") {
      methods.push(`${clientName}.${name}`);
    }
  }

  for (const [name, nestedValue] of Object.entries(value)) {
    enumerateMethods(`${clientName}.${name}`, nestedValue, methods);
  }
}

function pathToTemplate(pathname: string): string {
  for (const [value, template] of pathTemplateSegments) {
    pathname = pathname.replaceAll(value, template);
  }

  return pathname;
}

const registerRequest = {
  email: "ada@example.test",
  password: "correct horse battery staple",
  firstName: "Ada",
  lastName: "Lovelace",
  verifyUrl: "https://app.example.test/verify",
};

const captureOperations: readonly CaptureOperation[] = [
  {
    client: "onboarding",
    operation: "getState",
    invoke: (client) => client.onboarding.getState(),
  },
  {
    client: "onboarding",
    operation: "updateAccountInformation",
    invoke: (client) =>
      client.onboarding.updateAccountInformation({
        displayName: "Ada Lovelace",
        phoneNumber: "+14155552671",
      }),
  },
  {
    client: "onboarding",
    operation: "createPaymentSession",
    invoke: (client) =>
      client.onboarding.createPaymentSession({
        successUrl: "https://app.example.test/onboarding?payment=success",
        cancelUrl: "https://app.example.test/onboarding?payment=cancelled",
      }),
  },
  {
    client: "onboarding",
    operation: "validatePaymentMethod",
    invoke: (client) =>
      client.onboarding.validatePaymentMethod({ sessionId: "cs_test_onboarding" }),
  },
  {
    client: "onboarding",
    operation: "updateMfaDecision",
    invoke: (client) => client.onboarding.updateMfaDecision({ configureLater: true }),
  },
  {
    client: "onboarding",
    operation: "createOrganization",
    invoke: (client) => client.onboarding.createOrganization({ name: "Analytical Engines" }),
  },
  {
    client: "onboarding",
    operation: "createProject",
    invoke: (client) =>
      client.onboarding.createProject({
        name: "Primary",
        regions: ["gru"],
        backupRegion: "gru",
      }),
  },
  {
    client: "auth",
    operation: "getSession",
    invoke: (client) => client.auth.getSession(),
  },
  {
    client: "auth",
    operation: "getCurrentBilling",
    invoke: (client) => client.auth.getCurrentBilling(),
  },
  {
    client: "auth",
    operation: "evaluateBillingGuard",
    invoke: (client) => client.auth.evaluateBillingGuard({ requireActiveSubscription: true }),
  },
  {
    client: "auth",
    operation: "register",
    variant: "global",
    invoke: (client) => client.auth.register(registerRequest),
  },
  {
    client: "auth",
    operation: "register",
    variant: "project",
    defaultProject: true,
    invoke: (client) => client.auth.register(registerRequest),
  },
  {
    client: "auth",
    operation: "verifyEmail",
    variant: "global",
    invoke: (client) =>
      client.auth.verifyEmail({
        pendingAuthenticationToken: "pending-token",
        code: "123456",
      }),
  },
  {
    client: "auth",
    operation: "verifyEmail",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.verifyEmail({
        pendingAuthenticationToken: "pending-token",
        code: "123456",
      }),
  },
  {
    client: "auth",
    operation: "resendVerification",
    variant: "global",
    invoke: (client) =>
      client.auth.resendVerification(
        "pending-token",
        "https://app.example.test/verify",
      ),
  },
  {
    client: "auth",
    operation: "resendVerification",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.resendVerification(
        "pending-token",
        "https://app.example.test/verify",
      ),
  },
  {
    client: "auth",
    operation: "signIn",
    variant: "global",
    invoke: (client) =>
      client.auth.signIn({
        email: "ada@example.test",
        password: "correct horse battery staple",
      }),
  },
  {
    client: "auth",
    operation: "verifyPasswordMfa",
    variant: "global",
    invoke: (client) =>
      client.auth.verifyPasswordMfa({
        pendingAuthenticationToken: "pending-token",
        challengeId: sampleIds.sessionId,
        code: "123456",
      }),
  },
  {
    client: "auth",
    operation: "verifyPasswordMfa",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.verifyPasswordMfa({
        pendingAuthenticationToken: "pending-token",
        challengeId: sampleIds.sessionId,
        code: "123456",
      }),
  },
  {
    client: "auth",
    operation: "signIn",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.signIn({
        email: "ada@example.test",
        password: "correct horse battery staple",
      }),
  },
  {
    client: "auth",
    operation: "forgotPassword",
    variant: "global",
    invoke: (client) =>
      client.auth.forgotPassword({
        email: "ada@example.test",
        resetUrl: "https://app.example.test/reset",
      }),
  },
  {
    client: "auth",
    operation: "forgotPassword",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.forgotPassword({
        email: "ada@example.test",
        resetUrl: "https://app.example.test/reset",
      }),
  },
  {
    client: "auth",
    operation: "resetPassword",
    variant: "global",
    invoke: (client) =>
      client.auth.resetPassword({
        token: "reset-token",
        password: "correct horse battery staple",
      }),
  },
  {
    client: "auth",
    operation: "resetPassword",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.resetPassword({
        token: "reset-token",
        password: "correct horse battery staple",
      }),
  },
  {
    client: "auth",
    operation: "requestMagicLink",
    variant: "global",
    invoke: (client) =>
      client.auth.requestMagicLink({
        email: "ada@example.test",
        redirectUrl: "https://app.example.test/login",
      }),
  },
  {
    client: "auth",
    operation: "requestMagicLink",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.requestMagicLink({
        email: "ada@example.test",
        redirectUrl: "https://app.example.test/login",
      }),
  },
  {
    client: "auth",
    operation: "completeMagicLink",
    variant: "global",
    invoke: (client) => client.auth.completeMagicLink({ token: "magic-token" }),
  },
  {
    client: "auth",
    operation: "completeMagicLink",
    variant: "project",
    defaultProject: true,
    invoke: (client) => client.auth.completeMagicLink({ token: "magic-token" }),
  },
  {
    client: "auth",
    operation: "refresh",
    variant: "global",
    invoke: (client) => client.auth.refresh("refresh-token"),
  },
  {
    client: "auth",
    operation: "refresh",
    variant: "project",
    defaultProject: true,
    invoke: (client) => client.auth.refresh("refresh-token"),
  },
  {
    client: "auth",
    operation: "signOut",
    variant: "global",
    invoke: (client) => client.auth.signOut(),
  },
  {
    client: "auth",
    operation: "signOut",
    variant: "project",
    defaultProject: true,
    invoke: (client) => client.auth.signOut(),
  },
  {
    client: "auth",
    operation: "listSessions",
    invoke: (client) => client.auth.listSessions({ cursor: "next", limit: 25 }),
  },
  {
    client: "auth",
    operation: "revokeSession",
    invoke: (client) => client.auth.revokeSession(sampleIds.sessionId),
  },
  {
    client: "auth",
    operation: "revokeOtherSessions",
    invoke: (client) => client.auth.revokeOtherSessions(),
  },
  {
    client: "auth",
    operation: "updateProfile",
    invoke: (client) =>
      client.auth.updateProfile({ displayName: "Ada Lovelace", phoneNumber: "+14155552671" }),
  },
  {
    client: "auth",
    operation: "changePassword",
    invoke: (client) =>
      client.auth.changePassword({ currentPassword: "old-secret", newPassword: "new-secret" }),
  },
  {
    client: "auth",
    operation: "listMfaFactors",
    invoke: (client) => client.auth.listMfaFactors(),
  },
  {
    client: "auth",
    operation: "enrollTotp",
    invoke: (client) => client.auth.enrollTotp(),
  },
  {
    client: "auth",
    operation: "verifyTotpEnrollment",
    invoke: (client) =>
      client.auth.verifyTotpEnrollment({ challengeId: sampleIds.sessionId, code: "123456" }),
  },
  {
    client: "auth",
    operation: "deleteMfaFactor",
    invoke: (client) => client.auth.deleteMfaFactor(sampleIds.sessionId, { code: "123456" }),
  },
  {
    client: "auth",
    operation: "generateRecoveryCodes",
    invoke: (client) => client.auth.generateRecoveryCodes(),
  },
  {
    client: "auth",
    operation: "getRecoveryCodeCount",
    invoke: (client) => client.auth.getRecoveryCodeCount(),
  },
  {
    client: "auth",
    operation: "verifyRecoveryCode",
    variant: "global",
    invoke: (client) =>
      client.auth.verifyRecoveryCode({ email: "ada@example.test", code: "abcd-1234" }),
  },
  {
    client: "auth",
    operation: "verifyRecoveryCode",
    variant: "project",
    defaultProject: true,
    invoke: (client) =>
      client.auth.verifyRecoveryCode({ email: "ada@example.test", code: "abcd-1234" }),
  },
  {
    client: "auth",
    operation: "switchOrganization",
    invoke: (client) =>
      client.auth.switchOrganization({ organizationId: sampleIds.organizationId }),
  },
  {
    client: "auth",
    operation: "beginExternalLogin",
    invoke: (client) =>
      client.auth.beginExternalLogin({
        provider: "google",
        redirectUri: "https://app.example.test/api/auth/external-callback",
        state: "oauth-state",
      }),
  },
  {
    client: "auth",
    operation: "completeExternalLogin",
    invoke: (client) =>
      client.auth.completeExternalLogin({
        provider: "github",
        code: "oauth-code",
        redirectUri: "https://app.example.test/api/auth/external-callback",
      }),
  },
  {
    client: "auth",
    operation: "listProjectProviders",
    defaultProject: true,
    invoke: (client) => client.auth.listProjectProviders(undefined, { cursor: "next", limit: 25 }),
  },
  {
    client: "auth",
    operation: "initiateEmailChange",
    invoke: (client) =>
      client.auth.initiateEmailChange({
        newEmail: "augusta@example.test",
        password: "correct horse battery staple",
      }),
  },
  {
    client: "auth",
    operation: "verifyEmailChange",
    invoke: (client) =>
      client.auth.verifyEmailChange({
        newEmail: "augusta@example.test",
        otpCode: "123456",
      }),
  },
  {
    client: "organizations",
    operation: "list",
    invoke: (client) => client.organizations.list({ skip: 0, take: 25 }),
  },
  {
    client: "organizations",
    operation: "create",
    invoke: (client) => client.organizations.create({ name: "Analytical Engines" }),
  },
  {
    client: "organizations",
    operation: "get",
    invoke: (client) => client.organizations.get(sampleIds.organizationId),
  },
  {
    client: "organizations",
    operation: "update",
    invoke: (client) =>
      client.organizations.update(sampleIds.organizationId, { name: "Analytical Engines" }),
  },
  {
    client: "organizations",
    operation: "requestDeletion",
    invoke: (client) =>
      client.organizations.requestDeletion(sampleIds.organizationId, {
        confirm: true,
        organizationName: "Analytical Engines",
      }),
  },
  {
    client: "organizations",
    operation: "leave",
    invoke: (client) => client.organizations.leave(sampleIds.organizationId),
  },
  {
    client: "organizations",
    operation: "listProjects",
    invoke: (client) =>
      client.organizations.listProjects(sampleIds.organizationId, { skip: 0, take: 25 }),
  },
  {
    client: "organizations",
    operation: "listMembers",
    invoke: (client) =>
      client.organizations.listMembers(sampleIds.organizationId, { skip: 0, take: 25 }),
  },
  {
    client: "organizations",
    operation: "updateMemberRole",
    invoke: (client) =>
      client.organizations.updateMemberRole(sampleIds.organizationId, sampleIds.userId, {
        role: "OrganizationOwner",
      }),
  },
  {
    client: "organizations",
    operation: "removeMember",
    invoke: (client) =>
      client.organizations.removeMember(sampleIds.organizationId, sampleIds.userId),
  },
  {
    client: "organizations",
    operation: "createProject",
    invoke: (client) =>
      client.organizations.createProject(sampleIds.organizationId, {
        name: "Primary",
        regions: ["gru"],
        backupRegion: "gru",
      }),
  },
  {
    client: "organizations",
    operation: "getProject",
    defaultProject: true,
    invoke: (client) => client.organizations.getProject(),
  },
  {
    client: "organizations",
    operation: "listProjectRegionPlacements",
    defaultProject: true,
    invoke: (client) => client.organizations.listProjectRegionPlacements(),
  },
  {
    client: "organizations",
    operation: "updateProject",
    invoke: (client) =>
      client.organizations.updateProject(sampleIds.projectId, {
        name: "Core",
        backupRegion: "gru",
      }),
  },
  {
    client: "organizations",
    operation: "deleteProject",
    invoke: (client) =>
      client.organizations.deleteProject(sampleIds.projectId, {
        confirm: true,
        projectName: "Core",
      }),
  },
  {
    client: "organizations",
    operation: "listInvitations",
    invoke: (client) =>
      client.organizations.listInvitations(sampleIds.organizationId, { skip: 0, take: 25 }),
  },
  {
    client: "organizations",
    operation: "createInvitation",
    invoke: (client) =>
      client.organizations.createInvitation(sampleIds.organizationId, {
        email: "grace@example.test",
        responseUrlTemplate:
          "https://app.example.test/invitations/{invitationId}?organizationId={organizationId}",
      }),
  },
  {
    client: "organizations",
    operation: "acceptInvitation",
    invoke: (client) =>
      client.organizations.acceptInvitation(sampleIds.organizationId, sampleIds.invitationId),
  },
  {
    client: "organizations",
    operation: "declineInvitation",
    invoke: (client) =>
      client.organizations.declineInvitation(sampleIds.organizationId, sampleIds.invitationId),
  },
  {
    client: "organizations",
    operation: "resendInvitation",
    invoke: (client) =>
      client.organizations.resendInvitation(sampleIds.organizationId, sampleIds.invitationId),
  },
  {
    client: "organizations",
    operation: "cancelInvitation",
    invoke: (client) =>
      client.organizations.cancelInvitation(sampleIds.organizationId, sampleIds.invitationId),
  },
  {
    client: "regions",
    operation: "list",
    invoke: (client) => client.regions.list(),
  },
  {
    client: "regions",
    operation: "get",
    invoke: (client) => client.regions.get("sfo"),
  },
  {
    client: "savedQueries",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.savedQueries.list(),
  },
  {
    client: "savedQueries",
    operation: "create",
    defaultProject: true,
    invoke: (client) =>
      client.savedQueries.create({
        name: "Active users",
        query: "SELECT * FROM users WHERE active = true",
        repositoryId: sampleIds.repositoryId,
      }),
  },
  {
    client: "savedQueries",
    operation: "update",
    defaultProject: true,
    invoke: (client) =>
      client.savedQueries.update(sampleIds.savedQueryId, {
        name: "Active users",
        query: "SELECT * FROM users WHERE active = true",
      }),
  },
  {
    client: "savedQueries",
    operation: "delete",
    defaultProject: true,
    invoke: (client) => client.savedQueries.delete(sampleIds.savedQueryId),
  },
  {
    client: "savedQueries.v2",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.savedQueries.v2.list(),
  },
  {
    client: "savedQueries.v2",
    operation: "get",
    defaultProject: true,
    invoke: (client) => client.savedQueries.v2.get(sampleIds.savedQueryId),
  },
  {
    client: "savedQueries.v2",
    operation: "update",
    defaultProject: true,
    invoke: (client) =>
      client.savedQueries.v2.update(sampleIds.savedQueryId, {
        name: "Active users",
        query: "SELECT * FROM users WHERE active = true",
        revision: "opaque-revision",
      }),
  },
  {
    client: "savedQueries.v3",
    operation: "list",
    defaultProject: true,
    invoke: (client) =>
      client.savedQueries.v3.list(undefined, { cursor: "opaque-snapshot-cursor", take: 100 }),
  },
  {
    client: "databases",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.databases.list(),
  },
  {
    client: "databases",
    operation: "create",
    defaultProject: true,
    invoke: (client) => client.databases.create({ name: "primary" }),
  },
  {
    client: "databases",
    operation: "get",
    defaultProject: true,
    invoke: (client) => client.databases.get(sampleIds.databaseId),
  },
  {
    client: "databases",
    operation: "delete",
    defaultProject: true,
    invoke: (client) => client.databases.delete(sampleIds.databaseId, { confirm: true }),
  },
  {
    client: "databaseBranches",
    operation: "getCapability",
    defaultProject: true,
    invoke: (client) => client.databaseBranches.getCapability(),
  },
  {
    client: "databaseBranches",
    operation: "requestClone",
    defaultProject: true,
    invoke: (client) => client.databaseBranches.requestClone({ sourceDatabaseId: sampleIds.databaseId }),
  },
  {
    client: "queues",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.queues.list(),
  },
  {
    client: "queues",
    operation: "create",
    defaultProject: true,
    invoke: (client) => client.queues.create({ name: "jobs", kind: "queue" }),
  },
  {
    client: "queues",
    operation: "get",
    defaultProject: true,
    invoke: (client) => client.queues.get(sampleIds.queueId),
  },
  {
    client: "queues",
    operation: "delete",
    defaultProject: true,
    invoke: (client) => client.queues.delete(sampleIds.queueId),
  },
  {
    client: "queues",
    operation: "purge",
    defaultProject: true,
    invoke: (client) => client.queues.purge(sampleIds.queueId),
  },
  {
    client: "queues",
    operation: "publish",
    defaultProject: true,
    invoke: (client) => client.queues.publish(sampleIds.queueId, { data: { task: "index" } }),
  },
  {
    client: "queues",
    operation: "peek",
    defaultProject: true,
    invoke: (client) => client.queues.peek(sampleIds.queueId),
  },
  {
    client: "queues",
    operation: "receive",
    defaultProject: true,
    invoke: (client) =>
      client.queues.receive(sampleIds.queueId, { consumerName: "worker", maxMessages: 1 }),
  },
  {
    client: "queues",
    operation: "complete",
    defaultProject: true,
    invoke: (client) =>
      client.queues.complete(sampleIds.queueId, { receipt: "opaque", region: "sfo" }),
  },
  {
    client: "queues",
    operation: "abandon",
    defaultProject: true,
    invoke: (client) =>
      client.queues.abandon(sampleIds.queueId, { receipt: "opaque", region: "sfo" }, 1),
  },
  {
    client: "queues",
    operation: "deadLetter",
    defaultProject: true,
    invoke: (client) =>
      client.queues.deadLetter(sampleIds.queueId, { receipt: "opaque", region: "sfo" }),
  },
  {
    client: "queues",
    operation: "renewLock",
    defaultProject: true,
    invoke: (client) =>
      client.queues.renewLock(sampleIds.queueId, { receipt: "opaque", region: "sfo" }),
  },
  {
    client: "databases",
    operation: "listEnvironments",
    defaultProject: true,
    invoke: (client) => client.databases.listEnvironments(sampleIds.databaseId),
  },
  {
    client: "databases",
    operation: "createEnvironment",
    defaultProject: true,
    invoke: (client) =>
      client.databases.createEnvironment(sampleIds.databaseId, {
        name: "Preview",
        kind: "Preview",
      }),
  },
  {
    client: "databases",
    operation: "getEnvironment",
    defaultProject: true,
    invoke: (client) =>
      client.databases.getEnvironment(sampleIds.databaseId, sampleIds.environmentId),
  },
  {
    client: "databases",
    operation: "deleteEnvironment",
    defaultProject: true,
    invoke: (client) =>
      client.databases.deleteEnvironment(sampleIds.databaseId, sampleIds.environmentId),
  },
  {
    client: "repositories",
    operation: "list",
    invoke: (client) => client.repositories.list(),
  },
  {
    client: "repositories",
    operation: "create",
    invoke: (client) =>
      client.repositories.create({
        name: "users",
        databaseId: sampleIds.databaseId,
        environmentId: sampleIds.environmentId,
        collection: "Users",
      }),
  },
  {
    client: "repositories",
    operation: "get",
    invoke: (client) => client.repositories.get({ repositoryId: sampleIds.repositoryId }),
  },
  {
    client: "repositories",
    operation: "delete",
    invoke: (client) =>
      client.repositories.delete({ repositoryId: sampleIds.repositoryId }, { deleteDocuments: true }),
  },
  {
    client: "backups",
    operation: "configurePolicy",
    defaultProject: true,
    invoke: (client) =>
      client.backups.configurePolicy(sampleIds.databaseId, {
        fullBackupCron: "0 2 * * *",
        incrementalBackupCron: "0 */6 * * *",
        retentionDays: 30,
        disabled: false,
      }),
  },
  {
    client: "backups",
    operation: "planBackup",
    defaultProject: true,
    invoke: (client) =>
      client.backups.planBackup(sampleIds.databaseId, {
        operationSessionId: "backup-operation-session",
      }),
  },
  {
    client: "backups",
    operation: "executeBackup",
    defaultProject: true,
    invoke: (client) =>
      client.backups.executeBackup(
        sampleIds.databaseId,
        {
          operationSessionId: "backup-operation-session",
          confirmationToken: "backup-confirmation-token",
        },
        "backup-execution-key",
      ),
  },
  {
    client: "backups",
    operation: "listBackups",
    defaultProject: true,
    invoke: (client) => client.backups.listBackups(sampleIds.databaseId, { skip: 0, take: 25 }),
  },
  {
    client: "backups",
    operation: "getBackup",
    defaultProject: true,
    invoke: (client) => client.backups.getBackup(sampleIds.databaseId, sampleIds.backupId),
  },
  {
    client: "backups",
    operation: "listRestorePoints",
    defaultProject: true,
    invoke: (client) => client.backups.listRestorePoints(sampleIds.databaseId),
  },
  {
    client: "backups",
    operation: "startRestore",
    defaultProject: true,
    invoke: (client) =>
      client.backups.startRestore(sampleIds.databaseId, {
        backupId: sampleIds.backupId,
        idempotencyKey: "restore-1",
      }),
  },
  {
    client: "backups",
    operation: "getRestore",
    defaultProject: true,
    invoke: (client) => client.backups.getRestore(sampleIds.databaseId, sampleIds.restoreId),
  },
  {
    client: "backups",
    operation: "listRestores",
    defaultProject: true,
    invoke: (client) => client.backups.listRestores(sampleIds.databaseId, { skip: 0, take: 25 }),
  },
  {
    client: "documents",
    operation: "list",
    invoke: (client) =>
      client.documents.list({ repositoryId: sampleIds.repositoryId }, { skip: 0, take: 25 }),
  },
  {
    client: "documents",
    operation: "create",
    invoke: (client) =>
      client.documents.create({ repositoryId: sampleIds.repositoryId }, { name: "Ada" }),
  },
  {
    client: "documents",
    operation: "get",
    invoke: (client) =>
      client.documents.get({ repositoryId: sampleIds.repositoryId }, sampleIds.documentId),
  },
  {
    client: "documents",
    operation: "put",
    invoke: (client) =>
      client.documents.put(
        { repositoryId: sampleIds.repositoryId },
        sampleIds.documentId,
        { name: "Grace" },
      ),
  },
  {
    client: "documents",
    operation: "patch",
    invoke: (client) =>
      client.documents.patch(
        { repositoryId: sampleIds.repositoryId },
        sampleIds.documentId,
        { title: "Engineer" },
      ),
  },
  {
    client: "documents",
    operation: "batch",
    invoke: (client) =>
      client.documents.batch(
        { repositoryId: sampleIds.repositoryId },
        { operations: [{ operation: "create", data: { name: "Katherine" } }] },
      ),
  },
  {
    client: "documents",
    operation: "delete",
    invoke: (client) =>
      client.documents.delete({ repositoryId: sampleIds.repositoryId }, sampleIds.documentId),
  },
  {
    client: "query",
    operation: "execute",
    invoke: (client) =>
      client.query.execute({ repositoryId: sampleIds.repositoryId }, { query: "from Users" }),
  },
  {
    client: "schema",
    operation: "get",
    invoke: (client) => client.schema.get({ repositoryId: sampleIds.repositoryId }),
  },
  {
    client: "schema",
    operation: "validate",
    invoke: (client) =>
      client.schema.validate({ repositoryId: sampleIds.repositoryId }, { maxErrorMessages: 5 }),
  },
  {
    client: "schema",
    operation: "configure",
    invoke: (client) =>
      client.schema.configure(
        { repositoryId: sampleIds.repositoryId },
        { enabled: true, schema: "{\"type\":\"object\"}" },
      ),
  },
  {
    client: "apiKeys",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.apiKeys.list(undefined, { cursor: "next", limit: 25 }),
  },
  {
    client: "apiKeys",
    operation: "create",
    defaultProject: true,
    invoke: (client) =>
      client.apiKeys.create({
        name: "ci",
        scopes: ["repositories:read"],
        databaseId: sampleIds.databaseId,
        environmentId: sampleIds.environmentId,
      }),
  },
  {
    client: "apiKeys",
    operation: "rotate",
    defaultProject: true,
    invoke: (client) => client.apiKeys.rotate(sampleIds.apiKeyId),
  },
  {
    client: "apiKeys",
    operation: "revoke",
    defaultProject: true,
    invoke: (client) => client.apiKeys.revoke(sampleIds.apiKeyId),
  },
  {
    client: "m2m",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.m2m.list(undefined, { cursor: "next", limit: 25 }),
  },
  {
    client: "m2m",
    operation: "create",
    defaultProject: true,
    invoke: (client) =>
      client.m2m.create({
        name: "worker",
        scopes: ["documents:write"],
        databaseId: sampleIds.databaseId,
        environmentId: sampleIds.environmentId,
      }),
  },
  {
    client: "m2m",
    operation: "rotate",
    defaultProject: true,
    invoke: (client) => client.m2m.rotate(sampleIds.clientId),
  },
  {
    client: "m2m",
    operation: "revoke",
    defaultProject: true,
    invoke: (client) => client.m2m.revoke(sampleIds.clientId),
  },
  {
    client: "m2m",
    operation: "createToken",
    invoke: (client) =>
      client.m2m.createToken(sampleIds.projectId, {
        clientId: sampleIds.clientId,
        clientSecret: "client-secret",
        scope: "documents:write",
      }),
  },
  {
    client: "access",
    operation: "listTeamMembers",
    defaultProject: true,
    invoke: (client) => client.access.listTeamMembers(undefined, { skip: 0, take: 25 }),
  },
  {
    client: "access",
    operation: "addTeamMember",
    defaultProject: true,
    invoke: (client) => client.access.addTeamMember({ userId: sampleIds.userId, role: "owner" }),
  },
  {
    client: "access",
    operation: "updateTeamMember",
    defaultProject: true,
    invoke: (client) => client.access.updateTeamMember(sampleIds.userId, { role: "member" }),
  },
  {
    client: "access",
    operation: "removeTeamMember",
    defaultProject: true,
    invoke: (client) => client.access.removeTeamMember(sampleIds.userId),
  },
  {
    client: "usage",
    operation: "getProjectUsage",
    defaultProject: true,
    invoke: (client) => client.usage.getProjectUsage(),
  },
  {
    client: "usage",
    operation: "getOrganizationUsage",
    invoke: (client) => client.usage.getOrganizationUsage(sampleIds.organizationId),
  },
  {
    client: "usage",
    operation: "getProjectUsageWorkload",
    defaultProject: true,
    invoke: (client) => client.usage.getProjectUsageWorkload("24h"),
  },
  {
    client: "usage",
    operation: "getProjectLatency",
    defaultProject: true,
    invoke: (client) => client.usage.getProjectLatency(undefined, { latitude: -23.55, longitude: -46.63 }),
  },
  {
    client: "usage",
    operation: "getProjectUsageHistory",
    defaultProject: true,
    invoke: (client) => client.usage.getProjectUsageHistory("30d"),
  },
  {
    client: "billing",
    operation: "getAccount",
    invoke: (client) => client.billing.getAccount(sampleIds.organizationId),
  },
  {
    client: "billing",
    operation: "listInvoices",
    invoke: (client) =>
      client.billing.listInvoices(sampleIds.organizationId, { skip: 0, take: 25 }),
  },
  {
    client: "billing",
    operation: "listAccess",
    invoke: (client) => client.billing.listAccess(sampleIds.organizationId),
  },
  {
    client: "billing",
    operation: "setAccess",
    invoke: (client) =>
      client.billing.setAccess(sampleIds.organizationId, sampleIds.userId, true),
  },
  {
    client: "billing",
    operation: "createCheckoutSession",
    invoke: (client) =>
      client.billing.createCheckoutSession(sampleIds.organizationId, {
        planId: sampleIds.planId,
        billingInterval: "monthly",
        provider: "polar",
        quantity: 1,
      }),
  },
  {
    client: "billing",
    operation: "createPortalSession",
    invoke: (client) =>
      client.billing.createPortalSession(
        sampleIds.organizationId,
        "https://app.example.test/billing",
      ),
  },
  {
    client: "pricing",
    operation: "listPlans",
    invoke: (client) => client.pricing.listPlans(),
  },
  {
    client: "audit",
    operation: "list",
    defaultProject: true,
    invoke: (client) => client.audit.list(undefined, { skip: 0, take: 25 }),
  },
  {
    client: "audit",
    operation: "get",
    defaultProject: true,
    invoke: (client) => client.audit.get(sampleIds.auditEventId),
  },
  {
    client: "notifications",
    operation: "list",
    invoke: (client) =>
      client.notifications.list({
        skip: 0,
        take: 25,
        unreadOnly: true,
        organizationId: sampleIds.organizationId,
      }),
  },
  {
    client: "notifications",
    operation: "markRead",
    invoke: (client) => client.notifications.markRead(sampleIds.notificationId),
  },
  {
    client: "notifications",
    operation: "markAllRead",
    invoke: (client) =>
      client.notifications.markAllRead({ organizationId: sampleIds.organizationId }),
  },
  {
    client: "security",
    operation: "getSettings",
    defaultProject: true,
    invoke: (client) => client.security.getSettings(),
  },
  {
    client: "security",
    operation: "updateSettings",
    defaultProject: true,
    invoke: (client) =>
      client.security.updateSettings({
        mfaPolicy: "Required",
        ipAllowlist: "10.0.0.0/8",
        allowedEmailDomains: "terrascale.tech",
        dataResidency: "gru",
        authenticationEnabled: false,
        confirmDisableAuthentication: true,
      }),
  },
];
