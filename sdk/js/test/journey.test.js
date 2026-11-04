import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { expect, test } from "vitest";
import { journeyConfiguration } from "../scripts/qualification/journey-configuration.js";

const nativeJourney = journeyConfiguration(process.env, "native");

test("full customer release journey stays mandatory and fails honestly while producer authority is unavailable", async () => {
  await expect(
    promisify(execFile)(
      process.execPath,
      [
        new URL("../scripts/require-release-e2e-config.js", import.meta.url)
          .pathname,
      ],
      { env: { PATH: process.env.PATH ?? "" } },
    ),
  ).rejects.toThrow(/Release E2E remains blocked/u);
});
test.skipIf(nativeJourney === undefined)(
  "actual native installed-package customer journey",
  async () => {
    await promisify(execFile)(
      process.execPath,
      [
        new URL(
          "../scripts/qualification/current-producer-consumer.js",
          import.meta.url,
        ).pathname,
      ],
      { env: process.env },
    );
  },
);
