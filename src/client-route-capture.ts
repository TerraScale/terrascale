import { createTerraScaleClient, type TerraScaleClient } from "./factory.js";
import type { FetchLike } from "./config.js";
import {
  setTerraScaleRequestMetricRecorder,
  type TerraScaleBase,
  type TerraScaleRouteName,
} from "./http.js";
import { toUlid, type Ulid } from "./ids.js";

const sampleIds = {
  apiKeyId: toUlid("01BRZ3NDEKTSV4RRFFQ69G5FAV"),
  auditEventId: toUlid("01FRZ3NDEKTSV4RRFFQ69G5FAV"),
  clientId: toUlid("01CRZ3NDEKTSV4RRFFQ69G5FAV"),
  databaseId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAW"),
  documentId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAZ"),
  environmentId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAX"),
  invitationId: toUlid("01GRZ3NDEKTSV4RRFFQ69G5FAV"),
  notificationId: toUlid("01DRZ3NDEKTSV4RRFFQ69G5FAV"),
  organizationId: toUlid("01ERZ3NDEKTSV4RRFFQ69G5FAV"),
  planId: toUlid("01HRZ3NDEKTSV4RRFFQ69G5FAV"),
  projectId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV"),
  repositoryId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAY"),
  sessionId: toUlid("01JRZ3NDEKTSV4RRFFQ69G5FAV"),
  userId: toUlid("01KRZ3NDEKTSV4RRFFQ69G5FAV"),
} as const;

const pathTemplateSegments = new Map<string, string>(
  Object.entries(sampleIds).map(([name, value]) => [value, `{${name}}`]),
);

