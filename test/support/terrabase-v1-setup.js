// Test setup only: resolve routes from producerContract()'s verified pinned OpenAPI.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// Administrative routes are deliberately absent from public OpenAPI. This
// bootstrap declaration comes from the same SHA-bound source as producerContract().
const keySource = readFileSync(new URL("../../contracts/terrabase-csharp/CreateApiKeyEndpoint.cs", import.meta.url), "utf8");
const keyPath = keySource.match(/Post\("([^"]+)"\)/u)?.[1];
assert.ok(keyPath && keySource.includes('Name("createApiKey")'), "Pinned producer lacks its administrative key bootstrap.");

// Setup routes absent from public OpenAPI (management-user and hidden data-plane routes) are bound to their pinned endpoint source.
const hiddenSetupRoutes = Object.freeze({
  createOrganization: { file: "CreateOrganizationEndpoint.cs", name: "createOrganization", method: "POST", declaration: "Post(ManagementRoute.Organizations)", template: "/v1/management/organizations" },
  createProject: { file: "CreateProjectEndpoint.cs", name: "createProject", method: "POST", declaration: "Post(ManagementRoute.Projects)", template: "/v1/management/organizations/{organization}/projects" },
  createDatabase: { file: "CreateDatabaseEndpoint.cs", name: "legacyCreateDatabase", method: "PUT", declaration: "Put(DocumentRoute.DatabaseRoute)", template: "/v1/projects/{project}/databases/{database}" },
  setOrganizationPlan: { file: "SetOrganizationPlanEndpoint.cs", name: "setOrganizationPlan", method: "PUT", declaration: 'Put(ManagementRoute.Organization + "/plan")', template: "/v1/management/organizations/{organization}/plan" },
});
/** @param {string} name */
const readContract = name => readFileSync(new URL(`../../contracts/terrabase-csharp/${name}`, import.meta.url), "utf8");
const managementRouteSource = readContract("ManagementRoute.cs");
assert.ok(managementRouteSource.includes('Prefix = "/v1/management";') && managementRouteSource.includes('Organizations = Prefix + "/organizations";') && managementRouteSource.includes('Organization = Organizations + "/{organization}";') && managementRouteSource.includes('Projects = Organization + "/projects";'), "Pinned producer management route constants changed.");
assert.ok(readContract("DocumentRoute.cs").includes('DatabaseRoute = "/v1/projects/{project}/databases/{database}";'), "Pinned producer database route changed.");
for (const [operationId, { file, name, declaration }] of Object.entries(hiddenSetupRoutes)) {
  const source = readContract(file);
  assert.ok(source.includes(declaration) && source.includes(`Name("${name}")`), `Pinned producer lacks setup operation ${operationId}.`);
}

// Pinned producer discovery endpoint; fetching OpenAPI cannot depend on that schema.
export const producerBootstrapEndpoints = Object.freeze({ openapi: "/openapi/v1.json" });

/** @param {string} template @param {string} operationId @param {Readonly<Record<string, string>>} params */
function fillPath(template, operationId, params) {
  return template.replace(/\{([^}]+)\}/gu, (_match, name) => {
    const value = params[name];
    assert.ok(typeof value === "string" && value.trim() !== "", `${operationId}: missing path parameter ${name}.`);
    return encodeURIComponent(value);
  });
}

/** @param {import("../../scripts/terrabase-contract.mjs").ProducerOpenApi} contract @param {string} operationId @param {Readonly<Record<string, string>>} [params] */
export function provisioningRoute(contract, operationId, params = {}) {
  if (operationId === "createApiKey") return { method: "POST", path: /** @type {string} */ (keyPath) };
  const hidden = hiddenSetupRoutes[/** @type {keyof typeof hiddenSetupRoutes} */ (operationId)];
  if (hidden !== undefined) return { method: hidden.method, path: fillPath(hidden.template, operationId, params) };
  for (const [template, methods] of Object.entries(contract.paths)) {
    for (const [method, operation] of Object.entries(methods)) {
      if (operation.operationId !== operationId) continue;
      return { method: method.toUpperCase(), path: fillPath(template, operationId, params) };
    }
  }
  assert.fail(`Pinned OpenAPI has no provisioning operation ${operationId}.`);
}
