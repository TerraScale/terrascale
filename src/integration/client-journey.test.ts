import { describe, test } from "node:test";
import assert from "node:assert/strict";

import { createTerraScaleClient } from "../factory.js";
import { type Ulid, tryUlid } from "../ids.js";

const apiBaseUrl = process.env.TERRASCALE_E2E_BASE_URL;
const apiKey = process.env.TERRASCALE_E2E_API_KEY;
const projectId = tryUlid(process.env.TERRASCALE_E2E_PROJECT_ID ?? "");
const databaseId = tryUlid(process.env.TERRASCALE_E2E_DATABASE_ID ?? "");
const environmentId = tryUlid(process.env.TERRASCALE_E2E_ENVIRONMENT_ID ?? "");
const skipReason =
  apiBaseUrl === undefined || apiKey === undefined
    ? "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_API_KEY, TERRASCALE_E2E_PROJECT_ID, TERRASCALE_E2E_DATABASE_ID, and TERRASCALE_E2E_ENVIRONMENT_ID."
    : projectId === undefined || databaseId === undefined || environmentId === undefined
      ? "Set TERRASCALE_E2E_PROJECT_ID, TERRASCALE_E2E_DATABASE_ID, and TERRASCALE_E2E_ENVIRONMENT_ID to the 26-character ULIDs provisioned for the journey."
      : false;

type JourneyDocument = {
  readonly name: string;
  readonly status: string;
  readonly visits: number;
};

describe("SDK client journey", { skip: skipReason }, () => {
  test("creates a repository in the provisioned environment, round-trips a document, and cleans up", async () => {
    const suffix = crypto.randomUUID().slice(0, 8);
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      projectId: projectId!,
      defaultHeaders: { "x-terrascale-auth-key": apiKey! },
    });
    let repositoryId: Ulid | undefined;

    try {
      const createdRepository = await client.repositories.create({
        name: `sdk-journey-${suffix}`,
        databaseId: databaseId!,
        environmentId: environmentId!,
        collection: `sdkjourney${suffix}`,
        schemaValidationEnabled: false,
      });
      assert.equal(createdRepository.ok, true);
      if (!createdRepository.ok) return;
      repositoryId = createdRepository.value.repositoryId;
      assert.equal(createdRepository.value.databaseId, databaseId);
      assert.equal(createdRepository.value.environmentId, environmentId);
      assert.equal(createdRepository.value.schemaValidationEnabled, false);

      const scope = { repositoryId };
      const expectedData: JourneyDocument = {
        name: `sdk-journey-${suffix}`,
        status: "created",
        visits: 1,
      };
      const created = await client.documents.create<JourneyDocument>(scope, expectedData);
      assert.equal(created.ok, true);
      if (!created.ok) return;
      assert.equal(created.value.id.length, 26);
      assert.deepEqual(created.value.data, expectedData);

      const read = await client.documents.get<JourneyDocument>(scope, created.value.id);
      assert.equal(read.ok, true);
      if (read.ok) {
        assert.equal(read.value.id, created.value.id);
        assert.deepEqual(read.value.data, expectedData);
      }

      const page = await client.documents.list<JourneyDocument>(scope, { skip: 0, take: 100 });
      assert.equal(page.ok, true);
      if (page.ok) {
        const listed = page.value.items.find((item) => item.id === created.value.id);
        assert.notEqual(listed, undefined);
        assert.deepEqual(listed?.data, expectedData);
      }
    } finally {
      if (repositoryId !== undefined) {
        const deleted = await client.repositories.delete(
          { repositoryId },
          { deleteDocuments: true },
        );
        assert.equal(deleted.ok, true);
        if (deleted.ok) assert.equal(deleted.value.deleted, true);
      }
    }
  });
});
