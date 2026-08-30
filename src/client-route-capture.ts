import { createTerraScaleClient, type TerraScaleClient } from "./factory.js";
import type { FetchLike } from "./config.js";
import {
  type TerraScaleBase,
  type TerraScaleRequestMetric,
  type TerraScaleRouteName,
} from "./http.js";
import { toUlid } from "./ids.js";

const sampleIds = {
  auditEventId: toUlid("01FRZ3NDEKTSV4RRFFQ69G5FAV"),
  backupId: toUlid("01NRZ3NDEKTSV4RRFFQ69G5FAV"),
  databaseId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAW"),
  documentId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAZ"),
  environmentId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAX"),
  invitationId: toUlid("01GRZ3NDEKTSV4RRFFQ69G5FAV"),
  notificationId: toUlid("01DRZ3NDEKTSV4RRFFQ69G5FAV"),
  organizationId: toUlid("01ERZ3NDEKTSV4RRFFQ69G5FAV"),
  projectId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV"),
  queueId: toUlid("01PRZ3NDEKTSV4RRFFQ69G5FAV"),
  repositoryId: toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAY"),
  restoreId: toUlid("01MRZ3NDEKTSV4RRFFQ69G5FAV"),
  savedQueryId: toUlid("01SRZ3NDEKTSV4RRFFQ69G5FAV"),
  userId: toUlid("01KRZ3NDEKTSV4RRFFQ69G5FAV"),
} as const;

const pathTemplateSegments = new Map<string, string>(
  Object.entries(sampleIds).map(([name, value]) => [value, `{${name}}`]),
);
pathTemplateSegments.set("SFO", "{regionCode}");

export type CapturedSdkRoute = {
  readonly base: TerraScaleBase;
  readonly client: string;
  readonly method: string;
  readonly operation: string;
  readonly pathTemplate: string;
  readonly routeName: TerraScaleRouteName;
  readonly sourceContract: {
    readonly body: { readonly kind: "void" } | { readonly kind: "json"; readonly shape: unknown };
    readonly headers: readonly { readonly name: string; readonly value: string }[];
    readonly query: readonly { readonly name: string; readonly value: string }[];
  };
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
  return Promise.all(captureOperations.map((operation) => captureRoute(operation)));
}

export async function exercisePublicClientRoutes(
  fetch: FetchLike,
): Promise<readonly CapturedSdkRoute[]> {
  assertCaptureOperationCoverage();
  const routes: CapturedSdkRoute[] = [];
  for (const operation of captureOperations) {
    routes.push(await captureRoute(operation, fetch, true));
  }
  return routes;
}

async function captureRoute(
  operation: CaptureOperation,
  routeFetch?: FetchLike,
  assertRouted = false,
): Promise<CapturedSdkRoute> {
  let capturedRequest: Request | undefined;
  let capturedMetric: TerraScaleRequestMetric | undefined;

  const captureFetch: FetchLike = async (input) => {
    const request = input instanceof Request ? input : new Request(input);
    capturedRequest = request.clone();

    return routeFetch === undefined
      ? new Response("{}", {
          status: 200,
          headers: { "content-type": "application/json" },
        })
      : routeFetch(request);
  };

  const baseConfig = {
    apiBaseUrl: "https://api.terrascale.test",
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

  const result = await operation.invoke(client);

  if (
    assertRouted
    && isRoutingFailure(result)
    && !(operation.client === "databaseBranches" && operation.operation === "requestClone")
  ) {
    throw new Error(
      `Local route exercise failed for ${operation.client}.${operation.operation}: ${result.error.code} (${result.error.kind}).`,
    );
  }

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
    sourceContract: await capturedSourceContract(capturedRequest),
  };
}

