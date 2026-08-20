import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, test } from "node:test";

import { createTerraScaleClient, resolveRuntimeConfig } from "../index.js";
import { tryUlid } from "../ids.js";
import {
  fixtureCredentialFingerprint,
  withFixtureLease,
  type FixtureLeaseScope,
} from "./fixture-lease.js";

const apiBaseUrl = process.env.TERRASCALE_API_BASE_URL;
const authBaseUrl = process.env.TERRASCALE_AUTH_API_BASE_URL;
const accessToken = process.env.TERRASCALE_E2E_ACCESS_TOKEN;
const tenantId = tryUlid(process.env.TERRASCALE_E2E_TENANT_ID ?? "");
const projectId = tryUlid(process.env.TERRASCALE_E2E_PROJECT_ID ?? "");
const databaseId = tryUlid(process.env.TERRASCALE_E2E_DATABASE_ID ?? "");
const repositoryId = tryUlid(process.env.TERRASCALE_E2E_REPOSITORY_ID ?? "");
const documentId = tryUlid(process.env.TERRASCALE_E2E_DOCUMENT_ID ?? "");
const leasePath = process.env.TERRASCALE_E2E_DOCUMENT_WRITE_LEASE;
const routingRegion = process.env.TERRASCALE_E2E_ROUTING_REGION;
const strictRegion = process.env.TERRASCALE_E2E_STRICT_REGION;
const strictApiBaseUrl = process.env.TERRASCALE_E2E_STRICT_API_BASE_URL;
const unavailableRegion = process.env.TERRASCALE_E2E_UNAVAILABLE_REGION;
const unavailableApiBaseUrl = process.env.TERRASCALE_E2E_UNAVAILABLE_API_BASE_URL;

const fixtureSkipReason = missingFixtureConfiguration();

describe("fixture lease", () => {
  test("serializes competing processes and removes only its own lock", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-lease-"));
    const lockPath = path.join(directory, "document.lock");
    const scope = fixtureScope("tenant", "project", "database", "repository", "document");

    try {
      await assert.rejects(
        withFixtureLease("relative.lock", scope, async () => undefined),
        /absolute file path/u,
      );
      await withFixtureLease(lockPath, scope, async () => {
        const metadata = JSON.parse(await readFile(lockPath, "utf8")) as {
          readonly owner: string;
          readonly scope: FixtureLeaseScope;
        };
        assert.notEqual(metadata.owner, "");
        assert.deepEqual(metadata.scope, scope);
        await assert.rejects(
          withFixtureLease(lockPath, scope, async () => undefined),
          /already held/u,
        );
      });
      await assert.rejects(readFile(lockPath, "utf8"), { code: "ENOENT" });
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });
});

