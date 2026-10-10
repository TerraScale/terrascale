import { expect, test, vi } from "vitest";
import { releaseAsset } from "./provision-terrabase.mjs";
import { provisioningField, provisioningResponse } from "./provision-terrabase-tenant.mjs";
import { producerBootstrapEndpoints, provisioningRoute } from "../../test/support/terrabase-v1-setup.js";
import { producerContract } from "../terrabase-contract.mjs";

const pin = { registryOrigin: "https://registry.example", producer: "TerraScale/terrabase", revision: "pinned-revision", version: "0.1.0", sha256: "pinned-sha256", asset: "terrabase-linux-amd64" };
const asset = { name: pin.asset, browser_download_url: `${pin.registryOrigin}/releases/download/${pin.asset}`, size: 123 };
const release = { draft: true, target_commitish: pin.revision, body: `terrabase ${pin.version}\n${pin.sha256}`, assets: [asset] };

test("bootstrap metadata supplies discovery while health follows the verified producer contract", async () => {
  const contract = await producerContract();
  expect(producerBootstrapEndpoints.openapi).toBe("/openapi/v1.json");
  expect(contract.paths[producerBootstrapEndpoints.openapi]).toBeUndefined();
  expect(provisioningRoute(contract, "healthz")).toEqual({ method: "GET", path: "/healthz" });

  const revised = structuredClone(contract);
  const health = revised.paths["/healthz"];
  expect(health).toBeDefined();
  delete revised.paths["/healthz"];
  revised.paths["/revised/health"] = /** @type {NonNullable<typeof health>} */ (health);
  expect(provisioningRoute(revised, "healthz")).toEqual({ method: "GET", path: "/revised/health" });
});

test("provisioning derives named setup paths and methods from the pinned producer OpenAPI", async () => {
  const contract = await producerContract();
  expect(provisioningRoute(contract, "createOrganization")).toEqual({ method: "POST", path: "/v1/management/organizations" });
  expect(provisioningRoute(contract, "createProject", { organization: "org/id" })).toEqual({ method: "POST", path: "/v1/management/organizations/org%2Fid/projects" });
  expect(provisioningRoute(contract, "setOrganizationPlan", { organization: "org/id" })).toEqual({ method: "PUT", path: "/v1/management/organizations/org%2Fid/plan" });
  expect(provisioningRoute(contract, "createApiKey")).toEqual({ method: "POST", path: "/admin/api-keys" });
  expect(provisioningRoute(contract, "createDatabase", { project: "project/id", database: "app" })).toEqual({ method: "PUT", path: "/v1/projects/project%2Fid/databases/app" });
  expect(() => provisioningRoute(contract, "createProject")).toThrow("missing path parameter organization");
  for (const organization of ["", "   "])
    expect(() => provisioningRoute(contract, "createProject", { organization })).toThrow("missing path parameter organization");
  expect(() => provisioningRoute(contract, "createDatabase", { project: "project" })).toThrow("missing path parameter database");
  expect(() => provisioningRoute(contract, "createDatabase", { database: "app" })).toThrow("missing path parameter project");
  expect(() => provisioningRoute(contract, "unknownOperation")).toThrow("no provisioning operation unknownOperation");

  // The operator bootstrap is hidden from public OpenAPI, but still verified
  // against the SHA-bound producer endpoint source.
  expect(contract.paths["/admin/api-keys"]).toBeUndefined();
  expect(provisioningRoute(contract, "createApiKey")).toEqual({ method: "POST", path: "/admin/api-keys" });
});

