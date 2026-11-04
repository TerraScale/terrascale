import { test } from "vitest";
import { createTerraBaseClient } from "../src/index.js";
import { verifyShapeTokenIdentity } from "./support/shape-token-identity.js";

test.each([false, true])("shape reads reauthorize callback identities, including late/cancelled keys (async: %s)", async asynchronous => {
  await verifyShapeTokenIdentity(createTerraBaseClient, asynchronous);
});