function isRoutingFailure(value: unknown): value is {
  readonly error: { readonly code: string; readonly kind: string };
  readonly ok: false;
} {
  if (typeof value !== "object" || value === null || !("ok" in value) || value.ok !== false) {
    return false;
  }
  if (!("error" in value) || typeof value.error !== "object" || value.error === null) {
    return true;
  }
  const error = value.error as { readonly code?: unknown; readonly kind?: unknown };
  return error.code === "route_not_found"
    || error.code === "local_emulator_error"
    || error.kind === "invalid-response"
    || error.kind === "server"
    || error.kind === "transport";
}

async function capturedSourceContract(request: Request): Promise<CapturedSdkRoute["sourceContract"]> {
  const text = await request.clone().text();
  return {
    body: text.length === 0 ? { kind: "void" } : { kind: "json", shape: JSON.parse(text) },
    headers: canonicalContractHeaders(request.headers),
    query: [...new URL(request.url).searchParams].map(([name, value]) => contractPair(name, value)).sort(compareContractPair),
  };
}

const safeContractValueNames = new Set([
  "accept", "content-type", "cursor", "deletedocuments", "idempotency-key", "latitude", "limit", "longitude", "maxmessages", "organizationid", "range", "skip", "take", "unreadonly", "x-terrascale-delete-confirmation", "x-terrascale-preferred-region", "x-terrascale-project-id",
]);

export function canonicalContractHeaders(headers: Headers): readonly { readonly name: string; readonly value: string }[] {
  return [...headers].map(([name, value]) => contractPair(name, value)).sort(compareContractPair);
}

function contractPair(name: string, value: string): { readonly name: string; readonly value: string } {
  return { name, value: safeContractValueNames.has(name.toLowerCase()) ? value : "<redacted>" };
}

function compareContractPair(left: { readonly name: string; readonly value: string }, right: { readonly name: string; readonly value: string }): number {
  return left.name.localeCompare(right.name) || left.value.localeCompare(right.value);
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

const captureOperations: readonly CaptureOperation[] = [
  {
    client: "onboarding",
    operation: "getState",
    invoke: (client) => client.onboarding.getState(),
  },
  {
    client: "onboarding",
    operation: "createOrganization",
    invoke: (client) => client.onboarding.createOrganization({
      name: "Analytical Engines",
    }),
  },
  {
    client: "onboarding",
    operation: "createProject",
    invoke: (client) =>
      client.onboarding.createProject({
        name: "Primary",
        regions: ["GRU"],
        backupRegion: "GRU",
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
    invoke: (client) => client.organizations.create({
      name: "Analytical Engines",
    }),
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
        regions: ["GRU"],
        backupRegion: "GRU",
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
        backupRegion: "GRU",
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
        projectId: sampleIds.projectId,
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
    invoke: (client) => client.regions.get("SFO"),
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
    operation: "get",
    defaultProject: true,
    invoke: (client) => client.savedQueries.get(sampleIds.savedQueryId),
  },
  {
    client: "savedQueries",
    operation: "update",
    defaultProject: true,
    invoke: (client) =>
      client.savedQueries.update(sampleIds.savedQueryId, {
        name: "Active users",
        query: "SELECT * FROM users WHERE active = true",
        revision: "opaque-revision",
      }),
  },
  {
    client: "savedQueries",
    operation: "delete",
    defaultProject: true,
    invoke: (client) => client.savedQueries.delete(sampleIds.savedQueryId),
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
      client.queues.complete(sampleIds.queueId, { receipt: "opaque", region: "SFO" }),
  },
  {
    client: "queues",
    operation: "abandon",
    defaultProject: true,
    invoke: (client) =>
      client.queues.abandon(sampleIds.queueId, { receipt: "opaque", region: "SFO" }, 1),
  },
  {
    client: "queues",
    operation: "deadLetter",
    defaultProject: true,
    invoke: (client) =>
      client.queues.deadLetter(sampleIds.queueId, { receipt: "opaque", region: "SFO" }),
  },
  {
    client: "queues",
    operation: "renewLock",
    defaultProject: true,
    invoke: (client) =>
      client.queues.renewLock(sampleIds.queueId, { receipt: "opaque", region: "SFO" }),
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
];
