// The old Dashboard saved-query fixture was a second, hand-copied contract
// authority. The producer bundle now supplies the complete public fixture.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "vitest";

import { contractOpenApiPath, pinnedContractVersion, resolveContractBundle } from "../scripts/openapi-contract-paths.ts";

describe("pinned terrabase public contract", () => {
  test("contains the document, SQL, and reactive routes in v1", async () => {
    const bundle = await resolveContractBundle();
    try {
      const spec = JSON.parse(await readFile(await contractOpenApiPath(bundle), "utf8")) as {
        readonly openapi?: string;
        readonly paths?: Record<string, Record<string, { readonly operationId?: string }>>;
      };
      assert.equal(bundle.manifest.contractVersion, pinnedContractVersion);
      assert.equal(spec.openapi, "3.1.0");
      for (const [route, method, operationId] of [
        ["/api/v1/databases/{name}/collections/{collection}/documents", "get", "list_documents"],
        ["/api/v1/databases/{name}/sql", "post", "execute_sql"],
        ["/api/v1/reactive/views", "post", "attach_view"],
        ["/api/v1/reactive/views/{view}/{generation}/log", "get", "view_log"],
      ] as const) {
        assert.equal(spec.paths?.[route]?.[method]?.operationId, operationId);
      }
      assert.equal(spec.paths?.["/api/v1/projects/{projectId}/saved-queries"], undefined);
    } finally {
      await bundle.cleanup();
    }
  });
});
