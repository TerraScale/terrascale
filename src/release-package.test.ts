import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdtemp, mkdir, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "node:test";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);

test("release paths clean-install, build, test, and pack the SDK independently", async () => {
  const deployment = JSON.parse(
    await readFile(path.join(repositoryRoot, ".terrascale", "deploy.json"), "utf8"),
  ) as DeployConfiguration;

  for (const commands of [
    ...deployment.releaseProfiles.map((profile) => profile.commands),
    ...deployment.buildTargets.map((target) => target.commands),
  ]) {
    assert.deepEqual(commands.slice(0, 2).map((command) => command.description), [
      "Clean install",
      "Build SDK",
    ]);
  }
  assert.deepEqual(
    deployment.buildTargets[0]?.commands.map((command) => command.description),
    ["Clean install", "Build SDK", "Unit and contract regression", "Pack npm candidate"],
  );
});

test("a clean SDK-only checkout runs fixture tests and installs a usable npm pack", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-release-package-"));
  const checkout = path.join(workspace, "sdk");
  const packages = path.join(workspace, "packages");
  const consumer = path.join(workspace, "consumer");

  try {
    await cp(repositoryRoot, checkout, {
      recursive: true,
      filter: (source) => ![".git", "node_modules", "dist"].includes(path.basename(source)),
    });
    await mkdir(packages);
    await mkdir(consumer);
    await run("npm", ["ci", "--ignore-scripts", "--no-audit", "--no-fund"], { cwd: checkout });
    await run("npm", ["run", "build"], { cwd: checkout });
    await run("node", ["--test", "dist/public-schema-parity.test.js"], { cwd: checkout });
    const packed = await run("npm", ["pack", "--pack-destination", packages], { cwd: checkout });
    const tarball = path.join(packages, packed.stdout.trim().split("\n").at(-1)!);
    const listing = await run("tar", ["-tf", tarball]);

    assert.match(listing.stdout, /^package\/dist\/index\.js$/mu);
    assert.match(listing.stdout, /^package\/dist\/index\.d\.ts$/mu);
    await run("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball], { cwd: consumer });
    await run(
      "node",
      ["--input-type=module", "--eval", "import { createTerraScaleClient } from '@terrascale/sdk'; createTerraScaleClient({ apiBaseUrl: 'https://api.example.test' });"],
      { cwd: consumer },
    );
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

type DeployCommand = {
  readonly description: string;
};

type DeployConfiguration = {
  readonly releaseProfiles: readonly { readonly commands: readonly DeployCommand[] }[];
  readonly buildTargets: readonly { readonly commands: readonly DeployCommand[] }[];
};
