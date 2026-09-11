import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { journeySource } from "./packaged-consumer.ts";

test("emitted standalone consumer compiles against the installed public SDK declarations", async () => {
  const root = await mkdtemp(join(tmpdir(), "ts-generated-typecheck-"));
  const dashboard = new URL("../../../ts-dashboard/", import.meta.url).pathname;
  try {
    await mkdir(join(root, "node_modules", "@terrascale"), { recursive: true });
    await symlink(join(dashboard, "node_modules", "@terrascale", "sdk"), join(root, "node_modules", "@terrascale", "sdk"));
    await symlink(join(dashboard, "node_modules", "@types"), join(root, "node_modules", "@types"));
    await writeFile(join(root, "package.json"), JSON.stringify({ type: "module" }));
    const file = join(root, "journey.ts");
    await writeFile(file, journeySource("exercise"));
    await promisify(execFile)(join(dashboard, "node_modules", ".bin", "tsc"), ["--ignoreConfig", "--types", "node", "--target", "ES2023", "--module", "NodeNext", "--moduleResolution", "NodeNext", "--strict", "--skipLibCheck", "--noEmit", file], { cwd: root });
  } finally { await rm(root, { recursive: true, force: true }); }
});

// Executes the generated standalone producer with a module-boundary fixture;
// this tests generation/peer assertions, never claims live SDK qualification.
test("emitted standalone verifier resolves all five peers and enforces their bodies", async () => {
  const root = await mkdtemp(join(tmpdir(), "ts-generated-journey-"));
  try {
    const sdk = join(root, "node_modules", "@terrascale", "sdk");
    await mkdir(sdk, { recursive: true });
    await writeFile(join(root, "package.json"), JSON.stringify({ type: "module" }));
    await writeFile(join(sdk, "package.json"), JSON.stringify({ type: "module", exports: "./index.js" }));
    await writeFile(join(sdk, "index.js"), `export function createTerraScaleClient(){return {documents:{get:async(_db,_collection,id)=>({ok:true,status:200,value:{runId:process.env.TERRASCALE_JOURNEY_RUN_ID,sdk:process.env.WRONG_PEER ? "wrong" : id.slice(process.env.TERRASCALE_JOURNEY_RUN_ID.length+1)}})}}}`);
    const file = join(root, "journey.ts");
    await writeFile(file, journeySource("verify"));
    const env = { ...process.env, TERRASCALE_API_URL: "https://api.test", TERRASCALE_PROJECT_ID: "p", TERRASCALE_DATABASE_ID: "d", TERRASCALE_API_KEY: "authorized", TERRASCALE_DENY_API_KEY: "denied", TERRASCALE_JOURNEY_RUN_ID: "run" };
    const run = promisify(execFile);
    const result = await run(process.execPath, ["--experimental-strip-types", file], { cwd: root, env });
    assert.equal(result.stdout.trim().split("\n").length, 5);
    await assert.rejects(run(process.execPath, ["--experimental-strip-types", file], { cwd: root, env: { ...env, WRONG_PEER: "1" } }), /Command failed/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
