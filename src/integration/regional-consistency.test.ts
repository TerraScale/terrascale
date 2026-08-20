import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, test } from "node:test";

import { createTerraScaleClient } from "../index.js";
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

        const write = before.value.changeToken === null || before.value.changeToken === undefined
          ? await client.documents.put(scope, documentId!, before.value.data)
          : await client.documents.put(
              scope,
              documentId!,
              before.value.data,
              { expectedChangeToken: before.value.changeToken },
            );
        assert.equal(write.ok, true);

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
        if (!before.ok || before.value.changeToken === null || before.value.changeToken === undefined) return;

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
