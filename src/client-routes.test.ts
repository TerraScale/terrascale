import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import { capturePublicClientRoutes } from "./client-route-capture.js";
import { createTerraScaleClient } from "./factory.js";
import type { TerraScaleClient } from "./factory.js";
import { toUlid, type Ulid } from "./ids.js";
import type { JsonValue } from "./models.js";

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
      readonly routes: readonly ManifestRoute[];
    };
    const capturedRoutes = await capturePublicClientRoutes();

    assert.deepEqual(
      manifest.routes.map(routeManifestCore),
      capturedRoutes.map(routeManifestCore),
    );
  });

  test("public factory hides admin and dashboard clients at runtime", () => {
    const { client } = createCapturingClient({});
    const runtimeClient = client as TerraScaleClient & {
      readonly admin?: unknown;
      readonly dashboard?: unknown;
    };

    assert.equal("admin" in runtimeClient, false);
    assert.equal("dashboard" in runtimeClient, false);
    assert.equal(runtimeClient.admin, undefined);
    assert.equal(runtimeClient.dashboard, undefined);
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

  test("uses project database routes and deletion/environment DTOs", async () => {
    const deletedDatabase = {
      databaseId,
      projectId,
      name: "primary",
      deleted: true,
      environmentsDeleted: 1,
      physicalDatabasesDeleted: 1,
      repositoriesDeleted: 2,
    };
    const { client, requests } = createCapturingClient(deletedDatabase);

    await client.databases.list();
    assertRequest(requests[0], "GET", `/api/v1/projects/${projectId}/databases`);
    assert.equal(requests[0]?.url.searchParams.get("skip"), "0");
    assert.equal(requests[0]?.url.searchParams.get("take"), "100");

    const deleteResult = await client.databases.delete(databaseId);
    assert.equal(deleteResult.ok, true);
    if (deleteResult.ok) {
      assert.deepEqual(deleteResult.value, deletedDatabase);
    }
    assertRequest(requests[1], "DELETE", `/api/v1/projects/${projectId}/databases/${databaseId}`);

    await client.databases.createEnvironment(databaseId, { name: "Preview", kind: "Preview" });
    assertRequest(
      requests[2],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/environments`,
    );
    assert.deepEqual(requests[2]?.body, { name: "Preview", kind: "Preview" });

    await client.databases.deleteEnvironment(databaseId, environmentId);
    assertRequest(
      requests[3],
      "DELETE",
      `/api/v1/projects/${projectId}/databases/${databaseId}/environments/${environmentId}`,
    );
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
      backupJob,
      [backupJob],
      backupJob,
      [restorePoint],
      restoreJob,
      restoreJob,
      [restoreJob],
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

    const triggerResult = await client.backups.triggerBackup(databaseId, {
      idempotencyKey: "manual-backup-1",
    });
    assert.equal(triggerResult.ok, true);
    if (triggerResult.ok) {
      assert.deepEqual(triggerResult.value, backupJob);
      assert.equal("terraBaseTaskId" in triggerResult.value, false);
      assert.equal("physicalDatabaseName" in triggerResult.value, false);
    }
    assertRequest(
      requests[1],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups`,
    );
    assert.deepEqual(requests[1]?.body, { idempotencyKey: "manual-backup-1" });

    const listBackupsResult = await client.backups.listBackups(databaseId, { skip: 5, take: 10 });
    assert.equal(listBackupsResult.ok, true);
    if (listBackupsResult.ok) {
      assert.deepEqual(listBackupsResult.value, [backupJob]);
      assert.equal("items" in listBackupsResult.value, false);
    }
    assertRequest(
      requests[2],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/backups`,
    );
    assert.equal(requests[2]?.url.searchParams.get("skip"), "5");
    assert.equal(requests[2]?.url.searchParams.get("take"), "10");

    await client.backups.getBackup(databaseId, backupId);
    assertRequest(
      requests[3],
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
      requests[4],
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
      requests[5],
      "POST",
      `/api/v1/projects/${projectId}/databases/${databaseId}/restores`,
    );
    assert.deepEqual(requests[5]?.body, { backupId, idempotencyKey: "restore-1" });

    await client.backups.getRestore(databaseId, restoreId);
    assertRequest(
      requests[6],
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
      requests[7],
      "GET",
      `/api/v1/projects/${projectId}/databases/${databaseId}/restores`,
    );
    assert.equal(requests[7]?.url.searchParams.get("skip"), "2");
    assert.equal(requests[7]?.url.searchParams.get("take"), "3");
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

function createCapturingClientWithResponses(responseBodies: readonly JsonValue[]): {
  readonly client: TerraScaleClient;
  readonly requests: readonly CapturedRequest[];
} {
  const requests: CapturedRequest[] = [];
  let responseIndex = 0;

  const client = createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    dashboardClientId: "sdk-route-tests",
    projectId,
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
