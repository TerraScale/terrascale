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
  assert.deepEqual(
    deployment.releaseProfiles.find((profile) => profile.name === "release-e2e")?.commands.map((command) => command.description),
    ["Clean install", "Build SDK", "Require live integration configuration", "Authenticated live API integration"],
  );
  assert.doesNotMatch(workflow, /^  (?:REGISTRY_TOKEN|FORGEJO_TOKEN|NPM_TOKEN):/mu);
  const actionReferences = [...workflow.matchAll(/^\s*- uses: \S+@(\S+)$/gmu)];
  assert.ok(actionReferences.length > 0);
  for (const reference of actionReferences) {
    assert.match(reference[1] ?? "", /^[a-f0-9]{40}$/u);
  }
  for (const action of [
    "checkout@d23441a48e516b6c34aea4fa41551a30e30af803",
    "download-artifact@9bc31d5ccc31df68ecc42ccf4149144866c47d8a",
    "upload-artifact@ff15f0306b3f739f7b6fd43fb5d26cd321bd4de5",
  ]) {
    assert.match(workflow, new RegExp(`uses: https://data\\.forgejo\\.org/actions/${action}`, "u"));
  }
  for (const job of ["bootstrap", "plan", "release-tests", "release-e2e", "release-artifacts", "release-contract-fixture", "release-version-availability", "candidate", "promote"]) {
    assert.match(jobDefinition(workflow, job), /forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main'/u);
  }
  // 0addfcf ("ci: pull the toolbox image from the TerraScale container
  // registry") switched the bootstrap credential source to the org registry
  // package token; the env-var name it feeds stayed DEPLOY_CLI_READ_TOKEN.
  assert.match(jobDefinition(workflow, "bootstrap"), /secrets\.REGISTRY_TOKEN/u);
  assert.doesNotMatch(jobDefinition(workflow, "bootstrap"), /^    env:/mu);
  for (const job of ["pr-static", "pr-build"]) {
    assert.match(jobDefinition(workflow, job), /forgejo\.event_name == 'pull_request'/u);
    assert.doesNotMatch(jobDefinition(workflow, job), /forgejo\.base_ref/u);
    assert.doesNotMatch(jobSteps(workflow, job), /secrets\.[A-Z0-9_]+/u);
    assert.doesNotMatch(jobDefinition(workflow, job), /(?:CANDIDATE_STORE_TOKEN|DEPLOY_CLI_READ_TOKEN|NPM_TOKEN|candidate-store|upload-artifact)/u);
  }
  for (const job of ["pr-static", "pr-build"]) {
    assert.doesNotMatch(jobDefinition(workflow, job), /(?:needs:|download-artifact|terrascale-deploy)/u);
    assert.doesNotMatch(jobSteps(workflow, job), /secrets\.[A-Z0-9_]+/u);
    assert.equal((jobDefinition(workflow, job).match(/persist-credentials: false/gu) ?? []).length, 1);
  }
  assert.match(jobDefinition(workflow, "promotion-admission"), /name: promotion\/admission/u);
  assert.match(jobDefinition(workflow, "promotion-admission"), /forgejo\.event_name == 'pull_request' && forgejo\.base_ref == 'main'/u);
  assert.match(jobDefinition(workflow, "promotion-admission"), /test "\$\{\{ forgejo\.base_ref \}\}" = main/u);
  assert.match(jobDefinition(workflow, "promotion-admission"), /test "\$\{\{ forgejo\.head_ref \}\}" = develop/u);
  assert.match(jobDefinition(workflow, "promotion-admission"), /test "\$\{\{ forgejo\.event\.pull_request\.head\.repo\.full_name \}\}" = "\$\{\{ forgejo\.repository \}\}"/u);
  assert.doesNotMatch(jobSteps(workflow, "promotion-admission"), /secrets\./u);
  assert.doesNotMatch(jobDefinition(workflow, "promotion-admission"), /(?:candidate-store|upload-artifact|download-artifact|terrascale-deploy)/u);
  assert.match(jobDefinition(workflow, "pr-static"), /git -c credential\.helper= clone --depth 1 --branch develop https:\/\/git\.terrascale\.tech\/TerraScale\/TerraScale\.Documentation\.git Documentation/u);
  assert.match(jobDefinition(workflow, "pr-static"), /npm ci/u);
  assert.match(jobDefinition(workflow, "pr-static"), /npm run typecheck/u);
  assert.match(jobDefinition(workflow, "pr-static"), /npm run build/u);
  assert.ok(jobDefinition(workflow, "pr-static").indexOf("npm run build") < jobDefinition(workflow, "pr-static").indexOf("npm run check:route-manifest"));
  assert.match(jobDefinition(workflow, "pr-build"), /npm ci/u);
  assert.match(jobDefinition(workflow, "pr-build"), /npm run build/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /node: \["22", "24"\]/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /runs-on: terrascale-untrusted/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /npm run test:unit/u);
  assert.match(jobDefinition(workflow, "browser-matrix"), /runs-on: terrascale-untrusted/u);
  assert.match(jobDefinition(workflow, "browser-matrix"), /playwright:v1\.62\.1-noble/u);
  assert.match(jobDefinition(workflow, "browser-matrix"), /npm run test:browser/u);
  assert.match(jobDefinition(workflow, "release-contract-fixture"), /check-public-contract-fixture\.mjs Documentation\/public\/openapi\/server\.openapi\.json/u);
  assert.match(jobDefinition(workflow, "release-contract-fixture"), /check:openapi-contract-descriptor -- Documentation\/public\/openapi\/server\.openapi\.json/u);
  assert.ok(jobDefinition(workflow, "release-contract-fixture").indexOf("npm run build") < jobDefinition(workflow, "release-contract-fixture").indexOf("npm run check:route-manifest"));
  assert.ok(jobDefinition(workflow, "release-contract-fixture").indexOf("npm run check:route-manifest") < jobDefinition(workflow, "release-contract-fixture").indexOf("check:openapi-contract-descriptor"));
  assert.ok(jobDefinition(workflow, "release-contract-fixture").indexOf("check:openapi-contract-descriptor") < jobDefinition(workflow, "release-contract-fixture").indexOf("check:openapi-drift -- --strict"));
  assert.ok(jobDefinition(workflow, "pr-static").indexOf("check:openapi-contract-descriptor") < jobDefinition(workflow, "pr-static").indexOf("check:openapi-drift -- --strict"));
  assert.match(jobDefinition(workflow, "release-version-availability"), /--check-availability --package @terrascale\/sdk/u);
  assert.match(jobDefinition(workflow, "release-e2e"), /secrets\.TERRASCALE_E2E_ACCESS_TOKEN/u);
  assert.doesNotMatch(jobDefinition(workflow, "release-e2e"), /^    env:/mu);
  assert.match(jobDefinition(workflow, "release-e2e"), /--profile release-e2e/u);
  assert.match(jobDefinition(workflow, "release-tests"), /--profile regression/u);
  for (const name of [
    "TERRASCALE_API_BASE_URL",
    "TERRASCALE_AUTH_API_BASE_URL",
    "TERRASCALE_E2E_PROJECT_ID",
    "TERRASCALE_E2E_ORGANIZATION_ID",
    "TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID",
  ]) {
    assert.match(jobDefinition(workflow, "release-e2e"), new RegExp(`${name}: \\$\\{\\{ vars\\.${name} \\}\\}`, "u"));
  }
  for (const job of ["pr-static", "pr-build"]) {
    assert.doesNotMatch(jobDefinition(workflow, job), /TERRASCALE_E2E_ACCESS_TOKEN/u);
  }
  assert.match(jobDefinition(workflow, "candidate"), /release-contract-fixture/u);
  assert.match(jobDefinition(workflow, "candidate"), /release-e2e/u);
  assert.match(jobDefinition(workflow, "candidate"), /release-version-availability/u);
  assert.match(jobDefinition(workflow, "candidate"), /runtime-matrix/u);
  assert.match(jobDefinition(workflow, "candidate"), /browser-matrix/u);
  assert.doesNotMatch(jobDefinition(workflow, "candidate"), /NPM_TOKEN/u);
  assert.match(jobDefinition(workflow, "candidate"), /candidate-store upload/u);
  assert.doesNotMatch(jobDefinition(workflow, "candidate"), /^    env:/mu);
  assert.match(jobDefinition(workflow, "promote"), /promote-npm-candidate\.mjs/u);
  assert.match(jobDefinition(workflow, "promote"), /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.doesNotMatch(jobDefinition(workflow, "promote"), /^    env:/mu);
  assert.match(jobDefinition(workflow, "promote"), /needs: \[candidate\]/u);
  assert.match(jobDefinition(workflow, "promote"), /candidate-store download/u);
  // 0addfcf: the bootstrap env var DEPLOY_CLI_READ_TOKEN is now fed by the
  // org registry token instead of a dedicated secret.
  assertCredentialStepIsFinal(workflow, "bootstrap", "REGISTRY_TOKEN");
  assertCredentialStepIsFinal(workflow, "release-e2e", "TERRASCALE_E2E_ACCESS_TOKEN");
  assertCredentialStepIsFinal(workflow, "candidate", "CANDIDATE_STORE_TOKEN");
  assertCredentialStepIsFinal(workflow, "promote", "CANDIDATE_STORE_TOKEN");
  assertCredentialStepIsFinal(workflow, "promote", "NPM_TOKEN");
});

