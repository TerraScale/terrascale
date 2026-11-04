import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { test } from "vitest";
test.skipIf(process.env.TERRABASE_ENDPOINT === undefined)(
  "actual packaged native finite snapshot/poll customer journey",
  async () => {
    await promisify(execFile)(
      process.execPath,
      [
        new URL(
          "../../scripts/qualification/current-producer-consumer.js",
          import.meta.url,
        ).pathname,
      ],
      { env: process.env },
    );
  },
);
