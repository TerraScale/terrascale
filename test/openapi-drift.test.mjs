// OpenAPI drift gate (Vitest port of scripts/check-openapi-drift.mjs, D-47).
// The committed sdk-openapi-contract-descriptor.json must equal what the
// generator produces from the committed route manifest plus the
// owner-published Dashboard OpenAPI snapshot. Regeneration fails on any
// SDK route the spec does not cover, on untyped request/response drift, and
// on source-contract mismatch, so byte parity is the single drift gate.
//
// Skips when the Dashboard OpenAPI snapshot is absent (fresh checkout
// without the sibling repository); CI clones it before running npm test.

import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, test } from "vitest";

import { canonicalDashboardOpenApiSpec } from "../scripts/openapi-contract-paths.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const descriptorPath = path.join(repositoryRoot, "sdk-openapi-contract-descriptor.json");
const manifestPath = path.join(repositoryRoot, "sdk-route-manifest.json");

async function specAvailable() {
  try {
    await access(canonicalDashboardOpenApiSpec);
    return true;
  } catch {
    return false;
  }
}

describe("OpenAPI drift", () => {
  test.skipIf(!(await specAvailable()))(
    "committed descriptor matches regeneration from the manifest and owner spec",
    async () => {
      const { generateOpenApiContractDescriptor } = await import(
        "../scripts/openapi-contract-descriptor.mjs"
      );
      const descriptor = await generateOpenApiContractDescriptor({ manifestPath, specPath: canonicalDashboardOpenApiSpec });
      const committed = JSON.parse(await readFile(descriptorPath, "utf8"));

      assert.deepEqual(descriptor, committed);
      assert.equal(committed.routeCount, 60);
    },
  );

  test("committed manifest route count matches the descriptor", async () => {
    const [manifest, descriptor] = await Promise.all([
      readFile(manifestPath, "utf8"),
      readFile(descriptorPath, "utf8"),
    ]).then((values) => values.map((value) => JSON.parse(value)));

    assert.equal(manifest.routeCount, descriptor.routeCount);
    assert.equal(manifest.routes.length, descriptor.routeCount);
  });
});
