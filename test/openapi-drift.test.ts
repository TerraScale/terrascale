// The terrabase bundle is the sole REST contract authority (D-33, D-47).
// This gate is intentionally fail-closed when no explicit local producer or
// immutable published bundle is configured.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, test } from "vitest";

import { generateOpenApiContractDescriptor } from "../scripts/openapi-contract-descriptor.ts";
import { contractOpenApiPath, pinnedContractVersion, resolveContractBundle, resolveEdgeContractBundle } from "../scripts/openapi-contract-paths.ts";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const descriptorPath = path.join(repositoryRoot, "sdk-openapi-contract-descriptor.json");
const manifestPath = path.join(repositoryRoot, "sdk-route-manifest.json");

describe("combined TerraBase and edge OpenAPI drift", () => {
  test("committed descriptor matches the pinned producer bundles", async () => {
    const bundle = await resolveContractBundle();
    const edgeBundle = await resolveEdgeContractBundle();
    try {
      const descriptor = await generateOpenApiContractDescriptor({
        manifestPath,
        specPath: await contractOpenApiPath(bundle),
        specPaths: { edge: await contractOpenApiPath(edgeBundle) },
      }) as { readonly routeCount: number; readonly routes: readonly unknown[] };
      const committed = JSON.parse(await readFile(descriptorPath, "utf8")) as typeof descriptor;
      assert.deepEqual(descriptor, committed);
      assert.equal(committed.routeCount, committed.routes.length);
    } finally {
      await edgeBundle.cleanup();
      await bundle.cleanup();
    }
  });

  test("manifest and descriptor pin the same v1 route set", async () => {
    const [manifest, descriptor] = await Promise.all([
      readFile(manifestPath, "utf8"),
      readFile(descriptorPath, "utf8"),
    ]).then((values) => values.map((value) => JSON.parse(value) as { readonly routeCount: number; readonly routes: readonly unknown[]; readonly contractVersion?: string }));
    assert.equal(manifest.contractVersion, pinnedContractVersion);
    assert.equal(manifest.routeCount, descriptor.routeCount);
    assert.equal(manifest.routes.length, descriptor.routes.length);
  });
});
