import { describe, test } from "node:test";
import assert from "node:assert/strict";

import { createTerraScaleClient } from "../factory.js";
import { type Ulid, tryUlid } from "../ids.js";

const apiBaseUrl = process.env.TERRASCALE_API_BASE_URL;
const authBaseUrl = process.env.TERRASCALE_AUTH_API_BASE_URL;
const accessToken = process.env.TERRASCALE_E2E_ACCESS_TOKEN;
const projectId = tryUlid(process.env.TERRASCALE_E2E_PROJECT_ID ?? "");
const organizationId = tryUlid(process.env.TERRASCALE_E2E_ORGANIZATION_ID ?? "");
const savedQueriesProjectId = tryUlid(process.env.TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID ?? "");
const savedQueriesSkipReason =
  apiBaseUrl === undefined || authBaseUrl === undefined || accessToken === undefined
    ? "Set TERRASCALE_API_BASE_URL, TERRASCALE_AUTH_API_BASE_URL, and TERRASCALE_E2E_ACCESS_TOKEN."
    : savedQueriesProjectId === undefined
      ? "Set TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID to a dedicated disposable DevHost project with saved-query permissions."
      : false;

describe("DevHost smoke", { skip: apiBaseUrl === undefined || authBaseUrl === undefined }, () => {
  test("reaches the real auth HTTP boundary", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
    });

    const result = await client.auth.getSession();

    assert.equal(result.ok, false);
    assert.equal(result.routeName, "auth.session.get");
    assert.equal(typeof result.status, "number");
  });
});

describe(
  "AppHost authenticated contracts",
  {
    skip:
      apiBaseUrl === undefined ||
      authBaseUrl === undefined ||
      accessToken === undefined ||
      projectId === undefined ||
      organizationId === undefined,
  },
  () => {
    test("round-trips organization, placement, usage, and notification contracts", async () => {
      const client = createTerraScaleClient({
        apiBaseUrl: apiBaseUrl!,
        authBaseUrl: authBaseUrl!,
        accessToken: accessToken!,
        projectId: projectId!,
      });

      const [organizations, projects, invitations, placements, usage, notifications] =
        await Promise.all([
          client.organizations.list(),
          client.organizations.listProjects(organizationId!),
          client.organizations.listInvitations(organizationId!),
          client.organizations.listProjectRegionPlacements(projectId!),
          client.usage.getProjectUsageHistory("30d", projectId!),
          client.notifications.list({
            projectId: projectId!,
            organizationId: organizationId!,
            includeOrganizationNotifications: true,
          }),
        ]);

      assert.equal(organizations.ok, true);
      assert.equal(projects.ok, true);
      assert.equal(invitations.ok, true);
      assert.equal(placements.ok, true);
      assert.equal(usage.ok, true);
      assert.equal(notifications.ok, true);
      if (organizations.ok) assert.equal(Array.isArray(organizations.value), true);
      if (projects.ok) assert.equal(Array.isArray(projects.value), true);
      if (invitations.ok) assert.equal(Array.isArray(invitations.value), true);
      if (usage.ok) {
        assert.equal(
          usage.value.points.every((point) => typeof point.storageBytes === "string"),
          true,
        );
      }
      if (notifications.ok && notifications.value.length > 0) {
        assert.equal(typeof notifications.value[0]?.category, "string");
      }
    });
  },
);

describe("DevHost saved-query CRUD", { skip: savedQueriesSkipReason }, () => {
  test("uses the public SDK against a dedicated opt-in project", async () => {
    const suffix = crypto.randomUUID();
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      accessToken: accessToken!,
      projectId: savedQueriesProjectId!,
      idempotencyKeyFactory: () => `saved-query-${crypto.randomUUID()}`,
    });
    let savedQueryId: Ulid | undefined;

    try {
      const created = await client.savedQueries.create({
        name: `SDK smoke ${suffix}`,
        query: "from sdk_smoke",
      });
      assert.equal(created.ok, true);
      if (!created.ok) return;
      savedQueryId = created.value.savedQueryId;

      const listed = await client.savedQueries.list();
      assert.equal(listed.ok, true);
      if (listed.ok) {
        assert.equal(listed.value.some((savedQuery) => savedQuery.savedQueryId === savedQueryId), true);
      }

      const updated = await client.savedQueries.update(savedQueryId, {
        name: `SDK smoke updated ${suffix}`,
        query: "from sdk_smoke where active = true",
      });
      assert.equal(updated.ok, true);
      if (updated.ok) assert.equal(updated.value.name, `SDK smoke updated ${suffix}`);

      const invalid = await client.savedQueries.create({ name: "", query: "from sdk_smoke" });
      assert.equal(invalid.ok, false);
      if (!invalid.ok) {
        assert.equal(invalid.status, 400);
        assert.equal(invalid.error.kind, "validation");
      }
    } finally {
      if (savedQueryId !== undefined) {
        const deleted = await client.savedQueries.delete(savedQueryId);
        assert.equal(deleted.ok, true);
        if (deleted.ok) assert.equal(deleted.value.deleted, true);
      }
    }
  });
});
