import { expect, test } from "vitest";
import { createTerraScaleManagementClient } from "../management.js";
// Catalog authorization needs databases:read; the document test key cannot grant it.
const configured = ["TB_V1_URL", "TB_V1_PROJECT", "TB_V1_MANAGEMENT_TOKEN"].every(name => Boolean(process.env[name]));
test.skipIf(!configured)(
  "management reads the authorized live database catalog",
  async () => {
    const client = createTerraScaleManagementClient({
      baseUrl: /** @type {string} */ (process.env.TB_V1_URL),
      project: /** @type {string} */ (process.env.TB_V1_PROJECT),
      token: /** @type {string} */ (process.env.TB_V1_MANAGEMENT_TOKEN),
    });
    const page = await client.databases.list({ limit: 1 });
    expect(page.items.length).toBeLessThanOrEqual(1);
    for (const database of page.items) expect(database.projectId).toBe(process.env.TB_V1_PROJECT);
  },
);
