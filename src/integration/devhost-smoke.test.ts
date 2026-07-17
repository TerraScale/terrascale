import { describe, test } from "node:test";
import assert from "node:assert/strict";

import { createTerraScaleClient } from "../factory.js";
import { tryUlid } from "../ids.js";

const apiBaseUrl = process.env.TERRASCALE_API_BASE_URL;
const authBaseUrl = process.env.TERRASCALE_AUTH_API_BASE_URL;
const accessToken = process.env.TERRASCALE_E2E_ACCESS_TOKEN;
const projectId = tryUlid(process.env.TERRASCALE_E2E_PROJECT_ID ?? "");
const organizationId = tryUlid(process.env.TERRASCALE_E2E_ORGANIZATION_ID ?? "");

describe("DevHost smoke", { skip: apiBaseUrl === undefined || authBaseUrl === undefined }, () => {
  test("reaches the real auth HTTP boundary", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      dashboardClientId: "terrascale-sdk-integration",
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
        dashboardClientId: "terrascale-sdk-integration",
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
