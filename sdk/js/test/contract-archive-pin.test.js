import { expect, test } from "vitest";
import { producerContract } from "../scripts/terrabase-contract.mjs";

test("runtime contract qualification refuses a missing real producer OpenAPI", async () => {
  const required = process.env.TB_V1_REQUIRED;
  const openapi = process.env.TERRABASE_V1_OPENAPI;
  try {
    process.env.TB_V1_REQUIRED = "1";
    delete process.env.TERRABASE_V1_OPENAPI;
    await expect(producerContract()).rejects.toThrow(/Real runtime OpenAPI/u);
  } finally {
    if (required === undefined) delete process.env.TB_V1_REQUIRED;
    else process.env.TB_V1_REQUIRED = required;
    if (openapi === undefined) delete process.env.TERRABASE_V1_OPENAPI;
    else process.env.TERRABASE_V1_OPENAPI = openapi;
  }
});
