import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { cp, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "node:test";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);

test("release paths clean-install, build, test, and pack the SDK independently", async () => {
  const [deploymentJson, workflow] = await Promise.all([
    readFile(path.join(repositoryRoot, ".terrascale", "deploy.json"), "utf8"),
    readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8"),
  ]);
  const deployment = JSON.parse(deploymentJson) as DeployConfiguration;

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
  assert.doesNotMatch(workflow, /^  (?:REGISTRY_TOKEN|FORGEJO_TOKEN|NPM_TOKEN):/mu);
  for (const job of ["bootstrap", "pr-policy", "release-policy", "plan", "pr-static", "pr-build", "release-tests", "release-artifacts", "release-contract-fixture", "candidate"]) {
    assert.doesNotMatch(jobDefinition(workflow, job), /secrets\.(?:REGISTRY_TOKEN|NPM_TOKEN|PUBLISH)/u);
  }
  assert.doesNotMatch(jobDefinition(workflow, "pr-static"), /\n\s+token:/u);
  assert.match(jobDefinition(workflow, "release-contract-fixture"), /check-public-contract-fixture\.mjs Documentation\/public\/openapi\/server\.openapi\.json/u);
  assert.match(jobDefinition(workflow, "candidate"), /release-contract-fixture/u);
  assert.doesNotMatch(jobDefinition(workflow, "candidate"), /NPM_TOKEN/u);
  assert.match(jobDefinition(workflow, "promote"), /promote-npm-candidate\.mjs/u);
  assert.match(jobDefinition(workflow, "promote"), /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
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
    assert.doesNotMatch(
      listing.stdout,
      /^package\/(?:Documentation\/|node_modules\/|test\/|dist\/.*(?:\.map$|\.test\.|capture|internal))/mu,
    );
    const packageJson = JSON.parse(await readFile(path.join(checkout, "package.json"), "utf8")) as {
      readonly name: string;
      readonly version: string;
    };
    const manifestPath = path.join(workspace, "release-candidate.json");
    await writeFile(manifestPath, JSON.stringify({
      reservedVersion: packageJson.version,
      artifacts: [{ name: path.basename(tarball), sha256: await sha256(tarball) }],
    }));
    await run(
      "node",
      [
        path.join(checkout, "scripts", "promote-npm-candidate.mjs"),
        "--manifest", manifestPath,
        "--artifacts", packages,
        "--registry", "https://registry.npmjs.org/",
        "--verify-only",
      ],
      { cwd: checkout },
    );
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

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}

async function sha256(filePath: string): Promise<string> {
  return createHash("sha256").update(await readFile(filePath)).digest("hex");
}

type DeployCommand = {
  readonly description: string;
};

type DeployConfiguration = {
  readonly releaseProfiles: readonly { readonly commands: readonly DeployCommand[] }[];
  readonly buildTargets: readonly { readonly commands: readonly DeployCommand[] }[];
};
