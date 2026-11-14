import { execFile } from "node:child_process";
import { join } from "node:path";
import { promisify } from "node:util";
import { test, expect } from "@playwright/test";
import { e2eRoot } from "../support/stack";

test("a revoked canary credential and an unreachable component show unknown", async () => {
  const { stdout } = await promisify(execFile)(
    process.execPath,
    [
      "--experimental-strip-types",
      "--import",
      join(e2eRoot, "support/status-ts-register.mjs"),
      join(e2eRoot, "support/status-unknown-proof.mjs"),
    ],
    { env: { ...process.env, NODE_NO_WARNINGS: "1" } },
  );
  expect(stdout).toContain("revoked credential: unknown");
  expect(stdout).toContain("unreachable component: unknown");
  expect(stdout).toContain("partial measurement: unknown");
});
