import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import { capturePublicClientRoutes } from "./client-route-capture.js";
import { createTerraScaleClient } from "./factory.js";
import type { TerraScaleClient } from "./factory.js";
import { toUlid } from "./ids.js";
import type {
  DatabaseEnvironment,
  DatabaseBranchCapabilityResponse,
  CreateDatabaseBranchRequest,
  DeletedProjectDatabaseEnvironmentResponse,
  JsonValue,
  PricingPlan,
  ProjectSavedQueryResponse,
  ProjectSavedQuerySnapshotPageResponse,
  ProjectSavedQueryV2Response,
  UpdateProjectSavedQueryV2Request,
} from "./models.js";

const projectId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const databaseId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAW");
const environmentId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAX");
const repositoryId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAY");
const documentId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAZ");
const keyId = toUlid("01BRZ3NDEKTSV4RRFFQ69G5FAV");
const clientId = toUlid("01CRZ3NDEKTSV4RRFFQ69G5FAV");
const notificationId = toUlid("01DRZ3NDEKTSV4RRFFQ69G5FAV");
const organizationId = toUlid("01ERZ3NDEKTSV4RRFFQ69G5FAV");
const backupPolicyId = toUlid("01NRZ3NDEKTSV4RRFFQ69G5FAW");
const backupId = toUlid("01NRZ3NDEKTSV4RRFFQ69G5FAV");
const restoreId = toUlid("01MRZ3NDEKTSV4RRFFQ69G5FAV");
const restoredDatabaseId = toUlid("01QRZ3NDEKTSV4RRFFQ69G5FAV");
const savedQueryId = toUlid("01SRZ3NDEKTSV4RRFFQ69G5FAV");
const otherProjectId = toUlid("01TRZ3NDEKTSV4RRFFQ69G5FAV");

const savedQueryWithoutRepository = {
  savedQueryId,
  projectId,
  name: "Missing repository field",
  query: "SELECT 1",
  createdAt: "2026-08-05T00:00:00Z",
  updatedAt: "2026-08-05T00:00:00Z",
};

const pricingPlan = {
  planId: toUlid("01HRZ3NDEKTSV4RRFFQ69G5FAV"),
  name: "Developer",
  currency: "USD",
  monthlyBaseCents: 1000,
  annualBaseCents: 10000,
  includedUsage: [],
  key: "developer",
  provider: "polar",
  monthlyPriceId: null,
  annualPriceId: null,
  monthlyPolarProductId: null,
  annualPolarProductId: null,
  monthlyStripePriceId: null,
  annualStripePriceId: null,
  monthlyTrialDays: 14,
  annualTrialDays: 14,
  visible: true,
  maxRegions: 3,
  operationOverageCentsPerMillion: 100,
  storageOverageCentsPerGbRegion: 25,
} satisfies PricingPlan;

const pricingPlanWithoutKey: Omit<PricingPlan, "key"> = pricingPlan;
// @ts-expect-error Pricing responses always include key.
const invalidPricingPlanWithoutKey: PricingPlan = pricingPlanWithoutKey;
void invalidPricingPlanWithoutKey;

const pricingPlanWithoutVisible: Omit<PricingPlan, "visible"> = pricingPlan;
// @ts-expect-error Pricing responses always include visible.
const invalidPricingPlanWithoutVisible: PricingPlan = pricingPlanWithoutVisible;
void invalidPricingPlanWithoutVisible;

const pricingPlanWithoutStripePriceId: Omit<PricingPlan, "monthlyStripePriceId"> = pricingPlan;
// @ts-expect-error Pricing responses always include nullable provider identifier fields.
const invalidPricingPlanWithoutStripePriceId: PricingPlan = pricingPlanWithoutStripePriceId;
void invalidPricingPlanWithoutStripePriceId;

// @ts-expect-error Saved-query responses always include repositoryId, which may be null.
const invalidSavedQueryResponse: ProjectSavedQueryResponse = savedQueryWithoutRepository;
void invalidSavedQueryResponse;

// @ts-expect-error V2 updates require an opaque revision returned by a v2 read or list.
const invalidV2SavedQueryUpdate: UpdateProjectSavedQueryV2Request = {
  name: "Missing revision",
  query: "SELECT 1",
};
void invalidV2SavedQueryUpdate;

const deletingEnvironment = {
  environmentId,
  projectId,
  databaseId,
  name: "Preview",
  kind: "Preview",
  status: "Deleting",
  createdAt: "2026-08-05T00:00:00Z",
  version: 1,
} satisfies DatabaseEnvironment;
void deletingEnvironment;

type CapturedRequest = {
  readonly method: string;
  readonly url: URL;
  readonly headers: Headers;
  readonly body: unknown;
};

type ManifestRoute = {
  readonly base: string;
  readonly client: string;
  readonly method: string;
  readonly operation: string;
  readonly pathTemplate: string;
  readonly routeName: string;
};

// @ts-expect-error Unconfirmed backup triggers are intentionally not part of the public SDK.
type LegacyTriggerBackup = TerraScaleClient["backups"]["triggerBackup"];
void (undefined as unknown as LegacyTriggerBackup);

