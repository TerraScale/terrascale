// Test setup only: use the producer's real Management and loopback operator routes.
import assert from "node:assert/strict";
import { writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
import { object, string } from "../../src/v1/decode.js";
import { database, collections as fixtureCollections } from "../../test/support/terrabase-v1-fixture.js";
import { v1Route, v1RoutePath } from "../../src/v1/routes.js";
import { producerContract } from "../terrabase-contract.mjs";
import { provisioningRoute } from "../../test/support/terrabase-v1-setup.js";
import { generateId, isValidId } from "../../src/ids.js";

/** @param {Response} response @param {string} method @param {string} route */
export async function provisioningResponse(response, method, route) {
  if (!response.ok)
    assert.fail(`Real provisioning ${method} ${route} failed with ${response.status}: ${await response.text()}`);
  return response.json();
}

/** @param {import("../../src/v1/json.js").JsonValue | undefined} value @param {string} field @param {string} route */
export function provisioningField(value, field, route) {
  const result = string(object(value, `${route} response`)[field], `${route} ${field}`);
  assert.ok(result.trim() !== "", `Real provisioning ${route} returned an empty ${field}.`);
  if (field === "id")
    assert.ok(isValidId(result), `Real provisioning ${route} returned a noncanonical entity ID; TS-754 requires a ULID producer release.`);
  return result;
}

/** @param {string} origin @param {string} route @param {import("../../src/v1/json.js").JsonInput | undefined} body @param {Record<string, string>} headers @param {string} [method] */
async function request(origin, route, body, headers, method = "POST") {
  const response = await fetch(`${origin}${route}`, { method, headers: { "Content-Type": "application/json", ...headers }, ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(10_000) });
  return provisioningResponse(response, method, route);
}

export async function provisionTerraBaseTenant() {
  const client = process.env.TB_V1_URL;
  const admin = process.env.TB_ADMIN_URL;
  const credential = process.env.TB_DASHBOARD_CREDENTIAL;
  assert.ok(client && admin && credential && process.env.TB_PROVISION_FILE, "Isolated provisioning configuration is required.");
  const contract = await producerContract();
  /** @type {Record<string, string>} */
  const managementHeaders = { Authorization: `Bearer ${credential}`, "X-TerraBase-User-Id": generateId() };
  const organizations = provisioningRoute(contract, "createOrganization");
  const organizationId = provisioningField(await request(admin, organizations.path, { name: "SDK integration" }, managementHeaders, organizations.method), "id", organizations.path);
  managementHeaders["X-TerraBase-Organization-Id"] = organizationId;
  const projects = provisioningRoute(contract, "createProject", { organization: organizationId });
  const projectId = provisioningField(await request(admin, projects.path, { name: "SDK integration" }, managementHeaders, projects.method), "id", projects.path);
  const apiKeys = provisioningRoute(contract, "createApiKey");
  /** @param {readonly string[]} scopes */
  const key = async scopes => {
    return provisioningField(await request(admin, apiKeys.path, { name: "SDK integration", scopes, organizationId, projectId }, {}, apiKeys.method), "secret", apiKeys.path);
  };
  const readScope = v1Route("getDocument").permission;
  const writeScope = v1Route("putDocument").permission;
  const token = await key([readScope, writeScope, v1Route("readShape").permission]);
  const readKey = await key([readScope]);
  const writeKey = await key([writeScope]);
  const collectionRoute = v1Route("createCollection");
  const collections = v1RoutePath(collectionRoute, { project: projectId, database });
  const databases = provisioningRoute(contract, "createDatabase", { project: projectId, database });
  await request(client, databases.path, undefined, { Authorization: `Bearer ${token}` }, databases.method);
  for (const name of Object.values(fixtureCollections))
    await request(client, collections, { name }, { Authorization: `Bearer ${token}` }, collectionRoute.method);
  await writeFile(process.env.TB_PROVISION_FILE, JSON.stringify({ TB_V1_URL: client, TB_V1_PROJECT: projectId, TB_V1_DATA_KEY: token, TB_V1_READ_KEY: readKey, TB_V1_WRITE_KEY: writeKey, TB_V1_DATABASE: database }), { mode: 0o600 });
  console.log("Provisioned real isolated organization, project, database, collections and scoped tb_ keys.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await provisionTerraBaseTenant();
