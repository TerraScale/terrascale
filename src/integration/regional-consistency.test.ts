import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, test } from "vitest";

import {
  createTerraScaleClient,
  isRegionCode,
  resolveRuntimeConfig,
  type TerraScaleRegionCode,
} from "../index.js";
import {
  fixtureCredentialFingerprint,
  withFixtureLease,
  type FixtureLeaseScope,
} from "./fixture-lease.js";

const apiBaseUrl = process.env.TERRASCALE_API_BASE_URL;
const accessToken = process.env.TERRASCALE_E2E_ACCESS_TOKEN;
const tenantId = process.env.TERRASCALE_E2E_TENANT_ID?.trim() || undefined;
const projectId = process.env.TERRASCALE_E2E_PROJECT_ID?.trim() || undefined;
const databaseId = process.env.TERRASCALE_E2E_DATABASE_ID?.trim() || undefined;
const leasePath = process.env.TERRASCALE_E2E_DATABASE_WRITE_LEASE;
const routingRegion = exactRegionFromEnvironment("TERRASCALE_E2E_ROUTING_REGION");
const strictRegion = exactRegionFromEnvironment("TERRASCALE_E2E_STRICT_REGION");
const strictApiBaseUrl = process.env.TERRASCALE_E2E_STRICT_API_BASE_URL;
const unavailableRegion = exactRegionFromEnvironment("TERRASCALE_E2E_UNAVAILABLE_REGION");
const unavailableApiBaseUrl = process.env.TERRASCALE_E2E_UNAVAILABLE_API_BASE_URL;

const fixtureSkipReason = missingFixtureConfiguration();

describe("fixture lease", () => {
  test("serializes competing processes and removes only its own lock", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-lease-"));
    const lockPath = path.join(directory, "database.lock");
    const scope = fixtureScope("tenant", "project", "database");

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

describe.skipIf(Boolean(fixtureSkipReason))(
  "real regional routing consistency",
() => {
  test("reads the leased database through the real public API", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
    });

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!),
      async () => {
        const result = await client.databases.get(databaseId!, projectId!);
        assert.equal(result.ok, true);
        if (result.ok) assert.equal(result.value.databaseId, databaseId);
      },
    );
  });

  test("uses the configured preferred region on the real failover endpoint", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
      preferredRegion: routingRegion!,
      regionAffinity: "failover",
    });

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!),
      async () => {
        const result = await client.databases.get(databaseId!, projectId!);
        assert.equal(result.ok, true);
        if (result.ok) {
          assert.ok(result.routing, "The real API must return routing observations for regional qualification.");
          assert.ok(result.routing?.edgeRegion);
          assert.ok(result.routing?.servingRegion);
          assert.equal(result.routing?.routeMode, "preferred");
          assert.equal(result.routing?.servingRegion, routingRegion);
          assert.equal(result.routing?.degraded, false);
        }
      },
    );
  });

  test("uses the configured strict regional endpoint on the real API", async () => {
    const resolved = resolveRuntimeConfig({
      apiBaseUrl: strictApiBaseUrl!,
      preferredRegion: strictRegion!,
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(new URL(resolved.apiBaseUrl).hostname, `${strictRegion!.toLowerCase()}.terrascale.tech`);

    const client = createTerraScaleClient({
      apiBaseUrl: strictApiBaseUrl!,
      accessToken: accessToken!,
      projectId: projectId!,
      preferredRegion: strictRegion!,
      regionAffinity: "strict",
    });

    await withFixtureLease(
      leasePath!,
      fixtureScope(tenantId!, projectId!, databaseId!),
      async () => {
        const result = await client.databases.get(databaseId!, projectId!);
        assert.equal(result.ok, true);
        if (result.ok) {
          assert.ok(result.routing, "The strict endpoint must return routing observations.");
          assert.equal(result.routing?.routeMode, "regional-strict");
          assert.equal(result.routing?.servingRegion, strictRegion);
          assert.equal(result.routing?.degraded, false);
        }
      },
    );
  });

  test("classifies a real strict unavailable-region response and records its retry", async () => {
    const statuses: number[] = [];
    const resolved = resolveRuntimeConfig({
      apiBaseUrl: unavailableApiBaseUrl!,
      preferredRegion: unavailableRegion!,
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(new URL(resolved.apiBaseUrl).hostname, `${unavailableRegion!.toLowerCase()}.terrascale.tech`);

    const client = createTerraScaleClient({
      apiBaseUrl: unavailableApiBaseUrl!,
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
      fixtureScope(tenantId!, projectId!, databaseId!),
      async () => {
        const result = await client.backups.executeBackup(
          databaseId!,
          { operationSessionId: "regional-qualification-session", confirmationToken: "regional-qualification-token" },
          "regional-qualification-key",
        );
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
    ["TERRASCALE_E2E_ACCESS_TOKEN", accessToken],
    ["TERRASCALE_E2E_TENANT_ID", tenantId],
    ["TERRASCALE_E2E_PROJECT_ID", projectId],
    ["TERRASCALE_E2E_DATABASE_ID", databaseId],
    ["TERRASCALE_E2E_DATABASE_WRITE_LEASE", leasePath],
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

function exactRegionFromEnvironment(name: string): TerraScaleRegionCode | undefined {
  const value = process.env[name];
  return isRegionCode(value) ? value : undefined;
}

function fixtureScope(
  tenantId: string,
  projectId: string,
  databaseId: string,
): FixtureLeaseScope {
  return {
    tenantId,
    projectId,
    databaseId,
    apiBaseUrl: apiBaseUrl ?? "test://api",
    credentialFingerprint: fixtureCredentialFingerprint(accessToken ?? "test-token"),
  };
}