describe("TerraScaleClient route contracts", () => {
  test("keeps the generated SDK route manifest aligned with captured public routes", async () => {
    const manifestUrl = new URL("../sdk-route-manifest.json", import.meta.url);
    const manifest = JSON.parse(await readFile(manifestUrl, "utf8")) as {
      readonly routeCount: number;
      readonly routes: readonly ManifestRoute[];
    };
    const capturedRoutes = await capturePublicClientRoutes();

    assert.deepEqual(
      manifest.routes.map(routeManifestCore),
      capturedRoutes.map(routeManifestCore),
    );
    assert.equal(manifest.routeCount, 185);
    assert.equal(manifest.routeCount, capturedRoutes.length);
    assert.equal(manifest.routes.some((route) => route.routeName === "backups.trigger"), false);
    assert.deepEqual(
      manifest.routes
        .filter((route) => route.client === "backups" && route.method === "POST")
        .map((route) => [route.operation, route.pathTemplate]),
      [
        [
          "planBackup",
          "/api/v1/projects/{projectId}/databases/{databaseId}/backups:plan",
        ],
        [
          "executeBackup",
          "/api/v1/projects/{projectId}/databases/{databaseId}/backups:execute",
        ],
        ["startRestore", "/api/v1/projects/{projectId}/databases/{databaseId}/restores"],
      ],
    );
  });

  test("public factory hides private clients and exposes typed clients", async () => {
    const { client, requests } = createCapturingClient({});
    const runtimeClient = client as TerraScaleClient & {
      readonly admin?: unknown;
      readonly dashboard?: unknown;
      readonly http?: unknown;
    };

    assert.equal("admin" in runtimeClient, false);
    assert.equal("dashboard" in runtimeClient, false);
    assert.equal("http" in runtimeClient, false);
    assert.equal(runtimeClient.admin, undefined);
    assert.equal(runtimeClient.dashboard, undefined);
    assert.equal(runtimeClient.http, undefined);

    await client.regions.list();
    assertRequest(requests[0], "GET", "/api/v1/regions");
  });

  test("routes account identity to Auth and organization billing to the product API", async () => {
    const { client, requests } = createCapturingClientWithResponses([{}, {}, {}]);

    await client.auth.getSession();
    await client.auth.getCurrentBilling();
    await client.auth.evaluateBillingGuard({ requireActiveSubscription: true });

    assert.equal(requests[0]?.url.origin, "https://auth.terrascale.test");
    assertRequest(requests[0], "GET", "/api/v1/auth/me");
    assert.equal(requests[1]?.url.origin, "https://api.terrascale.test");
    assertRequest(requests[1], "GET", "/api/v1/auth/me/billing");
    assert.equal(requests[2]?.url.origin, "https://api.terrascale.test");
    assertRequest(requests[2], "POST", "/api/v1/auth/me/billing/guard");
  });

  test("omits ignored legacy checkout fields while preserving their source compatibility", async () => {
    const { client, requests } = createCapturingClient({});

    await client.billing.createCheckoutSession(organizationId, {
      priceId: "legacy-price",
      planId: pricingPlan.planId,
      billingInterval: "annual",
      provider: "stripe",
      quantity: 2,
      successUrl: "https://app.example.test/success",
      cancelUrl: "https://app.example.test/cancel",
      customerEmail: "billing@example.test",
    });

    assertRequest(
      requests[0],
      "POST",
      `/api/v1/organizations/${organizationId}/billing/checkout-sessions`,
    );
    assert.deepEqual(requests[0]?.body, {
      priceId: "legacy-price",
      planId: pricingPlan.planId,
      billingInterval: "annual",
      provider: "stripe",
      quantity: 2,
    });
  });

  test("preserves required pricing key and visibility from the public wire response", async () => {
    const { client, requests } = createCapturingClient([pricingPlan]);

    const result = await client.pricing.listPlans();

    assert.equal(result.ok, true);
    if (result.ok) {
      assert.deepEqual(result.value, [pricingPlan]);
    }
    assertRequest(requests[0], "GET", "/api/v1/pricing/plans");
  });

  test("sends onboarding checkout return URLs to the public product API", async () => {
    const { client, requests } = createCapturingClient({});

    await client.onboarding.createPaymentSession({
      successUrl: "https://app.example.test/onboarding?payment=success",
      cancelUrl: "https://app.example.test/onboarding?payment=cancelled",
    });

    assert.equal(requests[0]?.url.origin, "https://api.terrascale.test");
    assertRequest(requests[0], "POST", "/api/v1/onboarding/payment-method/checkout-session");
    assert.deepEqual(requests[0]?.body, {
      successUrl: "https://app.example.test/onboarding?payment=success",
      cancelUrl: "https://app.example.test/onboarding?payment=cancelled",
    });
  });

  test("sends the explicit development payment bypass through the typed onboarding operation", async () => {
    const { client, requests } = createCapturingClient({});

    await client.onboarding.validatePaymentMethod({ developmentBypass: true });

    assertRequest(requests[0], "POST", "/api/v1/onboarding/payment-method/validate");
    assert.deepEqual(requests[0]?.body, { developmentBypass: true });
  });

  test("uses typed public Auth routes for providers, email, and password changes", async () => {
    const { client, requests } = createCapturingClient({});

    await client.auth.listProjectProviders(undefined, { cursor: "next", limit: 25 });
    await client.auth.initiateEmailChange({
      newEmail: "augusta@example.test",
      password: "current-password",
    });
    await client.auth.verifyEmailChange({
      newEmail: "augusta@example.test",
      otpCode: "123456",
    });
    await client.auth.changePassword({
      currentPassword: "current-password",
      newPassword: "new-password",
    });

    for (const request of requests) {
      assert.equal(request.url.origin, "https://auth.terrascale.test");
    }
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/auth/providers`);
    assert.equal(requests[0]?.url.searchParams.get("cursor"), "next");
    assert.equal(requests[0]?.url.searchParams.get("limit"), "25");
    assertRequest(requests[1], "POST", "/api/v1/users/me/email/change");
    assert.deepEqual(requests[1]?.body, {
      newEmail: "augusta@example.test",
      password: "current-password",
    });
    assertRequest(requests[2], "POST", "/api/v1/users/me/email/verify");
    assert.deepEqual(requests[2]?.body, {
      newEmail: "augusta@example.test",
      otpCode: "123456",
    });
    assertRequest(requests[3], "POST", "/api/v1/auth/password/change-password");
    assert.deepEqual(requests[3]?.body, {
      currentPassword: "current-password",
      newPassword: "new-password",
    });
  });

  test("uses authenticated account-security routes and anonymous passkey exchanges", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      authBaseUrl: "https://auth.terrascale.test",
      projectId,
      accessToken: "account-token",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json({});
      },
    });

    await client.auth.listTrustedIps();
    await client.auth.getPasskeyAuthenticationOptions({ email: "ada@example.test" });

    assert.equal(requests[0]?.headers.get("authorization"), "Bearer account-token");
    assert.equal(requests[0]?.headers.get("x-terrascale-project-id"), projectId);
    assert.equal(requests[1]?.headers.has("authorization"), false);
    assert.equal(requests[1]?.headers.has("x-terrascale-project-id"), false);
  });

  test("maps account-security authorization errors through the typed result", async () => {
    for (const [status, kind] of [[401, "authentication"], [403, "authorization"]] as const) {
      const client = createTerraScaleClient({
        apiBaseUrl: "https://api.terrascale.test",
        authBaseUrl: "https://auth.terrascale.test",
        fetch: async () => Response.json({ code: "TERRASCALE-401", message: "Denied." }, { status }),
      });
      const result = await client.auth.getLoginHistory();

      assert.equal(result.ok, false);
      if (!result.ok) assert.equal(result.error.kind, kind);
    }
  });

  test("uses root repository routes without environment scope", async () => {
    const { client, requests } = createCapturingClient([]);

    await client.repositories.list();
    assertRequest(requests[0], "GET", "/api/v1/repositories");

    await client.repositories.create({
      name: "users",
      databaseId,
      environmentId,
      collection: "Users",
      schemaValidationEnabled: true,
      schema: '{"type":"object"}',
    });
    assertRequest(requests[1], "POST", "/api/v1/repositories");
    assert.deepEqual(requests[1]?.body, {
      name: "users",
      databaseId,
      environmentId,
      collection: "Users",
      schemaValidationEnabled: true,
      schema: '{"type":"object"}',
    });

    await client.repositories.get({ repositoryId });
    assertRequest(requests[2], "GET", `/api/v1/repositories/${repositoryId}`);

    await client.repositories.delete({ repositoryId }, { deleteDocuments: true });
    assertRequest(requests[3], "DELETE", `/api/v1/repositories/${repositoryId}`);
    assert.equal(requests[3]?.url.searchParams.get("deleteDocuments"), "true");
  });

  test("passes the caller-owned invitation response URL template", async () => {
    const { client, requests } = createCapturingClient({});
    const responseUrlTemplate =
      "https://app.example.test/invitations/{invitationId}?organizationId={organizationId}";

    await client.organizations.createInvitation(organizationId, {
      email: "grace@example.test",
      responseUrlTemplate,
    });

    assertRequest(
      requests[0],
      "POST",
      `/api/v1/organizations/${organizationId}/invitations`,
    );
    assert.deepEqual(requests[0]?.body, {
      email: "grace@example.test",
      responseUrlTemplate,
    });
  });

  test("locks document list, get, and delete routes directly", async () => {
    const { client, requests } = createCapturingClient({
      items: [],
      totalCount: 0,
      nextCursor: "next",
    });

    await client.documents.list({ repositoryId }, { skip: 10, take: 20 });
    assertRequest(requests[0], "GET", `/api/v1/repositories/${repositoryId}/documents`);
    assert.equal(requests[0]?.url.searchParams.get("skip"), "10");
    assert.equal(requests[0]?.url.searchParams.get("take"), "20");

    await client.documents.get({ repositoryId }, documentId);
    assertRequest(
      requests[1],
      "GET",
      `/api/v1/repositories/${repositoryId}/documents/${documentId}`,
    );

    await client.documents.delete({ repositoryId }, documentId);
    assertRequest(
      requests[2],
      "DELETE",
      `/api/v1/repositories/${repositoryId}/documents/${documentId}`,
    );
  });

  test("wraps document write bodies and preserves DocumentResponse shape", async () => {
    const documentResponse = {
      repositoryId,
      repository: "users",
      id: documentId,
      data: { name: "Ada" },
      lastModified: "2026-07-05T00:00:00Z",
      changeToken: "A:1",
    };
    const { client, requests } = createCapturingClient(documentResponse);

    const createResult = await client.documents.create({ repositoryId }, { name: "Ada" });
    assert.equal(createResult.ok, true);
    if (createResult.ok) {
      assert.deepEqual(createResult.value, documentResponse);
      assert.equal("documentId" in createResult.value, false);
      assert.equal("document" in createResult.value, false);
      assert.equal("etag" in createResult.value, false);
    }
    assertRequest(requests[0], "POST", `/api/v1/repositories/${repositoryId}/documents`);
    assert.deepEqual(requests[0]?.body, { data: { name: "Ada" } });

    await client.documents.put({ repositoryId }, documentId, { name: "Grace" });
    assertRequest(requests[1], "PUT", `/api/v1/repositories/${repositoryId}/documents/${documentId}`);
    assert.deepEqual(requests[1]?.body, { data: { name: "Grace" } });

    await client.documents.patch({ repositoryId }, documentId, { title: "Engineer" });
    assertRequest(
      requests[2],
      "PATCH",
      `/api/v1/repositories/${repositoryId}/documents/${documentId}`,
    );
    assert.deepEqual(requests[2]?.body, { data: { title: "Engineer" } });
    assert.equal(requests[2]?.headers.get("content-type"), "application/json");

    await client.documents.batch({
      repositoryId,
    }, {
      operations: [{ operation: "create", data: { name: "Katherine" } }],
    });
    assertRequest(requests[3], "POST", `/api/v1/repositories/${repositoryId}/documents/batch`);
    assert.deepEqual(requests[3]?.body, {
      operations: [{ operation: "create", data: { name: "Katherine" } }],
    });
  });

  test("uses repository TQL and schema routes", async () => {
    const { client, requests } = createCapturingClient({});

    await client.query.execute({ repositoryId }, { query: "from users", skip: 0, take: 10 });
    assertRequest(requests[0], "POST", `/api/v1/repositories/${repositoryId}/tql`);
    assert.deepEqual(requests[0]?.body, { query: "from users", skip: 0, take: 10 });

    await client.schema.get({ repositoryId });
    assertRequest(requests[1], "GET", `/api/v1/repositories/${repositoryId}/schema`);

    await client.schema.validate({ repositoryId }, { maxErrorMessages: 5 });
    assertRequest(requests[2], "POST", `/api/v1/repositories/${repositoryId}/schema/validate`);
    assert.deepEqual(requests[2]?.body, { maxErrorMessages: 5 });

    await client.schema.configure({ repositoryId }, { enabled: true, schema: '{"type":"object"}' });
    assertRequest(requests[3], "PUT", `/api/v1/repositories/${repositoryId}/schema`);
    assert.deepEqual(requests[3]?.body, { enabled: true, schema: '{"type":"object"}' });
  });

  test("uses project database routes, requires explicit deletion confirmation, and scopes its header", async () => {
    const deletedDatabase = {
      databaseId,
      projectId,
      name: "primary",
      deleted: true,
      environmentsDeleted: 1,
      physicalDatabasesDeleted: 1,
      repositoriesDeleted: 2,
    };
    const retryScheduledEnvironmentDeletion = {
      environmentId,
      projectId,
      databaseId,
      name: "Preview",
      deleted: false,
      physicalDatabaseDeleted: false,
      repositoriesDeleted: 0,
      operationId: toUlid("01VRZ3NDEKTSV4RRFFQ69G5FAV"),
      status: "RetryScheduled",
      retryAt: "2026-08-05T00:05:00Z",
    } satisfies DeletedProjectDatabaseEnvironmentResponse;
    const { client, requests } = createCapturingClientWithResponses([
      deletedDatabase,
      deletedDatabase,
      deletedDatabase,
      retryScheduledEnvironmentDeletion,
    ]);

    await client.databases.list();
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/databases`);
    assert.equal(requests[0]?.url.searchParams.get("skip"), "0");
    assert.equal(requests[0]?.url.searchParams.get("take"), "100");

    if (false) {
      // @ts-expect-error Database deletion requires explicit confirmation.
      void client.databases.delete(databaseId);
    }

    for (const options of [
      {},
      { confirm: false },
      { confirm: "true" },
      { confirm: new Boolean(true) },
    ]) {
      assert.throws(
        () => client.databases.delete(databaseId, options as never),
        TypeError,
      );
    }
    assert.equal(requests.filter(request => request.method === "DELETE").length, 0);

    const deleteResult = await client.databases.delete(databaseId, { confirm: true });
    assert.equal(deleteResult.ok, true);
    if (deleteResult.ok) {
      assert.deepEqual(deleteResult.value, deletedDatabase);
    }
    assertRequest(requests[1], "DELETE", `/api/v1/projects/${projectId}/databases/${databaseId}`);
    assert.equal(requests[1]?.headers.get("x-terrascale-delete-confirmation"), databaseId);

    await client.databases.createEnvironment(databaseId, { name: "Preview", kind: "Preview" });
    assertRequest(
      requests[2],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/environments`,
    );
    assert.deepEqual(requests[2]?.body, { name: "Preview", kind: "Preview" });

    const environmentDeleteResult = await client.databases.deleteEnvironment(databaseId, environmentId);
    assert.equal(environmentDeleteResult.ok, true);
    if (environmentDeleteResult.ok) {
      assert.deepEqual(environmentDeleteResult.value, retryScheduledEnvironmentDeletion);
      assert.equal(environmentDeleteResult.value.deleted, false);
      assert.equal(environmentDeleteResult.value.status, "RetryScheduled");
      assert.equal(environmentDeleteResult.value.operationId, retryScheduledEnvironmentDeletion.operationId);
      assert.equal(environmentDeleteResult.value.retryAt, "2026-08-05T00:05:00Z");
    }
    assertRequest(
      requests[3],
      "DELETE",
      `/api/v1/projects/${projectId}/databases/${databaseId}/environments/${environmentId}`,
    );
    assert.equal(requests[3]?.headers.has("x-terrascale-delete-confirmation"), false);
  });

  test("exposes fail-closed physical database branch capability and clone request routes", async () => {
    const capability = {
      projectId,
      admissionState: "unavailable",
      physicalCloneAvailable: false,
      promotionAvailable: false,
      mergeAvailable: false,
      detail: "Physical database branching is not available.",
    } satisfies DatabaseBranchCapabilityResponse;
    const cloneRequest = { sourceDatabaseId: databaseId } satisfies CreateDatabaseBranchRequest;
    const { client, requests } = createCapturingClientWithResponses(
      [capability],
      { idempotencyKeyFactory: () => "not-admitted-clone-key" },
    );

    const capabilityResult = await client.databaseBranches.getCapability();
    assert.equal(capabilityResult.ok, true);
    if (capabilityResult.ok) assert.deepEqual(capabilityResult.value, capability);
    assertRequest(
      requests[0],
      "GET",
      `/api/v1/projects/${projectId}/database-branches/capability`,
    );

    await client.databaseBranches.requestClone(cloneRequest);
    assertRequest(requests[1], "POST", `/api/v1/projects/${projectId}/database-branches`);
    assert.deepEqual(requests[1]?.body, cloneRequest);
    assert.equal(requests[1]?.headers.has("idempotency-key"), false);
  });

  test("keeps unavailable physical database clone typed as the Platform 503 failure", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async () =>
        Response.json(
          { code: "database_branch_unavailable", message: "Physical database branching is unavailable." },
          { status: 503 },
        ),
    });

    const result = await client.databaseBranches.requestClone({ sourceDatabaseId: databaseId });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 503);
      assert.equal(result.error.kind, "server");
      assert.equal(result.error.code, "database_branch_unavailable");
    }
  });

  test("uses typed project saved-query CRUD routes, DTOs, pagination, and idempotency", async () => {
    const savedQuery = {
      savedQueryId,
      projectId,
      name: "Active users",
      query: "SELECT * FROM users WHERE active = true",
      repositoryId: null,
      createdAt: "2026-08-05T00:00:00Z",
      updatedAt: "2026-08-05T00:00:00Z",
    } satisfies ProjectSavedQueryResponse;
    const { client, requests } = createCapturingClientWithResponses([
      [savedQuery],
      [savedQuery],
      [savedQuery],
      [savedQuery],
      [savedQuery],
      savedQuery,
      savedQuery,
      { savedQueryId, projectId, deleted: true },
    ], { idempotencyKeyFactory: () => "saved-query-replay-key" });

    const defaultListResult = await client.savedQueries.list();
    assert.equal(defaultListResult.ok, true);
    if (defaultListResult.ok) {
      assert.deepEqual(defaultListResult.value, [savedQuery]);
      assert.equal(defaultListResult.value[0]?.repositoryId, null);
      assert.equal(Object.hasOwn(defaultListResult.value[0]!, "repositoryId"), true);
    }
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/saved-queries`);
    assert.equal(requests[0]?.url.search, "?skip=0&take=100");

    const emptyQueryResult = await client.savedQueries.list(projectId, {});
    assert.equal(emptyQueryResult.ok, true);
    assertRequest(requests[1], "GET", `/api/v1/projects/${projectId}/saved-queries`);
    assert.equal(requests[1]?.url.search, "?skip=0&take=100");

    const skipOnlyQuery = { skip: 10 };
    const skipOnlyResult = await client.savedQueries.list(projectId, skipOnlyQuery);
    assert.equal(skipOnlyResult.ok, true);
    assert.deepEqual(skipOnlyQuery, { skip: 10 });
    assertRequest(requests[2], "GET", `/api/v1/projects/${projectId}/saved-queries`);
    assert.equal(requests[2]?.url.search, "?skip=10&take=100");

    const takeOnlyQuery = { take: 20 };
    const takeOnlyResult = await client.savedQueries.list(projectId, takeOnlyQuery);
    assert.equal(takeOnlyResult.ok, true);
    assert.deepEqual(takeOnlyQuery, { take: 20 });
    assertRequest(requests[3], "GET", `/api/v1/projects/${projectId}/saved-queries`);
    assert.equal(requests[3]?.url.search, "?skip=0&take=20");

    const listResult = await client.savedQueries.list(otherProjectId, { skip: 10, take: 20 });
    assert.equal(listResult.ok, true);
    if (listResult.ok) {
      assert.deepEqual(listResult.value, [savedQuery]);
    }
    assertRequest(requests[4], "GET", `/api/v1/projects/${otherProjectId}/saved-queries`);
    assert.equal(requests[4]?.url.search, "?skip=10&take=20");

    const createResult = await client.savedQueries.create({
      name: savedQuery.name,
      query: savedQuery.query,
      repositoryId,
    });
    assert.equal(createResult.ok, true);
    if (createResult.ok) {
      assert.deepEqual(createResult.value, savedQuery);
    }
    assertRequest(requests[5], "POST", `/api/v1/projects/${projectId}/saved-queries`);
    assert.deepEqual(requests[5]?.body, {
      name: savedQuery.name,
      query: savedQuery.query,
      repositoryId,
    });
    assert.equal(requests[5]?.headers.get("idempotency-key"), "saved-query-replay-key");

    await client.savedQueries.update(savedQueryId, {
      name: "Active users v2",
      query: savedQuery.query,
    });
    assertRequest(
      requests[6],
      "PUT",
      `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`,
    );
    assert.deepEqual(requests[6]?.body, {
      name: "Active users v2",
      query: savedQuery.query,
    });
    assert.equal(requests[6]?.headers.get("idempotency-key"), "saved-query-replay-key");

    const deleteResult = await client.savedQueries.delete(savedQueryId);
    assert.equal(deleteResult.ok, true);
    if (deleteResult.ok) {
      assert.deepEqual(deleteResult.value, { savedQueryId, projectId, deleted: true });
    }
    assertRequest(
      requests[7],
      "DELETE",
      `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`,
    );
    assert.equal(requests[7]?.headers.get("idempotency-key"), "saved-query-replay-key");
  });

  test("uses opaque revision-aware v2 saved-query reads and conditional updates", async () => {
    const savedQuery = {
      savedQueryId,
      projectId,
      name: "Active users",
      query: "SELECT * FROM users WHERE active = true",
      repositoryId: null,
      createdAt: "2026-08-05T00:00:00Z",
      updatedAt: "2026-08-05T00:00:00Z",
      revision: "opaque-resource-revision",
    } satisfies ProjectSavedQueryV2Response;
    const { client, requests } = createCapturingClientWithResponses([
      [savedQuery],
      [savedQuery],
      savedQuery,
      savedQuery,
    ], { idempotencyKeyFactory: () => "saved-query-v2-replay-key" });

    await client.savedQueries.v2.list();
    assertRequest(requests[0], "GET", `/api/v2/projects/${projectId}/saved-queries`);
    assert.equal(requests[0]?.url.search, "?skip=0&take=100");

    const listed = await client.savedQueries.v2.list(undefined, { skip: 10, take: 20 });
    assert.equal(listed.ok, true);
    if (listed.ok) assert.deepEqual(listed.value, [savedQuery]);
    assertRequest(requests[1], "GET", `/api/v2/projects/${projectId}/saved-queries`);
    assert.equal(requests[1]?.url.search, "?skip=10&take=20");

    const fetched = await client.savedQueries.v2.get(savedQueryId);
    assert.equal(fetched.ok, true);
    if (fetched.ok) assert.equal(fetched.value.revision, savedQuery.revision);
    assertRequest(requests[2], "GET", `/api/v2/projects/${projectId}/saved-queries/${savedQueryId}`);

    await client.savedQueries.v2.update(savedQueryId, {
      name: "Active users v2",
      query: savedQuery.query,
      revision: savedQuery.revision,
    });
    assertRequest(requests[3], "PUT", `/api/v2/projects/${projectId}/saved-queries/${savedQueryId}`);
    assert.deepEqual(requests[3]?.body, {
      name: "Active users v2",
      query: savedQuery.query,
      revision: savedQuery.revision,
    });
    assert.equal(requests[3]?.headers.get("idempotency-key"), "saved-query-v2-replay-key");
  });

  test("uses a typed v3 mutation-safe snapshot cursor without exposing change vectors", async () => {
    const savedQuery = {
      savedQueryId,
      projectId,
      name: "Active users",
      query: "SELECT * FROM users WHERE active = true",
      repositoryId: null,
      createdAt: "2026-08-05T00:00:00Z",
      updatedAt: "2026-08-05T00:00:00Z",
      revision: "opaque-resource-revision",
    } satisfies ProjectSavedQueryV2Response;
    const page = {
      items: [savedQuery],
      nextCursor: null,
    } satisfies ProjectSavedQuerySnapshotPageResponse;
    const { client, requests } = createCapturingClientWithResponses([page]);

    const result = await client.savedQueries.v3.list(otherProjectId, {
      cursor: "opaque-project-bound-cursor",
      take: 500,
    });

    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value, page);
    assertRequest(requests[0], "GET", `/api/v3/projects/${otherProjectId}/saved-queries`);
    assert.equal(requests[0]?.url.search, "?cursor=opaque-project-bound-cursor&take=500");
  });

  test("maps expired v3 snapshot cursors to the public conflict result", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async () =>
        Response.json(
          { code: "TERRASCALE-1401", message: "The snapshot cursor expired." },
          { status: 409 },
        ),
    });

    const result = await client.savedQueries.v3.list(undefined, { cursor: "expired-cursor" });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 409);
      assert.equal(result.error.kind, "conflict");
    }
  });

  test("preserves typed public errors for invalid or tampered v3 cursors", async () => {
    const expected = new Map([
      [400, "domain"],
      [401, "authentication"],
      [403, "authorization"],
      [404, "not-found"],
      [409, "conflict"],
      [503, "server"],
    ]);

    for (const [status, kind] of expected) {
      const client = createTerraScaleClient({
        apiBaseUrl: "https://api.terrascale.test",
        projectId,
        fetch: async () =>
          Response.json(
            { code: "TERRASCALE-1401", message: "The opaque snapshot cursor was rejected." },
            { status },
          ),
      });

      const result = await client.savedQueries.v3.list(undefined, { cursor: "tampered-cursor" });
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.status, status);
        assert.equal(result.error.kind, kind);
      }
    }
  });

  test("maps v2 revision conflicts to the public conflict result", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async () =>
        Response.json(
          { code: "TERRASCALE-1401", message: "The saved query was changed." },
          { status: 409 },
        ),
    });

    const result = await client.savedQueries.v2.update(savedQueryId, {
      name: "Active users v2",
      query: "SELECT * FROM users WHERE active = true",
      revision: "opaque-resource-revision",
    });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 409);
      assert.equal(result.error.kind, "conflict");
      assert.equal(result.error.code, "TERRASCALE-1401");
    }
  });

  test("uses backed database backup and restore routes with public DTO shapes", async () => {
    const backupPolicy = {
      backupPolicyId,
      projectId,
      databaseId,
      databaseName: "primary",
      fullBackupCron: "0 2 * * *",
      incrementalBackupCron: "0 */6 * * *",
      retentionDays: 30,
      disabled: false,
      destinationType: "oci-object-storage",
      status: "Configured",
      createdAt: "2026-07-05T00:00:00Z",
      updatedAt: "2026-07-05T00:00:00Z",
    };
    const backupJob = {
      backupId,
      projectId,
      databaseId,
      databaseName: "primary",
      policy: "manual",
      retention: "30d",
      status: "Completed",
      destinationType: "oci-object-storage",
      hasRestorePoint: true,
      requestedBy: "owner@terrascale.tech",
      requestedAt: "2026-07-05T00:00:00Z",
      updatedAt: "2026-07-05T00:01:00Z",
      completedAt: "2026-07-05T00:02:00Z",
      errorMessage: null,
    };
    const backupConfirmationPlan = {
      action: "backup",
      projectId,
      databaseId,
      databaseName: "primary",
      requestDigest: "backup-request-digest",
      confirmationToken: "backup-confirmation-token",
      expiresAt: "2026-08-05T00:05:00Z",
    };
    const restorePoint = {
      name: "restore-point-20260705",
      backupId,
      createdAt: "2026-07-05T00:02:00Z",
      isSnapshotRestore: true,
      isEncrypted: true,
      isIncremental: false,
      filesToRestore: 1,
    };
    const restoreJob = {
      restoreId,
      projectId,
      databaseId,
      backupId,
      restoredDatabaseId,
      restoredDatabaseName: "primary-restored",
      status: "Running",
      requestedBy: "owner@terrascale.tech",
      requestedAt: "2026-07-05T00:03:00Z",
      updatedAt: "2026-07-05T00:04:00Z",
      completedAt: null,
      errorMessage: null,
    };
    const { client, requests } = createCapturingClientWithResponses([
      backupPolicy,
      [backupJob],
      backupJob,
      [restorePoint],
      restoreJob,
      restoreJob,
      [restoreJob],
      backupConfirmationPlan,
      backupJob,
    ]);

    const policyResult = await client.backups.configurePolicy(databaseId, {
      fullBackupCron: "0 2 * * *",
      incrementalBackupCron: "0 */6 * * *",
      retentionDays: 30,
      disabled: false,
    });
    assert.equal(policyResult.ok, true);
    if (policyResult.ok) {
      assert.deepEqual(policyResult.value, backupPolicy);
      assert.equal("terraBaseTaskId" in policyResult.value, false);
      assert.equal("physicalDatabaseName" in policyResult.value, false);
    }
    assertRequest(
      requests[0],
      "PUT",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups/policy`,
    );
    assert.deepEqual(requests[0]?.body, {
      fullBackupCron: "0 2 * * *",
      incrementalBackupCron: "0 */6 * * *",
      retentionDays: 30,
      disabled: false,
    });

    const listBackupsResult = await client.backups.listBackups(databaseId, { skip: 5, take: 10 });
    assert.equal(listBackupsResult.ok, true);
    if (listBackupsResult.ok) {
      assert.deepEqual(listBackupsResult.value, [backupJob]);
      assert.equal("items" in listBackupsResult.value, false);
    }
    assertRequest(
      requests[1],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups`,
    );
    assert.equal(requests[1]?.url.searchParams.get("skip"), "5");
    assert.equal(requests[1]?.url.searchParams.get("take"), "10");

    await client.backups.getBackup(databaseId, backupId);
    assertRequest(
      requests[2],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups/${backupId}`,
    );

    const restorePointsResult = await client.backups.listRestorePoints(databaseId);
    assert.equal(restorePointsResult.ok, true);
    if (restorePointsResult.ok) {
      assert.deepEqual(restorePointsResult.value, [restorePoint]);
      const [capturedRestorePoint] = restorePointsResult.value;
      assert.ok(capturedRestorePoint);
      assert.equal("terraBaseDatabaseName" in capturedRestorePoint, false);
    }
    assertRequest(
      requests[3],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/restore-points`,
    );

    const startRestoreResult = await client.backups.startRestore(databaseId, {
      backupId,
      idempotencyKey: "restore-1",
    });
    assert.equal(startRestoreResult.ok, true);
    if (startRestoreResult.ok) {
      assert.equal(startRestoreResult.value.restoredDatabaseId, restoredDatabaseId);
      assert.equal("terraBaseTaskId" in startRestoreResult.value, false);
      assert.equal("physicalRestoredDatabaseName" in startRestoreResult.value, false);
    }
    assertRequest(
      requests[4],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/restores`,
    );
    assert.deepEqual(requests[4]?.body, { backupId, idempotencyKey: "restore-1" });

    await client.backups.getRestore(databaseId, restoreId);
    assertRequest(
      requests[5],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/restores/${restoreId}`,
    );

    const listRestoresResult = await client.backups.listRestores(databaseId, {
      skip: 2,
      take: 3,
    });
    assert.equal(listRestoresResult.ok, true);
    if (listRestoresResult.ok) {
      assert.deepEqual(listRestoresResult.value, [restoreJob]);
      assert.equal("items" in listRestoresResult.value, false);
    }
    assertRequest(
      requests[6],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/restores`,
    );
    assert.equal(requests[6]?.url.searchParams.get("skip"), "2");
    assert.equal(requests[6]?.url.searchParams.get("take"), "3");

    assert.throws(
      () => client.backups.planBackup(databaseId, { operationSessionId: " " }),
      TypeError,
    );
    assert.throws(
      () =>
        client.backups.executeBackup(
          databaseId,
          { operationSessionId: "backup-operation-session", confirmationToken: " " },
          "backup-execution-key",
        ),
      TypeError,
    );
    assert.equal(requests.length, 7);

    const planResult = await client.backups.planBackup(databaseId, {
      operationSessionId: "backup-operation-session",
    });
    assert.equal(planResult.ok, true);
    if (planResult.ok) {
      assert.deepEqual(planResult.value, backupConfirmationPlan);
      assert.equal("physicalDatabaseName" in planResult.value, false);
    }
    assertRequest(
      requests[7],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups:plan`,
    );
    assert.deepEqual(requests[7]?.body, { operationSessionId: "backup-operation-session" });
    assert.equal(requests[7]?.headers.has("idempotency-key"), false);

    const executeResult = await client.backups.executeBackup(
      databaseId,
      {
        operationSessionId: "backup-operation-session",
        confirmationToken: "backup-confirmation-token",
      },
      "backup-execution-key",
    );
    assert.equal(executeResult.ok, true);
    if (executeResult.ok) {
      assert.deepEqual(executeResult.value, backupJob);
    }
    assertRequest(
      requests[8],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups:execute`,
    );
    assert.deepEqual(requests[8]?.body, {
      operationSessionId: "backup-operation-session",
      confirmationToken: "backup-confirmation-token",
    });
    assert.equal(requests[8]?.headers.get("idempotency-key"), "backup-execution-key");
    assert.equal(requests[8]?.headers.has("x-terrascale-delete-confirmation"), false);
  });

  test("uses project auth API key wrappers and rotate/delete routes", async () => {
    const created = {
      apiKey: {
        keyId,
        projectId,
        name: "ci",
        prefix: "ts",
        lastFour: "1234",
        scopes: ["repositories:read"],
        createdByEmail: "owner@terrascale.tech",
        createdAt: "2026-07-05T00:00:00Z",
        isActive: true,
      },
      secret: "ts_secret",
    };
    const { client, requests } = createCapturingClient(created);

    await client.apiKeys.list(undefined, { cursor: "next", limit: 25 });
    assert.equal(requests[0]?.url.origin, "https://auth.terrascale.test");
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/auth/api-keys`);
    assert.equal(requests[0]?.url.searchParams.get("cursor"), "next");
    assert.equal(requests[0]?.url.searchParams.get("limit"), "25");

    const createResult = await client.apiKeys.create({
      name: "ci",
      scopes: ["repositories:read"],
      databaseId,
      environmentId,
    });
    assert.equal(createResult.ok, true);
    if (createResult.ok) {
      assert.equal(createResult.value.apiKey.keyId, keyId);
      assert.equal(createResult.value.secret, "ts_secret");
      assert.equal("apiKeyId" in createResult.value.apiKey, false);
    }
    assertRequest(requests[1], "POST", `/api/v1/projects/${projectId}/auth/api-keys`);
    assert.deepEqual(requests[1]?.body, {
      name: "ci",
      scopes: ["repositories:read"],
      databaseId,
      environmentId,
    });

    await client.apiKeys.rotate(keyId);
    assertRequest(requests[2], "POST", `/api/v1/projects/${projectId}/auth/api-keys/${keyId}/rotate`);

    await client.apiKeys.revoke(keyId);
    assertRequest(requests[3], "DELETE", `/api/v1/projects/${projectId}/auth/api-keys/${keyId}`);
  });

  test("uses project auth M2M wrappers, token body, rotate, and delete routes", async () => {
    const created = {
      application: {
        clientId,
        projectId,
        name: "worker",
        prefix: "m2m",
        lastFour: "9876",
        scopes: ["documents:write"],
        createdByEmail: "owner@terrascale.tech",
        createdAt: "2026-07-05T00:00:00Z",
        isActive: true,
      },
      clientSecret: "client_secret",
    };
    const { client, requests } = createCapturingClient(created);

    await client.m2m.list(undefined, { cursor: "next", limit: 25 });
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/auth/m2m-applications`);
    assert.equal(requests[0]?.url.searchParams.get("cursor"), "next");
    assert.equal(requests[0]?.url.searchParams.get("limit"), "25");

    const createResult = await client.m2m.create({
      name: "worker",
      scopes: ["documents:write"],
      databaseId,
      environmentId,
    });
    assert.equal(createResult.ok, true);
    if (createResult.ok) {
      assert.equal(createResult.value.application.clientId, clientId);
      assert.equal(createResult.value.clientSecret, "client_secret");
    }
    assertRequest(requests[1], "POST", `/api/v1/projects/${projectId}/auth/m2m-applications`);
    for (const request of requests) {
      assert.equal(request.url.origin, "https://auth.terrascale.test");
    }
    assert.deepEqual(requests[1]?.body, {
      name: "worker",
      scopes: ["documents:write"],
      databaseId,
      environmentId,
    });

    await client.m2m.rotate(clientId);
    assertRequest(
      requests[2],
      "POST",
      `/api/v1/projects/${projectId}/auth/m2m-applications/${clientId}/rotate`,
    );

    await client.m2m.revoke(clientId);
    assertRequest(
      requests[3],
      "DELETE",
      `/api/v1/projects/${projectId}/auth/m2m-applications/${clientId}`,
    );

    await client.m2m.createToken(projectId, {
      clientId,
      clientSecret: "client_secret",
      scope: "documents:write",
    });
    assertRequest(requests[4], "POST", `/api/v1/projects/${projectId}/auth/m2m/token`);
    assert.equal(requests[4]?.url.origin, "https://auth.terrascale.test");
    assert.deepEqual(requests[4]?.body, {
      grant_type: "client_credentials",
      client_id: clientId,
      client_secret: "client_secret",
      scope: "documents:write",
    });
  });

  test("captures notification route shapes", async () => {
    const notification = {
      notificationId,
      title: "Backup complete",
      body: "Repository backup finished.",
      severity: "info",
      createdAt: "2026-07-05T00:00:00Z",
    };
    const { client, requests } = createCapturingClientWithResponses([
      [notification],
      notification,
      { markedRead: 3 },
    ]);

    const listResult = await client.notifications.list({
      skip: 5,
      take: 10,
      unreadOnly: true,
      organizationId,
    });
    assert.equal(listResult.ok, true);
    if (listResult.ok) {
      assert.deepEqual(listResult.value, [notification]);
      assert.equal("items" in listResult.value, false);
    }
    assertRequest(requests[0], "GET", "/api/v1/users/me/notifications");
    assert.equal(requests[0]?.url.searchParams.get("skip"), "5");
    assert.equal(requests[0]?.url.searchParams.get("take"), "10");
    assert.equal(requests[0]?.url.searchParams.get("unreadOnly"), "true");
    assert.equal(requests[0]?.url.searchParams.get("organizationId"), organizationId);

    const markReadResult = await client.notifications.markRead(notificationId);
    assert.equal(markReadResult.ok, true);
    if (markReadResult.ok) {
      assert.deepEqual(markReadResult.value, notification);
    }
    assertRequest(
      requests[1],
      "POST",
      `/api/v1/users/me/notifications/${notificationId}:mark-read`,
    );

    const markAllReadResult = await client.notifications.markAllRead({ organizationId });
    assert.equal(markAllReadResult.ok, true);
    if (markAllReadResult.ok) {
      assert.deepEqual(markAllReadResult.value, { markedRead: 3 });
    }
    assertRequest(requests[2], "POST", "/api/v1/users/me/notifications:mark-all-read");
    assert.equal(requests[2]?.url.searchParams.get("organizationId"), organizationId);
  });

  test("uses the backed security settings routes", async () => {
    const { client, requests } = createCapturingClient({});

    await client.security.getSettings();
    await client.security.updateSettings({
      mfaPolicy: "Required",
      ipAllowlist: "10.0.0.0/8",
      allowedEmailDomains: "terrascale.tech",
      dataResidency: "gru",
    });

    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/security/settings`);
    assertRequest(requests[1], "PUT", `/api/v1/projects/${projectId}/security/settings`);
    assert.deepEqual(requests[1]?.body, {
      mfaPolicy: "Required",
      ipAllowlist: "10.0.0.0/8",
      allowedEmailDomains: "terrascale.tech",
      dataResidency: "gru",
    });
  });
});

