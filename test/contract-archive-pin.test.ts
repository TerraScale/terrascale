import { expect, test } from "vitest";
import { currentProducerOpenApi } from "../scripts/openapi-contract-paths.ts";
test("producer source parity refuses missing or relative source paths", async () => {
  const previous = process.env.TERRASCALE_CURRENT_OPENAPI;
  try {
    delete process.env.TERRASCALE_CURRENT_OPENAPI;
    await expect(currentProducerOpenApi()).rejects.toThrow(
      /absolute actual producer/u,
    );
    process.env.TERRASCALE_CURRENT_OPENAPI = "relative.json";
    await expect(currentProducerOpenApi()).rejects.toThrow(
      /absolute actual producer/u,
    );
  } finally {
    if (previous === undefined) delete process.env.TERRASCALE_CURRENT_OPENAPI;
    else process.env.TERRASCALE_CURRENT_OPENAPI = previous;
  }
});
