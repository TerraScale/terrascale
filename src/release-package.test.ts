import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { chmod, cp, mkdtemp, mkdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
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
  assert.match(jobDefinition(workflow, "release-version-availability"), /--check-availability --package @terrascale\/sdk/u);
  assert.doesNotMatch(jobDefinition(workflow, "release-version-availability"), /secrets\.[A-Z0-9_]+/u);
  assert.doesNotMatch(jobDefinition(workflow, "release-version-availability"), /CANDIDATE_STORE_TOKEN/u);
  assert.match(jobDefinition(workflow, "candidate"), /release-contract-fixture/u);
  assert.match(jobDefinition(workflow, "candidate"), /release-version-availability/u);
  assert.doesNotMatch(jobDefinition(workflow, "candidate"), /NPM_TOKEN/u);
  assert.match(jobDefinition(workflow, "candidate"), /forgejo\.event\.pull_request\.head\.repo\.full_name == forgejo\.repository/u);
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
      artifacts: [{ name: path.basename(tarball), sha256: `sha256:${await sha256(tarball)}` }],
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
    await assert.rejects(
      verifyCandidate(checkout, manifestPath, packages, { ...JSON.parse(await readFile(manifestPath, "utf8")), artifacts: [{ name: `../${path.basename(tarball)}`, sha256: `sha256:${await sha256(tarball)}` }] }),
    );
    const symlinkedTarball = path.join(packages, "linked-candidate.tgz");
    await symlink(tarball, symlinkedTarball);
    await assert.rejects(
      verifyCandidate(checkout, manifestPath, packages, { ...JSON.parse(await readFile(manifestPath, "utf8")), artifacts: [{ name: path.basename(symlinkedTarball), sha256: `sha256:${await sha256(tarball)}` }] }),
    );
    await verifyLifecycleSafePublish(checkout, workspace, packages, packageJson);
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

async function verifyCandidate(
  checkout: string,
  manifestPath: string,
  packages: string,
  manifest: unknown,
): Promise<void> {
  await writeFile(manifestPath, JSON.stringify(manifest));
  await run(
    "node",
    [path.join(checkout, "scripts", "promote-npm-candidate.mjs"), "--manifest", manifestPath, "--artifacts", packages, "--registry", "https://registry.npmjs.org/", "--verify-only"],
    { cwd: checkout },
  );
}

async function verifyLifecycleSafePublish(
  checkout: string,
  workspace: string,
  packages: string,
  packageJson: { readonly name: string; readonly version: string },
): Promise<void> {
  const lifecycleRoot = path.join(workspace, "lifecycle");
  const lifecyclePackage = path.join(lifecycleRoot, "package");
  const lifecycleMarker = path.join(workspace, "lifecycle-ran");
  const lifecycleTarball = path.join(packages, "lifecycle-candidate.tgz");
  const manifestPath = path.join(workspace, "lifecycle-candidate.json");
  const fakeBin = path.join(workspace, "fake-bin");
  const fakeNpm = path.join(fakeBin, "npm");
  const publishRecord = path.join(workspace, "publish-record.json");
  const availabilityRecord = path.join(workspace, "availability-record.json");
  const projectConfig = path.join(checkout, ".npmrc");
  const userConfig = path.join(workspace, "user.npmrc");
  const globalConfig = path.join(workspace, "global-npmrc");

  await mkdir(lifecyclePackage, { recursive: true });
  await mkdir(fakeBin);
  await Promise.all([
    writeFile(projectConfig, "//registry.npmjs.org/:_authToken=project-token\\n"),
    writeFile(userConfig, "//registry.npmjs.org/:_authToken=user-token\\n"),
    writeFile(globalConfig, "//registry.npmjs.org/:_authToken=global-token\\n"),
  ]);
  await writeFile(path.join(lifecyclePackage, "package.json"), JSON.stringify({
    name: packageJson.name,
    version: packageJson.version,
    scripts: { prepublishOnly: `node -e \"require('node:fs').writeFileSync('${lifecycleMarker}', 'ran')\"` },
  }));
  await run("tar", ["-czf", lifecycleTarball, "-C", lifecycleRoot, "package"]);
  await writeFile(manifestPath, JSON.stringify({
    reservedVersion: packageJson.version,
    artifacts: [{ name: path.basename(lifecycleTarball), sha256: `sha256:${await sha256(lifecycleTarball)}` }],
  }));
  await writeFile(fakeNpm, `#!/usr/bin/env node
const { existsSync, readFileSync, writeFileSync } = require('node:fs');
const [command, ...arguments_] = process.argv.slice(2);
if (command === 'view') {
  writeFileSync(${JSON.stringify(availabilityRecord)}, JSON.stringify({
    arguments_, cwd: process.cwd(), token: process.env.NPM_TOKEN,
    userConfig: process.env.NPM_CONFIG_USERCONFIG,
    userConfigHasAuth: /_authToken=/.test(readFileSync(process.env.NPM_CONFIG_USERCONFIG, 'utf8')),
    globalConfig: process.env.NPM_CONFIG_GLOBALCONFIG,
    globalConfigHasAuth: /_authToken=/.test(readFileSync(process.env.NPM_CONFIG_GLOBALCONFIG, 'utf8')),
  }));
  if (existsSync(${JSON.stringify(path.join(workspace, "npm-view-exists"))})) { process.stdout.write('"already-exists"\\n'); process.exit(0); }
  process.stderr.write('npm error code E404\\n'); process.exit(1);
}
if (command !== 'publish') process.exit(2);
writeFileSync(${JSON.stringify(publishRecord)}, JSON.stringify({
  arguments_, cwd: process.cwd(), token: process.env.NPM_TOKEN,
  ignoreScripts: process.env.NPM_CONFIG_IGNORE_SCRIPTS,
  configHasAuth: /_authToken=/.test(readFileSync(process.env.NPM_CONFIG_USERCONFIG, 'utf8')),
  globalConfig: process.env.NPM_CONFIG_GLOBALCONFIG,
  globalConfigHasAuth: /_authToken=/.test(readFileSync(process.env.NPM_CONFIG_GLOBALCONFIG, 'utf8')),
}));
`);
  await chmod(fakeNpm, 0o755);
  await run(
    "node",
    [path.join(checkout, "scripts", "promote-npm-candidate.mjs"), "--manifest", manifestPath, "--artifacts", packages, "--registry", "https://registry.npmjs.org/"],
    {
      cwd: checkout,
      env: {
        ...process.env,
        PATH: `${fakeBin}${path.delimiter}${process.env.PATH ?? ""}`,
        NPM_TOKEN: "test-token",
        NPM_CONFIG_USERCONFIG: userConfig,
        NPM_CONFIG_GLOBALCONFIG: globalConfig,
      },
    },
  );
  const availability = JSON.parse(await readFile(availabilityRecord, "utf8")) as {
    readonly arguments_: readonly string[];
    readonly cwd: string;
    readonly token?: string;
    readonly userConfig: string;
    readonly userConfigHasAuth: boolean;
    readonly globalConfig: string;
    readonly globalConfigHasAuth: boolean;
  };
  assert.equal(availability.token, undefined);
  assert.notEqual(availability.cwd, checkout);
  assert.notEqual(availability.userConfig, userConfig);
  assert.equal(availability.userConfigHasAuth, false);
  assert.notEqual(availability.globalConfig, globalConfig);
  assert.equal(availability.globalConfigHasAuth, false);
  assert.equal(availability.arguments_.includes("--ignore-scripts"), true);
  const record = JSON.parse(await readFile(publishRecord, "utf8")) as {
    readonly arguments_: readonly string[];
    readonly cwd: string;
    readonly token?: string;
    readonly ignoreScripts?: string;
    readonly configHasAuth: boolean;
    readonly globalConfig: string;
    readonly globalConfigHasAuth: boolean;
  };
  assert.equal(record.token, undefined);
  assert.notEqual(record.cwd, checkout);
  assert.equal(record.ignoreScripts, "true");
  assert.equal(record.configHasAuth, true);
  assert.notEqual(record.globalConfig, globalConfig);
  assert.equal(record.globalConfigHasAuth, false);
  assert.equal(record.arguments_.includes("--ignore-scripts"), true);
  await assert.rejects(readFile(lifecycleMarker));

  await rm(publishRecord);
  await writeFile(path.join(workspace, "npm-view-exists"), "");
  await assert.rejects(
    run(
      "node",
      [path.join(checkout, "scripts", "promote-npm-candidate.mjs"), "--manifest", manifestPath, "--artifacts", packages, "--registry", "https://registry.npmjs.org/"],
      {
        cwd: checkout,
        env: {
          ...process.env,
          PATH: `${fakeBin}${path.delimiter}${process.env.PATH ?? ""}`,
          NPM_TOKEN: "test-token",
          NPM_CONFIG_USERCONFIG: userConfig,
          NPM_CONFIG_GLOBALCONFIG: globalConfig,
        },
      },
    ),
    /already exists/u,
  );
  await assert.rejects(readFile(publishRecord));
}

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
