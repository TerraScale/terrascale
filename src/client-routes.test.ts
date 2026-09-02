import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "vitest";

import { capturePublicClientRoutes } from "./client-route-capture.js";
import { createTerraScaleClient } from "./factory.js";
import type { TerraScaleClient } from "./factory.js";
import { toUlid } from "./ids.js";
import type {
  DatabaseBranchCapabilityResponse,
  CreateDatabaseBranchRequest,
  JsonValue,
  ProjectSavedQueryPageResponse,
  ProjectSavedQueryResponse,
} from "./models.js";

const projectId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const databaseId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAW");
const notificationId = toUlid("01DRZ3NDEKTSV4RRFFQ69G5FAV");
const organizationId = toUlid("01ERZ3NDEKTSV4RRFFQ69G5FAV");
const backupPolicyId = toUlid("01NRZ3NDEKTSV4RRFFQ69G5FAW");
const backupId = toUlid("01NRZ3NDEKTSV4RRFFQ69G5FAV");
const restoreId = toUlid("01MRZ3NDEKTSV4RRFFQ69G5FAV");
const restoredDatabaseId = toUlid("01QRZ3NDEKTSV4RRFFQ69G5FAV");
const savedQueryId = toUlid("01SRZ3NDEKTSV4RRFFQ69G5FAV");
const otherProjectId = toUlid("01TRZ3NDEKTSV4RRFFQ69G5FAV");

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
    assert.equal(manifest.routeCount, 60);
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

  test("passes the caller-owned invitation response URL template", async () => {
    const { client, requests } = createCapturingClient({});
    const responseUrlTemplate =
      "https://app.example.test/invitations/{invitationId}?organizationId={organizationId}";

    await client.organizations.createInvitation(organizationId, {
      email: "grace@example.test",
      projectId,
      responseUrlTemplate,
    });

    assertRequest(
      requests[0],
      "POST",
      `/api/v1/organizations/${organizationId}/invitations`,
    );
    assert.deepEqual(requests[0]?.body, {
      email: "grace@example.test",
      projectId,
      responseUrlTemplate,
    });
  });

  test("uses project database routes, requires explicit deletion confirmation, and scopes its header", async () => {
    const deletedDatabase = {
      databaseId,
      projectId,
      name: "primary",
      deleted: true,
      physicalDatabasesDeleted: 1,
    };
    const { client, requests } = createCapturingClientWithResponses([
      deletedDatabase,
      deletedDatabase,
      deletedDatabase,
    ]);

    await client.databases.list();
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/databases`);
    assert.equal(requests[0]?.url.searchParams.get("skip"), "0");
    assert.equal(requests[0]?.url.searchParams.get("take"), "100");

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
      createdAt: "2026-08-05T00:00:00Z",
      updatedAt: "2026-08-05T00:00:00Z",
      revision: "opaque-resource-revision",
    } satisfies ProjectSavedQueryResponse;
    const savedQueryPage = {
      items: [savedQuery],
      totalCount: 1,
      nextCursor: null,
    } satisfies ProjectSavedQueryPageResponse;
    const { client, requests } = createCapturingClientWithResponses([
      savedQueryPage,
      savedQueryPage,
      savedQueryPage,
      savedQueryPage,
      savedQueryPage,
      savedQuery,
      savedQuery,
      { savedQueryId, projectId, deleted: true },
    ], { idempotencyKeyFactory: () => "saved-query-replay-key" });

    const defaultListResult = await client.savedQueries.list();
    assert.equal(defaultListResult.ok, true);
    if (defaultListResult.ok) {
      assert.deepEqual(defaultListResult.value.items, [savedQuery]);
      assert.equal(defaultListResult.value.totalCount, 1);
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
      assert.deepEqual(listResult.value.items, [savedQuery]);
    }
    assertRequest(requests[4], "GET", `/api/v1/projects/${otherProjectId}/saved-queries`);
    assert.equal(requests[4]?.url.search, "?skip=10&take=20");

    const createResult = await client.savedQueries.create({
      name: savedQuery.name,
      query: savedQuery.query,
    });
    assert.equal(createResult.ok, true);
    if (createResult.ok) {
      assert.deepEqual(createResult.value, savedQuery);
    }
    assertRequest(requests[5], "POST", `/api/v1/projects/${projectId}/saved-queries`);
    assert.deepEqual(requests[5]?.body, {
      name: savedQuery.name,
      query: savedQuery.query,
    });
    assert.equal(requests[5]?.headers.get("idempotency-key"), "saved-query-replay-key");

    await client.savedQueries.update(savedQueryId, {
      name: "Active users v2",
      query: savedQuery.query,
      revision: savedQuery.revision,
    });
    assertRequest(
      requests[6],
      "PUT",
      `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`,
    );
    assert.deepEqual(requests[6]?.body, {
      name: "Active users v2",
      query: savedQuery.query,
      revision: savedQuery.revision,
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

  test("uses revision-aware v1 saved-query reads and conditional updates", async () => {
    const savedQuery = {
      savedQueryId,
      projectId,
      name: "Active users",
      query: "SELECT * FROM users WHERE active = true",
      createdAt: "2026-08-05T00:00:00Z",
      updatedAt: "2026-08-05T00:00:00Z",
      revision: "opaque-resource-revision",
    } satisfies ProjectSavedQueryResponse;
    const savedQueryPage = {
      items: [savedQuery],
      totalCount: 1,
      nextCursor: null,
    } satisfies ProjectSavedQueryPageResponse;
    const { client, requests } = createCapturingClientWithResponses([
      savedQueryPage,
      savedQueryPage,
      savedQuery,
      savedQuery,
    ], { idempotencyKeyFactory: () => "saved-query-v1-replay-key" });

    await client.savedQueries.list();
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/saved-queries`);
    assert.equal(requests[0]?.url.search, "?skip=0&take=100");

    const listed = await client.savedQueries.list(undefined, { skip: 10, take: 20 });
    assert.equal(listed.ok, true);
    if (listed.ok) assert.deepEqual(listed.value.items, [savedQuery]);
    assertRequest(requests[1], "GET", `/api/v1/projects/${projectId}/saved-queries`);
    assert.equal(requests[1]?.url.search, "?skip=10&take=20");

    const fetched = await client.savedQueries.get(savedQueryId);
    assert.equal(fetched.ok, true);
    if (fetched.ok) assert.equal(fetched.value.revision, savedQuery.revision);
    assertRequest(requests[2], "GET", `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`);

    await client.savedQueries.update(savedQueryId, {
      name: "Active users v2",
      query: savedQuery.query,
      revision: savedQuery.revision,
    });
    assertRequest(requests[3], "PUT", `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`);
    assert.deepEqual(requests[3]?.body, {
      name: "Active users v2",
      query: savedQuery.query,
      revision: savedQuery.revision,
    });
    assert.equal(requests[3]?.headers.get("idempotency-key"), "saved-query-v1-replay-key");
  });

  test("uses the typed v1 continuation cursor returned by the saved-query page", async () => {
    const savedQuery = {
      savedQueryId,
      projectId,
      name: "Active users",
      query: "SELECT * FROM users WHERE active = true",
      createdAt: "2026-08-05T00:00:00Z",
      updatedAt: "2026-08-05T00:00:00Z",
      revision: "opaque-resource-revision",
    } satisfies ProjectSavedQueryResponse;
    const page = {
      items: [savedQuery],
      totalCount: 11,
      nextCursor: null,
    } satisfies ProjectSavedQueryPageResponse;
    const { client, requests } = createCapturingClientWithResponses([page]);

    const result = await client.savedQueries.list(otherProjectId, {
      cursor: "10",
      take: 500,
    });

    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value, page);
    assertRequest(requests[0], "GET", `/api/v1/projects/${otherProjectId}/saved-queries`);
    assert.equal(requests[0]?.url.search, "?cursor=10&skip=0&take=500");
  });

  test("maps saved-query list conflicts to the public conflict result", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async () =>
        Response.json(
          { code: "saved_query_conflict", message: "The saved-query page changed." },
          { status: 409 },
        ),
    });

    const result = await client.savedQueries.list(undefined, { cursor: "10" });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 409);
      assert.equal(result.error.kind, "conflict");
    }
  });

  test("preserves typed public errors for saved-query list failures", async () => {
    const expected = new Map([
      [400, "domain"],
      [401, "authentication"],
      [403, "authorization"],
      [404, "not-found"],
      [409, "conflict"],
      [429, "rate-limit"],
      [500, "server"],
    ]);

    for (const [status, kind] of expected) {
      const client = createTerraScaleClient({
        apiBaseUrl: "https://api.terrascale.test",
        projectId,
        fetch: async () =>
          Response.json(
            { code: "saved_query_list_failed", message: "The saved-query list failed." },
            { status },
          ),
      });

      const result = await client.savedQueries.list(undefined, { cursor: "10" });
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.status, status);
        assert.equal(result.error.kind, kind);
      }
    }
  });

  test("maps saved-query revision conflicts to the public conflict result", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async () =>
        Response.json(
          { code: "TERRASCALE-1401", message: "The saved query was changed." },
          { status: 409 },
        ),
    });

    const result = await client.savedQueries.update(savedQueryId, {
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

  test("captures notification route shapes", async () => {
    const notification = {
      notificationId,
      title: "Backup complete",
      body: "Database backup finished.",
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