export type CapturedSdkRoute = {
  readonly base: TerraScaleBase;
  readonly client: string;
  readonly method: string;
  readonly operation: string;
  readonly path: string;
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

type CapturedRequest = {
  readonly method: string;
  readonly pathname: string;
};

type CapturedMetric = {
  readonly base: TerraScaleBase;
  readonly routeName: TerraScaleRouteName;
};

type PublicClientMethod = {
  readonly canonicalKey: string;
  readonly key: string;
};

export async function capturePublicClientRoutes(): Promise<readonly CapturedSdkRoute[]> {
  assertCaptureOperationCoverage();

  const routes: CapturedSdkRoute[] = [];

  try {
    for (const operation of captureOperations) {
      routes.push(await captureRoute(operation));
    }
  } finally {
    setTerraScaleRequestMetricRecorder(undefined);
  }

  return routes;
}

function routeOperation(operation: string, variant: string | undefined): string {
  return variant === undefined ? operation : `${operation}:${variant}`;
}

async function captureRoute(operation: CaptureOperation): Promise<CapturedSdkRoute> {
  let capturedRequest: CapturedRequest | undefined;
  let capturedMetric: CapturedMetric | undefined;

  setTerraScaleRequestMetricRecorder((metric) => {
    capturedMetric = {
      base: metric.base,
      routeName: metric.routeName,
    };
  });

  const captureFetch: FetchLike = async (input) => {
    const request = input instanceof Request ? input : new Request(input);
    const url = new URL(request.url);
    capturedRequest = {
      method: request.method,
      pathname: url.pathname,
    };

    return new Response("{}", {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  };

  const baseConfig = {
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    dashboardClientId: "sdk-route-manifest",
    fetch: captureFetch,
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
    operation: routeOperation(operation.operation, operation.variant),
    path: capturedRequest.pathname,
    pathTemplate: pathToTemplate(capturedRequest.pathname),
    routeName: capturedMetric.routeName,
  };
}

function assertCaptureOperationCoverage(): void {
  const publicMethods = enumeratePublicClientMethods();
  const publicMethodsByKey = new Map(publicMethods.map((method) => [method.key, method]));
  const capturedKeys = new Set(captureOperations.map(captureOperationKey));
  const coveredCanonicalKeys = new Set<string>();
  const staleCaptureKeys = [...capturedKeys].filter((key) => {
    const publicMethod = publicMethodsByKey.get(key);

    if (publicMethod !== undefined) {
      coveredCanonicalKeys.add(publicMethod.canonicalKey);
      return false;
    }

    return true;
  });
  const uncoveredMethods = publicMethods.filter(
    (method) => !coveredCanonicalKeys.has(method.canonicalKey),
  );

  if (staleCaptureKeys.length === 0 && uncoveredMethods.length === 0) {
    return;
  }

  const lines = ["Public client route capture coverage is incomplete."];

  if (uncoveredMethods.length > 0) {
    lines.push("Uncovered public client methods:");
    lines.push(...uniqueSorted(uncoveredMethods.map((method) => method.key)).map((key) => `  - ${key}`));
  }

  if (staleCaptureKeys.length > 0) {
    lines.push("Capture operations reference methods that no longer exist on the public client surface:");
    lines.push(...uniqueSorted(staleCaptureKeys).map((key) => `  - ${key}`));
  }

  throw new Error(lines.join("\n"));
}

function enumeratePublicClientMethods(): readonly PublicClientMethod[] {
  const client = createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    dashboardClientId: "sdk-route-manifest",
    fetch: async () =>
      new Response("{}", {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
  });
  const canonicalClientNamesByInstance = new Map<object, string>();
  const methods: PublicClientMethod[] = [];

  for (const [clientName, value] of Object.entries(client)) {
    if (!isObject(value)) {
      continue;
    }

    const methodNames = publicMethodNames(value);

    if (methodNames.length === 0) {
      continue;
    }

    const canonicalClientName = canonicalClientNamesByInstance.get(value) ?? clientName;
    canonicalClientNamesByInstance.set(value, canonicalClientName);

    for (const methodName of methodNames) {
      methods.push({
        canonicalKey: `${canonicalClientName}.${methodName}`,
        key: `${clientName}.${methodName}`,
      });
    }
  }

  return methods.sort((left, right) => left.key.localeCompare(right.key));
}

function publicMethodNames(value: object): readonly string[] {
  const names = new Set<string>();

  for (
    let prototype = Object.getPrototypeOf(value) as object | null;
    prototype !== null && prototype !== Object.prototype;
    prototype = Object.getPrototypeOf(prototype) as object | null
  ) {
    for (const [name, descriptor] of Object.entries(Object.getOwnPropertyDescriptors(prototype))) {
      if (name === "constructor" || typeof descriptor.value !== "function") {
        continue;
      }

      names.add(name);
    }
  }

  return [...names].sort();
}

function captureOperationKey(operation: CaptureOperation): string {
  return `${operation.client}.${operation.operation}`;
}

function isObject(value: unknown): value is object {
  return typeof value === "object" && value !== null;
}

function uniqueSorted(values: readonly string[]): readonly string[] {
  return [...new Set(values)].sort();
}

function pathToTemplate(pathname: string): string {
  return pathname
    .split("/")
    .map((segment) => {
      let templated = segment;

      for (const [value, template] of pathTemplateSegments.entries()) {
        templated = templated.replaceAll(value, template);
      }

      return templated;
    })
    .join("/");
}

const registerRequest = {
  email: "ada@example.test",
  password: "correct horse battery staple",
  firstName: "Ada",
  lastName: "Lovelace",
  verifyUrl: "https://dashboard.terrascale.test/verify",
};

const captureOperations: readonly CaptureOperation[] = [
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
        "https://dashboard.terrascale.test/verify",
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
        "https://dashboard.terrascale.test/verify",
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
        resetUrl: "https://dashboard.terrascale.test/reset",
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
        resetUrl: "https://dashboard.terrascale.test/reset",
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
        redirectUrl: "https://dashboard.terrascale.test/login",
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
        redirectUrl: "https://dashboard.terrascale.test/login",
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
    operation: "listProjects",
    invoke: (client) =>
      client.organizations.listProjects(sampleIds.organizationId, { skip: 0, take: 25 }),
  },
  {
    client: "organizations",
    operation: "listUsers",
    invoke: (client) =>
      client.organizations.listUsers(sampleIds.organizationId, { skip: 0, take: 25 }),
  },
  {
    client: "organizations",
    operation: "createProject",
    invoke: (client) =>
      client.organizations.createProject(sampleIds.organizationId, { name: "Primary" }),
  },
  {
    client: "organizations",
    operation: "getProject",
    defaultProject: true,
    invoke: (client) => client.organizations.getProject(),
  },
  {
    client: "organizations",
    operation: "updateProject",
    invoke: (client) => client.organizations.updateProject(sampleIds.projectId, { name: "Core" }),
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
        role: "admin",
      }),
  },
  {
    client: "organizations",
    operation: "revokeInvitation",
    invoke: (client) =>
      client.organizations.revokeInvitation(sampleIds.organizationId, sampleIds.invitationId),
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
    invoke: (client) => client.databases.create({ name: "primary", regions: ["gru"] }),
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
    invoke: (client) => client.databases.delete(sampleIds.databaseId),
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
    invoke: (client) => client.access.addTeamMember({ userId: sampleIds.userId, role: "admin" }),
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
    operation: "getProjectUsageWorkload",
    defaultProject: true,
    invoke: (client) => client.usage.getProjectUsageWorkload("24h"),
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
    operation: "createCheckoutSession",
    invoke: (client) =>
      client.billing.createCheckoutSession(sampleIds.organizationId, {
        successUrl: "https://dashboard.terrascale.test/success",
        cancelUrl: "https://dashboard.terrascale.test/cancel",
      }),
  },
  {
    client: "billing",
    operation: "createPortalSession",
    invoke: (client) =>
      client.billing.createPortalSession(
        sampleIds.organizationId,
        "https://dashboard.terrascale.test/billing",
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
    invoke: (client) => client.notifications.list({ skip: 0, take: 25, unreadOnly: true }),
  },
  {
    client: "notifications",
    operation: "markRead",
    invoke: (client) => client.notifications.markRead(sampleIds.notificationId),
  },
  {
    client: "notifications",
    operation: "markAllRead",
    invoke: (client) => client.notifications.markAllRead(),
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
      }),
  },
];
