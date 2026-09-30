import { test } from "vitest";
import { requireTerraScaleBetterAuth } from "../better-auth.js";
test.skipIf(process.env.TERRASCALE_REGIONAL_AUTH_ALLOCATION === undefined)(
  "required real cross-region unique/consume/revocation and denial qualification",
  () => {
    requireTerraScaleBetterAuth();
  },
);
