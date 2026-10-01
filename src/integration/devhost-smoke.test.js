import { test } from "vitest";
import { requireTerraScaleManagement } from "../management.js";
test.skipIf(process.env.TERRASCALE_MANAGEMENT_ALLOCATION === undefined)(
  "required real provisioning/catalog/credentials/admin lifecycle",
  () => {
    requireTerraScaleManagement();
  },
);
