import { expect, test } from "vitest";
import { createTerraScaleIdentityManagementClient, createTerraScaleManagementClient, requireTerraScaleManagement } from "./management.js";
import { V1Databases } from "./v1/catalog.js";
import { V1Database } from "./v1/terrabase-v1.js";

// Isolated construction/contract tests never call a product boundary.
const config = Object.freeze({
  baseUrl: "https://management.example.invalid",
  project: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
  token: "test.payload.signature",
});

const identityConfig = Object.freeze({ apiBaseUrl: "https://identity.example.invalid", signInToken: config.token });

test("management entrypoints construct the existing project-scoped named clients", () => {
  for (const create of [createTerraScaleManagementClient, requireTerraScaleManagement]) {
    const client = create(config);
    expect(client.databases).toBeInstanceOf(V1Databases);
    // A detached selector retains its root client's private project transport.
    const database = client.database;
    expect(database("app")).toBeInstanceOf(V1Database);
    expect(database("app").backups).toBeDefined();
    expect(Object.isFrozen(client)).toBe(true);
    expect(JSON.stringify(client)).not.toContain(config.token);
    expect(Object.keys(client).sort()).toEqual([
      "backupDestination", "database", "databases", "metrics",
    ]);
  }
});

test("project API key creation sends a stable database ULID and listing preserves it", async () => {
  const database = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
  const key = { id: config.project, name: "test", organization_id: config.project, project_id: config.project,
    database, scopes: ["sql:read"], created_by: config.project, created_at: "2026-10-26T00:00:00Z",
    expires_at: null, previous_secret_expires_at: null, last_used_at: null, allowed_cidrs: [] };
  const client = createTerraScaleIdentityManagementClient({ ...identityConfig, fetch: async input => {
    const request = /** @type {Request} */ (input);
    expect(new URL(request.url).pathname).toBe(`/v1/terrascale/organizations/${config.project}/projects/${config.project}/api-keys`);
    if (request.method === "POST") {
      expect(await request.json()).toEqual({ name: "test", scopes: ["sql:read"], database });
      return Response.json({ api_key: key, key: `${config.project}_${"a".repeat(52)}` }, { status: 201 });
    }
    expect(request.method).toBe("GET");
    return Response.json({ items: [key], next: null });
  } });
  expect(await client.createProjectApiKey(config.project, config.project, { name: "test", scopes: ["sql:read"], database }))
    .toMatchObject({ ok: true, value: { api_key: { database } } });
  expect(await client.listProjectApiKeys(config.project, config.project))
    .toMatchObject({ ok: true, value: { items: [{ database }] } });
});

test("organization deletion and ownership-transfer start send the step-up proof header", async () => {
  /** @type {string[]} */
  const proofs = [];
  const client = createTerraScaleIdentityManagementClient({ ...identityConfig, fetch: async input => {
    const request = /** @type {Request} */ (input);
    proofs.push(/** @type {string} */ (request.headers.get("x-terrascale-step-up-proof")));
    if (request.method === "DELETE") return new Response(null, { status: 204 });
    expect(new URL(request.url).pathname).toBe(`/v1/terrascale/organizations/${config.project}/ownership-transfer`);
    expect(await request.json()).toEqual({ user_id: config.project });
    return Response.json({ from_user_id: config.project, to_user_id: config.project,
      created_at: "2026-11-08T00:00:00Z", expires_at: "2026-11-09T00:00:00Z" }, { status: 201 });
  } });
  expect(await client.deleteOrganization(config.project, { stepUpProof: "fixture-proof" })).toMatchObject({ ok: true });
  expect(await client.startOwnershipTransfer(config.project, { user_id: config.project }, { stepUpProof: "fixture-proof" }))
    .toMatchObject({ ok: true, value: { to_user_id: config.project } });
  expect(proofs).toEqual(["fixture-proof", "fixture-proof"]);
  await expect(client.deleteOrganization(config.project)).rejects.toThrow(TypeError("stepUpProof must be 1-8192 characters without whitespace or control characters."));
});
