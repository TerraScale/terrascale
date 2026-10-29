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
        ".env.example",
        "README.md",
        "package.json",
        "tsconfig.json",
        "next-env.d.ts",
        "terrascale.ts",
        "app/page.tsx",
        "app/layout.tsx",
        "AGENTS.md",
      ];
      for (const file of files) {
        const firstText = await readFile(join(root, "first", file), "utf8");
        const secondText = await readFile(join(secondRoot, "second", file), "utf8");
        assert.equal(firstText, secondText, `${file} should be deterministic`);
      }
      const pkg = JSON.parse(await readFile(join(root, "first", "package.json"), "utf8"));
      assert.match(pkg.dependencies["@terrascale/sdk"], /^\d+\.\d+\.\d+$/);
      const client = await readFile(join(root, "first", "terrascale.ts"), "utf8");
      assert.match(client, /createTerraBaseV1Client/);
      assert.match(client, /grant_type: "client_credentials"/);
      const page = await readFile(join(root, "first", "app/page.tsx"), "utf8");
      assert.match(page, /\.query\(/);
      assert.match(page, /\.create\(/);
      for (const file of files) {
        const text = await readFile(join(root, "first", file), "utf8");
        assert.doesNotMatch(text, /NEXT_PUBLIC_TERRASCALE_API_KEY|createTerraScaleClient|npx create-terrascale-app/u, file);
      }
    } finally {
      await rm(secondRoot, { recursive: true, force: true });
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
