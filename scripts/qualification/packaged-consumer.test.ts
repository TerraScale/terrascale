import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { test } from "vitest";

const exec = promisify(execFile);

test("packaged consumer requires a scoped deny credential for qualification", async () => {
  const env = { ...process.env };
  for (const key of [
    "TERRASCALE_API_URL", "TERRASCALE_AUTH_URL", "TERRASCALE_PROJECT_ID",
    "TERRASCALE_DATABASE_ID", "TERRASCALE_API_KEY", "TERRASCALE_JOURNEY_RUN_ID",
    "TERRASCALE_SDK_TARBALL", "TERRASCALE_SDK_TARBALL_SHA256", "TERRASCALE_DENY_API_KEY",
  ]) delete env[key];

  await assert.rejects(
    exec(process.execPath, ["--experimental-strip-types", "scripts/qualification/packaged-consumer.ts", "--phase", "exercise"], { env }),
    (error: unknown) => {
      const message = error instanceof Error ? error.message : String(error);
      return message.includes("TERRASCALE_DENY_API_KEY");
    },
  );
});
