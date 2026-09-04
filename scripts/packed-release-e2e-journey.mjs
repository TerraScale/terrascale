import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { createTerraScaleClient } from "@terrascale/sdk";

const apiBaseUrl = process.env.TERRASCALE_API_BASE_URL;
const accessToken = process.env.TERRASCALE_E2E_ACCESS_TOKEN;
const projectId = process.env.TERRASCALE_E2E_PROJECT_ID;
const organizationId = process.env.TERRASCALE_E2E_ORGANIZATION_ID;
const savedQueriesProjectId = process.env.TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID;
const client = createTerraScaleClient({ apiBaseUrl, accessToken, projectId });

const [organizations, projects, invitations, placements, usage, notifications] = await Promise.all([
  client.organizations.list(),
  client.organizations.listProjects(organizationId),
  client.organizations.listInvitations(organizationId),
  client.organizations.listProjectRegionPlacements(projectId),
  client.usage.getProjectUsageHistory("30d", projectId),
  client.notifications.list({
    projectId,
    organizationId,
    includeOrganizationNotifications: true,
  }),
]);
for (const result of [organizations, projects, invitations, placements, usage, notifications]) {
  assert.equal(result.ok, true, result.ok ? undefined : JSON.stringify(result.error));
}
if (organizations.ok) assert.equal(Array.isArray(organizations.value), true);
if (projects.ok) assert.equal(Array.isArray(projects.value), true);
if (invitations.ok) assert.equal(Array.isArray(invitations.value), true);
if (usage.ok) assert.equal(usage.value.points.every((point) => typeof point.storageBytes === "string"), true);

const suffix = randomUUID();
const savedQueries = createTerraScaleClient({
  apiBaseUrl,
  accessToken,
  projectId: savedQueriesProjectId,
  idempotencyKeyFactory: () => `packed-saved-query-${randomUUID()}`,
});
let savedQueryId;
try {
  const created = await savedQueries.savedQueries.create({
    name: `Packed SDK smoke ${suffix}`,
    query: "from packed_sdk_smoke",
  });
  assert.equal(created.ok, true, created.ok ? undefined : JSON.stringify(created.error));
  if (!created.ok) throw new Error("Packed SDK saved-query creation failed.");
  savedQueryId = created.value.savedQueryId;

  const listed = await savedQueries.savedQueries.list();
  assert.equal(listed.ok, true, listed.ok ? undefined : JSON.stringify(listed.error));
  if (listed.ok) {
    assert.equal(listed.value.items.some((value) => value.savedQueryId === savedQueryId), true);
  }

  const fetched = await savedQueries.savedQueries.get(savedQueryId);
  assert.equal(fetched.ok, true, fetched.ok ? undefined : JSON.stringify(fetched.error));
  if (!fetched.ok) throw new Error("Packed SDK saved-query read failed.");

  const updated = await savedQueries.savedQueries.update(savedQueryId, {
    name: `Packed SDK updated ${suffix}`,
    query: "from packed_sdk_smoke where active = true",
    revision: fetched.value.revision,
  });
  assert.equal(updated.ok, true, updated.ok ? undefined : JSON.stringify(updated.error));

  const stale = await savedQueries.savedQueries.update(savedQueryId, {
    name: `Packed SDK stale ${suffix}`,
    query: "from packed_sdk_smoke where active = false",
    revision: fetched.value.revision,
  });
  assert.equal(stale.ok, false);
  if (!stale.ok) {
    assert.equal(stale.status, 409);
    assert.equal(stale.error.kind, "conflict");
  }

  const invalid = await savedQueries.savedQueries.create({ name: "", query: "from packed_sdk_smoke" });
  assert.equal(invalid.ok, false);
  if (!invalid.ok) {
    assert.equal(invalid.status, 400);
    assert.equal(invalid.error.kind, "validation");
  }
} finally {
  if (savedQueryId !== undefined) {
    const deleted = await savedQueries.savedQueries.delete(savedQueryId);
    assert.equal(deleted.ok, true, deleted.ok ? undefined : JSON.stringify(deleted.error));
  }
}
