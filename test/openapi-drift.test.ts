import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";
import { generateOpenApiContractDescriptor } from "../scripts/openapi-contract-descriptor.ts";
import { currentProducerOpenApi } from "../scripts/openapi-contract-paths.ts";
test.skipIf(process.env.TERRASCALE_CURRENT_OPENAPI === undefined)(
  "native descriptor matches the exact current producer including security/errors",
  async () => {
    const descriptor = await generateOpenApiContractDescriptor({
      manifestPath: new URL("../sdk-route-manifest.json", import.meta.url)
        .pathname,
      specPath: await currentProducerOpenApi(),
    });
    assert.deepEqual(
      descriptor,
      JSON.parse(
        await readFile(
          new URL("../sdk-openapi-contract-descriptor.json", import.meta.url),
          "utf8",
        ),
      ),
    );
  },
);
