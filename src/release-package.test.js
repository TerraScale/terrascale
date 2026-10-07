import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);

test("packed SDK independently installs its project-scoped JavaScript and declarations", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-package-"));
  try {
    const consumer = path.join(workspace, "consumer");
    await mkdir(consumer);
    const npmEntrypoint = process.env.npm_execpath;
    assert.ok(npmEntrypoint, "Run package qualification through npm test.");
    assert.equal((await run(process.execPath, [npmEntrypoint, "--version"])).stdout.trim(), "11.18.0");
    const npmRun = (/** @type {string[]} */ args, /** @type {string} */ cwd) =>
      run(process.execPath, [npmEntrypoint, ...args], { cwd, maxBuffer: 4 * 1024 * 1024 });
    await npmRun(["run", "build"], root);
    const packed = await npmRun(["pack", "--ignore-scripts", "--json", "--pack-destination", workspace], root);
    const filename = JSON.parse(packed.stdout)[0].filename;
    assert.equal(path.basename(filename), filename);
    const tarball = path.join(workspace, filename);
    const listing = (await run("tar", ["-tf", tarball])).stdout;
    for (const entry of ["src/index.js", "src/v1/terrabase-v1.js", "types/index.d.ts", "sdk-v1-route-manifest.json"])
      assert.ok(listing.includes(`package/${entry}\n`), entry);
    assert.doesNotMatch(listing, /^package\/(?:test|scripts|contracts|node_modules)\/|\.test\.js$/mu);
    await writeFile(path.join(consumer, "package.json"), JSON.stringify({ type: "module" }));
    await npmRun(["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball, "typescript@5.9.3"], consumer);
    await writeFile(path.join(consumer, "caller.ts"), installedCaller);
    await npmRun(["exec", "--", "tsc", "--noEmit", "--strict", "--skipLibCheck", "--target", "ES2023", "--module", "NodeNext", "--moduleResolution", "NodeNext", "caller.ts"], consumer);
    await run(process.execPath, ["caller.ts"], { cwd: consumer });
    await run(process.execPath, ["--conditions=browser", "--input-type=module", "--eval",
      "import assert from 'node:assert/strict'; for (const entry of ['local','better-auth','postgres']) await assert.rejects(import('@terrascale/sdk/' + entry), { code: 'ERR_PACKAGE_PATH_NOT_EXPORTED' });"], { cwd: consumer });
    const declarations = await readFile(path.join(root, "types/index.d.ts"), "utf8");
    assert.doesNotMatch(declarations, /node:sqlite|local\/|database-types|discovery\.js/u);
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

const installedCaller = `import { createTerraBaseClient, createTerraBaseV1Client, TerraBaseV1Client, ExactDecimal } from "@terrascale/sdk";
const token = "eyJ0eXAiOiJhdCtqd3QifQ.eyJwcm9qZWN0X2lkIjoicHJvamVjdCJ9.c2lnbmF0dXJl";
const client = createTerraBaseClient({ project: "project", baseUrl: "http://127.0.0.1:18447", token });
if (!(client instanceof TerraBaseV1Client) || createTerraBaseClient !== createTerraBaseV1Client) throw new Error("Multiple client implementations.");
const collection = client.database("main").collection("users");
for (const name of ["get", "put", "create", "patch", "delete", "query"] as const) if (typeof collection[name] !== "function") throw new Error(name);
if (new ExactDecimal("9007199254740993").toString() !== "9007199254740993") throw new Error("Lost exact integer.");
if (JSON.stringify(client).includes(token)) throw new Error("Exposed credential.");
`;