test("release E2E configuration guard fails closed before skip-capable tests", async () => {
  const guard = path.join(repositoryRoot, "scripts", "require-release-e2e-config.mjs");
  const environment = {
    PATH: process.env.PATH ?? "",
    TERRASCALE_API_BASE_URL: "https://api.example.test",
    TERRASCALE_AUTH_API_BASE_URL: "https://auth.example.test",
    TERRASCALE_E2E_ACCESS_TOKEN: "test-token",
    TERRASCALE_E2E_PROJECT_ID: "01J00000000000000000000000",
    TERRASCALE_E2E_ORGANIZATION_ID: "01J00000000000000000000001",
    TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID: "01J00000000000000000000002",
  };

  await assert.rejects(run("node", [guard], { env: { PATH: environment.PATH } }), /Release E2E configuration is missing/u);
  await assert.rejects(
    run("node", [guard], { env: { ...environment, TERRASCALE_API_BASE_URL: "not-a-url", TERRASCALE_E2E_PROJECT_ID: "not-a-ulid" } }),
    /Release E2E configuration is invalid: TERRASCALE_API_BASE_URL, TERRASCALE_E2E_PROJECT_ID/u,
  );
  await run("node", [guard], { env: environment });
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
    await run("npm", ["run", "check:route-manifest"], { cwd: checkout });
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
    const journey = path.join(consumer, "packed-journey.mjs");
    await writeFile(journey, packedConsumerJourney);
    await run("node", [journey], { cwd: consumer });
    await run(
      "node",
      [
        "--input-type=module",
        "--eval",
        "import { createTerraScaleClient } from '@terrascale/sdk'; import { createTerraScaleAdminClient } from '@terrascale/sdk/admin'; createTerraScaleClient({ apiBaseUrl: 'https://api.example.test' }); createTerraScaleAdminClient({ apiBaseUrl: 'https://api.example.test' });",
      ],
      { cwd: consumer },
    );
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

const packedConsumerJourney = String.raw`import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { open, unlink } from "node:fs/promises";
import { hostname } from "node:os";
import path from "node:path";
import { createTerraScaleClient, toUlid } from "@terrascale/sdk";

const required = [
  "TERRASCALE_API_BASE_URL",
  "TERRASCALE_AUTH_API_BASE_URL",
  "TERRASCALE_E2E_ACCESS_TOKEN",
  "TERRASCALE_E2E_TENANT_ID",
  "TERRASCALE_E2E_PROJECT_ID",
  "TERRASCALE_E2E_DATABASE_ID",
  "TERRASCALE_E2E_REPOSITORY_ID",
  "TERRASCALE_E2E_DOCUMENT_ID",
  "TERRASCALE_E2E_DOCUMENT_WRITE_LEASE",
];
const missing = required.filter(name => (process.env[name] ?? "").trim().length === 0);
if (missing.length > 0) {
  console.log("Packed live journey skipped: " + missing.join(", ") + " is not configured.");
  process.exit(0);
}

const tenantId = toUlid(process.env.TERRASCALE_E2E_TENANT_ID);
const projectId = toUlid(process.env.TERRASCALE_E2E_PROJECT_ID);
const databaseId = toUlid(process.env.TERRASCALE_E2E_DATABASE_ID);
const repositoryId = toUlid(process.env.TERRASCALE_E2E_REPOSITORY_ID);
const documentId = toUlid(process.env.TERRASCALE_E2E_DOCUMENT_ID);
const leasePath = process.env.TERRASCALE_E2E_DOCUMENT_WRITE_LEASE.trim();
if (!path.isAbsolute(leasePath) || path.dirname(leasePath) === leasePath) {
  throw new TypeError("TERRASCALE_E2E_DOCUMENT_WRITE_LEASE must be an absolute file path.");
}
const client = createTerraScaleClient({
  apiBaseUrl: process.env.TERRASCALE_API_BASE_URL,
  authBaseUrl: process.env.TERRASCALE_AUTH_API_BASE_URL,
  accessToken: process.env.TERRASCALE_E2E_ACCESS_TOKEN,
  projectId,
  sessionConsistency: "automatic",
});
const scope = { repositoryId };
const leaseScope = {
  tenantId,
  projectId,
  databaseId,
  repositoryId,
  documentId,
  apiBaseUrl: process.env.TERRASCALE_API_BASE_URL,
  authBaseUrl: process.env.TERRASCALE_AUTH_API_BASE_URL,
  credentialFingerprint: createHash("sha256").update(process.env.TERRASCALE_E2E_ACCESS_TOKEN).digest("hex"),
};

let lease;
try {
  lease = await open(leasePath, "wx");
} catch (error) {
  if (error instanceof Error && "code" in error && error.code === "EEXIST") {
    throw new Error("TERRASCALE_E2E_DOCUMENT_WRITE_LEASE is already held by another process.");
  }
  throw error;
}

try {
  await lease.writeFile(JSON.stringify({
    version: 1,
    owner: hostname() + ":" + process.pid + ":" + randomUUID(),
    acquiredAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
    scope: leaseScope,
  }));
  await lease.sync();

  const before = await client.documents.get(scope, documentId);
  assert.equal(before.ok, true, before.ok ? undefined : before.error.message);
  assert.notEqual(before.value.changeToken, null);
  assert.notEqual(before.value.changeToken, undefined);

  const write = await client.documents.put(
    scope,
    documentId,
    before.value.data,
    { expectedChangeToken: before.value.changeToken },
  );
  assert.equal(write.ok, true, write.ok ? undefined : write.error.message);

  const after = await client.documents.get(scope, documentId);
  assert.equal(after.ok, true, after.ok ? undefined : after.error.message);
  console.log("Packed live journey completed for /api/v1 document read/write.");
} finally {
  await lease.close();
  await unlink(leasePath).catch(error => {
    if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) throw error;
  });
}
`;

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
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}

/**
 * A job definition with its `container:` block removed: since 0addfcf every
 * container job pulls the org-registry toolbox image with
 * secrets.REGISTRY_USER/REGISTRY_TOKEN, so the no-secrets guards below hold
 * over the job's own steps, not the runner-level image pull.
 */
function jobSteps(workflow: string, job: string): string {
  return jobDefinition(workflow, job).replace(/\n    container:\n(?:[ ]{6,}.*\n?)+/u, "");
}

function assertCredentialStepIsFinal(workflow: string, job: string, credential: string): void {
  // Runs over jobSteps: since 0addfcf the container image pull also
  // references secrets.REGISTRY_USER/REGISTRY_TOKEN ahead of every step,
  // which is runner infrastructure, not a job credential step.
  const steps = jobSteps(workflow, job);
  assert.doesNotMatch(steps, /^    env:/mu, `${job} must not set job-level environment variables.`);

  const credentialIndex = steps.indexOf(`secrets.${credential}`);
  assert.notEqual(credentialIndex, -1, `${job} must use ${credential}.`);
  const stepStart = steps.lastIndexOf("\n      - ", credentialIndex);
  assert.notEqual(stepStart, -1, `${credential} must be scoped to a shell step.`);
  const credentialStep = steps.slice(stepStart + 1);
  assert.match(credentialStep, /^      - (?:(?:env:)|[\s\S]*^        env:)[\s\S]*^        run:/mu, `${credential} must be scoped to a run step.`);
  const runIndex = credentialStep.indexOf("\n        run:");
  assert.notEqual(runIndex, -1);
  assert.doesNotMatch(credentialStep.slice(runIndex + 1), /^      - run:/mu, `${credential} must be on the final shell step.`);
  assert.doesNotMatch(steps.slice(0, stepStart), /secrets\./u, `${credential} must not be present before its scoped step.`);
}

async function sha256(filePath: string): Promise<string> {
  return createHash("sha256").update(await readFile(filePath)).digest("hex");
}

type DeployCommand = {
  readonly description: string;
};

type DeployConfiguration = {
  readonly releaseProfiles: readonly { readonly name: string; readonly commands: readonly DeployCommand[] }[];
  readonly buildTargets: readonly { readonly commands: readonly DeployCommand[] }[];
};
