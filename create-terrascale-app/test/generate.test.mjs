import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import { test } from "node:test";

const packageRoot = fileURLToPath(new URL("..", import.meta.url));
const bin = join(packageRoot, "bin", "create-terrascale-app.mjs");

function runNode(args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, args, { cwd, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8").on("data", (chunk) => { stdout += chunk; });
    child.stderr.setEncoding("utf8").on("data", (chunk) => { stderr += chunk; });
    child.once("error", reject);
    child.once("close", (code) => resolve({ code, stdout, stderr }));
  });
}

test("scaffold is deterministic, cloud-only, and complete", async () => {
  const root = await mkdtemp(join(tmpdir(), "create-terrascale-app-"));
  try {
    const first = await runNode([bin, "first"], root);
    assert.equal(first.code, 0, first.stderr);
    const secondRoot = await mkdtemp(join(tmpdir(), "create-terrascale-app-"));
    try {
      const second = await runNode([bin, "second"], secondRoot);
      assert.equal(second.code, 0, second.stderr);
      const files = [
        "package.json",
        "tsconfig.json",
        "next-env.d.ts",
        "schema.ts",
        "terrascale.schema.json",
        "seed.ts",
        "app/page.tsx",
        "app/layout.tsx",
        "AGENTS.md",
      ];
      for (const file of files) {
        const firstText = await readFile(join(root, "first", file), "utf8");
        const secondText = await readFile(join(secondRoot, "second", file), "utf8");
        assert.equal(firstText, secondText, `${file} should be deterministic`);
      }
      const page = await readFile(join(root, "first", "app/page.tsx"), "utf8");
      assert.match(page, /client\.documents\.put/);
      assert.match(page, /TerraScaleSyncV2Client/);
      assert.match(page, /select\(posts\)/);
      assert.match(page, /parameters: queryParameters/);
      assert.doesNotMatch(page, /BroadcastChannel|localStorage|sqlite|node:sqlite/iu);
      const readme = await readFile(join(root, "first", "README.md"), "utf8");
      assert.match(readme, /no customer-run emulator/i);
      assert.match(readme, /local database\s+process/i);
      assert.doesNotMatch(readme, /local TerraBase|TerraBase emulator/i);
      assert.match(await readFile(join(root, "first", "terrascale.schema.json"), "utf8"), /"historyRetentionDays": 7/);
      assert.match(await readFile(join(root, "first", "seed.ts"), "utf8"), /documents\.put/);
    } finally {
      await rm(secondRoot, { recursive: true, force: true });
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
