import { test } from "vitest";
import { requireTerraScaleBetterAuth } from "../better-auth.js";
// Real authority qualification remains a required gate. Environment presence
// cannot turn the local SQLite adapter into admitted production persistence.
test.skipIf(process.env.TERRASCALE_AUTH_AUTHORITY_ALLOCATION === undefined)(
  "required production auth unique/CAS/consume/transaction/index/revocation/region authority",
  () => {
    requireTerraScaleBetterAuth();
  },
);