function createCapturingClient(responseBody: JsonValue): {
  readonly client: TerraScaleClient;
  readonly requests: readonly CapturedRequest[];
} {
  return createCapturingClientWithResponses([responseBody]);
}

function createCapturingClientWithResponses(
  responseBodies: readonly JsonValue[],
  options: { readonly idempotencyKeyFactory?: () => string } = {},
): {
  readonly client: TerraScaleClient;
  readonly requests: readonly CapturedRequest[];
} {
  const requests: CapturedRequest[] = [];
  let responseIndex = 0;

  const client = createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    projectId,
    ...options,
    fetch: async (input) => {
      const request = input instanceof Request ? input : new Request(input);
      const rawBody = await request.clone().text();
      requests.push({
        method: request.method,
        url: new URL(request.url),
        headers: new Headers(request.headers),
        body: rawBody.length === 0 ? undefined : (JSON.parse(rawBody) as unknown),
      });
      const responseBody =
        responseBodies[Math.min(responseIndex, responseBodies.length - 1)] ?? {};
      responseIndex += 1;

      return new Response(JSON.stringify(responseBody), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    },
  });

  return { client, requests };
}

function assertRequest(
  request: CapturedRequest | undefined,
  method: string,
  pathname: string,
): asserts request is CapturedRequest {
  assert.ok(request, "expected a captured request");
  assert.equal(request.method, method);
  assert.equal(request.url.pathname, pathname);
}

function routeManifestCore(route: ManifestRoute): ManifestRoute {
  return {
    base: route.base,
    client: route.client,
    method: route.method,
    operation: route.operation,
    pathTemplate: route.pathTemplate,
    routeName: route.routeName,
  };
}