test("release metadata is validated before reading draft, notes or assets", () => {
  expect(releaseAsset(release, pin)).toEqual({ browser_download_url: asset.browser_download_url, size: asset.size });
  for (const value of [undefined, null, [], true, "release", 42])
    expect(() => releaseAsset(value, pin)).toThrow("Release metadata must be a nonnull object");
  for (const draft of [undefined, null, "true", 1])
    expect(() => releaseAsset({ ...release, draft }, pin)).toThrow("draft status");
  for (const body of [undefined, null, [], {}, 42])
    expect(() => releaseAsset({ ...release, body }, pin)).toThrow("Release notes must be a string");
  for (const assets of [undefined, null, {}, "assets", 42])
    expect(() => releaseAsset({ ...release, assets }, pin)).toThrow("Release assets must be an array");
  for (const assets of [[], [null, 42, "asset", {}], [{ ...asset, name: "other-binary" }]])
    expect(() => releaseAsset({ ...release, assets }, pin)).toThrow("Release binary is missing");
  expect(releaseAsset({ ...release, assets: [null, asset] }, pin)).toEqual({ browser_download_url: asset.browser_download_url, size: asset.size });
  expect(() => releaseAsset({ ...release, target_commitish: "other-revision" }, pin)).toThrow("source revision changed");
  expect(() => releaseAsset({ ...release, body: `terrabase ${pin.version}` }, pin)).toThrow("pinned SHA-256");
  expect(() => releaseAsset({ ...release, body: `terrabase 0x1x0\n${pin.sha256}` }, pin)).toThrow();
});

test("selected release asset URL and size are validated before download", () => {
  for (const browser_download_url of [undefined, null, {}, 42, "", "   ", "/relative", "https:registry.example/binary", "http://registry.example/binary", "https://storage.example/binary", "https://user:password@registry.example/binary"]) {
    expect(() => releaseAsset({ ...release, assets: [{ ...asset, browser_download_url }] }, pin)).toThrow();
  }
  for (const size of [undefined, null, "123", 0, -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1])
    expect(() => releaseAsset({ ...release, assets: [{ ...asset, size }] }, pin)).toThrow("positive safe integer");
});

test("tenant IDs and key secrets reject malformed responses with endpoint diagnostics", () => {
  const fields = /** @type {const} */ ([["id", "/v1/management/organizations"], ["id", "/v1/management/organizations/org/projects"], ["secret", "/admin/api-keys"]]);
  for (const [field, route] of fields) {
    const valid = field === "id" ? "01ARZ3NDEKTSV4RRFFQ69G5FAV" : "valid-value";
    expect(provisioningField({ [field]: valid }, field, route)).toBe(valid);
    for (const value of [undefined, null, [], true, "response", 42, {}, { [field]: null }, { [field]: 42 }, { [field]: "" }, { [field]: "   " }])
      expect(() => provisioningField(value, field, route)).toThrow(route);
    if (field === "id") {
      for (const id of ["00000000-0000-0000-0000-000000000001", valid.toLowerCase(), `org_${valid}`, "8" + valid.slice(1)])
        expect(() => provisioningField({ id }, field, route)).toThrow("TS-754 requires a ULID producer release");
    }
  }
});

test("successful provisioning consumes JSON once without cloning or reading error text", async () => {
  const response = new Response('{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}', { status: 201 });
  const json = vi.spyOn(response, "json");
  const text = vi.spyOn(response, "text");
  const clone = vi.spyOn(response, "clone");
  await expect(provisioningResponse(response, "POST", "/v1/management/organizations")).resolves.toEqual({ id: "01ARZ3NDEKTSV4RRFFQ69G5FAV" });
  expect(json).toHaveBeenCalledTimes(1);
  expect(text).not.toHaveBeenCalled();
  expect(clone).not.toHaveBeenCalled();
  expect(response.bodyUsed).toBe(true);
});

test("failed provisioning reads error text once and reports method, endpoint and status", async () => {
  const response = new Response("permission denied", { status: 403 });
  const json = vi.spyOn(response, "json");
  const text = vi.spyOn(response, "text");
  const clone = vi.spyOn(response, "clone");
  await expect(provisioningResponse(response, "POST", "/admin/api-keys"))
    .rejects.toThrow("Real provisioning POST /admin/api-keys failed with 403: permission denied");
  expect(text).toHaveBeenCalledTimes(1);
  expect(json).not.toHaveBeenCalled();
  expect(clone).not.toHaveBeenCalled();
  expect(response.bodyUsed).toBe(true);
});