describe("real regional document consistency", { skip: fixtureSkipReason }, () => {
  test("round-trips a leased document through the real public API", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
      sessionConsistency: "automatic",
      maxRetryAttempts: 1,
      retryDelayMs: 0,
    });
    const scope = { repositoryId: repositoryId! };

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!, repositoryId!, documentId!),
      async () => {
        const before = await client.documents.get(scope, documentId!);
        assert.equal(before.ok, true);
        assert.notEqual(before.value.changeToken, null);
        assert.notEqual(before.value.changeToken, undefined);
        if (before.value.changeToken === null || before.value.changeToken === undefined) {
          assert.fail("The leased fixture document must have a change token.");
        }

        const write = await client.documents.put(
          scope,
          documentId!,
          before.value.data,
          { expectedChangeToken: before.value.changeToken },
        );
        assert.equal(write.ok, true);
        assert.notEqual(write.value.sessionConsistencyToken, null);
        assert.notEqual(write.value.sessionConsistencyToken, undefined);

        const after = await client.documents.get(scope, documentId!);
        assert.equal(after.ok, true);
        if (after.ok) {
          assert.equal(after.value.id, documentId);
          assert.deepEqual(after.value.data, before.value.data);
        }
      },
    );
  });

  test("classifies a real stale document write as a conflict without a simulated response", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
    });
    const scope = { repositoryId: repositoryId! };

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!, repositoryId!, documentId!),
      async () => {
        const before = await client.documents.get(scope, documentId!);
        assert.equal(before.ok, true);
        assert.notEqual(before.value.changeToken, null, "The leased fixture document must have a change token.");
        assert.notEqual(before.value.changeToken, undefined, "The leased fixture document must have a change token.");
        if (before.value.changeToken === null || before.value.changeToken === undefined) {
          assert.fail("The leased fixture document must have a change token.");
        }

        const advance = await client.documents.put(
          scope,
          documentId!,
          before.value.data,
          { expectedChangeToken: before.value.changeToken },
        );
        assert.equal(advance.ok, true);

        const stale = await client.documents.put(
          scope,
          documentId!,
          before.value.data,
          { expectedChangeToken: before.value.changeToken },
        );
        assert.equal(stale.ok, false);
        if (!stale.ok) assert.equal(stale.error.kind, "conflict");
      },
    );
  });

  test("uses the configured preferred region on the real failover endpoint", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
      preferredRegion: routingRegion!,
      regionAffinity: "failover",
    });

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!, repositoryId!, documentId!),
      async () => {
        const result = await client.documents.get({ repositoryId: repositoryId! }, documentId!);
        assert.equal(result.ok, true);
        if (result.ok) {
          assert.ok(result.routing, "The real API must return routing observations for regional qualification.");
          assert.ok(result.routing?.edgeRegion);
          assert.ok(result.routing?.servingRegion);
          assert.equal(result.routing?.routeMode, "preferred");
          assert.equal(result.routing?.servingRegion, routingRegion!.trim().toLowerCase());
          assert.equal(result.routing?.degraded, false);
        }
      },
    );
  });

  test("uses the configured strict regional endpoint on the real API", async () => {
    const resolved = resolveRuntimeConfig({
      apiBaseUrl: strictApiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      preferredRegion: strictRegion!,
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(new URL(resolved.apiBaseUrl).hostname, `${strictRegion}.api.terrascale.tech`);

    const client = createTerraScaleClient({
      apiBaseUrl: strictApiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
      preferredRegion: strictRegion!,
      regionAffinity: "strict",
    });

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!, repositoryId!, documentId!),
      async () => {
        const result = await client.documents.get({ repositoryId: repositoryId! }, documentId!);
        assert.equal(result.ok, true);
        if (result.ok) {
          assert.ok(result.routing, "The strict endpoint must return routing observations.");
          assert.equal(result.routing?.routeMode, "regional-strict");
          assert.equal(result.routing?.servingRegion, strictRegion!.trim().toLowerCase());
          assert.equal(result.routing?.degraded, false);
        }
      },
    );
  });

  test("classifies a real strict unavailable-region response and records its retry", async () => {
    const statuses: number[] = [];
    const resolved = resolveRuntimeConfig({
      apiBaseUrl: unavailableApiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      preferredRegion: unavailableRegion!,
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(new URL(resolved.apiBaseUrl).hostname, `${unavailableRegion}.api.terrascale.tech`);

    const client = createTerraScaleClient({
      apiBaseUrl: unavailableApiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
      preferredRegion: unavailableRegion!,
      regionAffinity: "strict",
      maxRetryAttempts: 1,
      retryDelayMs: 0,
      requestMetricRecorder: metric => {
        if (metric.status !== undefined) statuses.push(metric.status);
      },
    });

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!, repositoryId!, documentId!),
      async () => {
        const result = await client.documents.get({ repositoryId: repositoryId! }, documentId!);
        assert.equal(result.ok, false);
        if (!result.ok) {
          assert.equal(result.status, 503);
          assert.equal(result.error.kind, "unavailable");
          assert.equal(result.error.code, "project_region_unavailable");
        }
        assert.deepEqual(statuses, [503, 503]);
      },
    );
  });
});

function missingFixtureConfiguration(): string | false {
  const missing = [
    ["TERRASCALE_API_BASE_URL", apiBaseUrl],
    ["TERRASCALE_AUTH_API_BASE_URL", authBaseUrl],
    ["TERRASCALE_E2E_ACCESS_TOKEN", accessToken],
    ["TERRASCALE_E2E_TENANT_ID", tenantId],
    ["TERRASCALE_E2E_PROJECT_ID", projectId],
    ["TERRASCALE_E2E_DATABASE_ID", databaseId],
    ["TERRASCALE_E2E_REPOSITORY_ID", repositoryId],
    ["TERRASCALE_E2E_DOCUMENT_ID", documentId],
    ["TERRASCALE_E2E_DOCUMENT_WRITE_LEASE", leasePath],
    ["TERRASCALE_E2E_ROUTING_REGION", routingRegion],
    ["TERRASCALE_E2E_STRICT_REGION", strictRegion],
    ["TERRASCALE_E2E_STRICT_API_BASE_URL", strictApiBaseUrl],
    ["TERRASCALE_E2E_UNAVAILABLE_REGION", unavailableRegion],
    ["TERRASCALE_E2E_UNAVAILABLE_API_BASE_URL", unavailableApiBaseUrl],
  ].filter(([, value]) => value === undefined || (typeof value === "string" && value.trim().length === 0));

  return missing.length === 0
    ? false
    : `Set dedicated real-API fixture configuration: ${missing.map(([name]) => name).join(", ")}.`;
}

function fixtureScope(
  tenantId: string,
  projectId: string,
  databaseId: string,
  repositoryId: string,
  documentId: string,
): FixtureLeaseScope {
  return {
    tenantId,
    projectId,
    databaseId,
    repositoryId,
    documentId,
    apiBaseUrl: apiBaseUrl ?? "test://api",
    authBaseUrl: authBaseUrl ?? "test://auth",
    credentialFingerprint: fixtureCredentialFingerprint(accessToken ?? "test-token"),
  };
}
