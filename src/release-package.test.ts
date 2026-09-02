import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";

import { localSqliteTestChildEnvironment } from "./local/test-environment.js";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);
const forbiddenRoutePattern = /(?:\/api\/v\d+\/(?:auth|account|billing|pricing|onboarding\/payment)(?:[\/"'`: ]|$)|\/api\/v\d+\/projects\/\{[^}]+\}\/(?:auth|security)(?:[\/"'`: ]|$)|\b(?:auth|apiKeys|m2m|billing|pricing|payment|security)\.[a-z])/iu;
const forbiddenSymbolPattern = /\b(?:AuthClient|ProjectAuth\w*|authBaseUrl|ApiKey\w*|M2M\w*|OnboardingPayment\w*|CreateOnboardingPayment\w*|ValidateOnboardingPayment\w*|RegisterOnboardingPayment\w*|PaymentMethod|Billing\w*|Pricing\w*|Security(?:Client|Settings\w*))\b/u;

type PackedRouteManifest = {
  readonly routes?: readonly {
    readonly client?: string;
    readonly operation?: string;
    readonly routeName?: string;
    readonly pathTemplate?: string;
  }[];
};

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
    ["Clean install", "Build SDK", "Unit and contract regression", "Qualify npm release candidate"],
  );
  assert.equal(deployment.buildTargets[0]?.outputPath, "artifacts/candidate");
  assert.deepEqual(deployment.buildTargets[0]?.commands.at(-1), {
    description: "Qualify npm release candidate",
    command: "node",
    arguments: ["scripts/qualify-npm-artifact.mjs", "--release", "--output", "artifacts/candidate"],
  });
  assert.doesNotMatch(deploymentJson, /"command": "npm", "arguments": \["pack"/u);
  assert.equal(deployment.releaseProfiles.every((profile) => profile.required), true);
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
    "setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020",
  ]) {
    assert.match(workflow, new RegExp(`uses: https://data\\.forgejo\\.org/actions/${action}`, "u"));
  }
  for (const job of ["bootstrap", "plan", "release-tests", "release-e2e", "release-artifacts", "release-browser-candidate", "release-contract-fixture", "release-version-availability", "candidate", "promote"]) {
    assert.match(jobDefinition(workflow, job), /forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main'/u);
  }
  assert.match(jobDefinition(workflow, "bootstrap"), /secrets\.REGISTRY_TOKEN/u);
  assert.match(jobDefinition(workflow, "bootstrap"), /--version 1\.0\.80/u);
  assert.doesNotMatch(jobDefinition(workflow, "bootstrap"), /newest stable/u);
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
  assert.match(jobDefinition(workflow, "pr-static"), /git -c credential\.helper= clone --depth 1 --branch develop https:\/\/git\.terrascale\.tech\/TerraScale\/ts-dashboard\.git Dashboard/u);
  assert.match(jobDefinition(workflow, "pr-static"), /npm ci/u);
  assert.match(jobDefinition(workflow, "pr-static"), /npm run typecheck/u);
  assert.match(jobDefinition(workflow, "pr-static"), /npm run build/u);
  assert.ok(jobDefinition(workflow, "pr-static").indexOf("npm run build") < jobDefinition(workflow, "pr-static").indexOf("npm test"));
  assert.match(jobDefinition(workflow, "pr-build"), /npm ci/u);
  assert.match(jobDefinition(workflow, "pr-build"), /npm run build/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /node: \["22", "24"\]/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /runs-on: terrascale-untrusted/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /node scripts\/provision-reviewed-npm\.mjs/u);
  assert.match(jobDefinition(workflow, "runtime-matrix"), /npm test/u);
  assert.match(jobDefinition(workflow, "browser-matrix"), /runs-on: terrascale-untrusted/u);
  assert.match(jobDefinition(workflow, "browser-matrix"), /playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
  assert.match(jobDefinition(workflow, "browser-matrix"), /npm run test:browser/u);
  for (const job of ["pr-static", "release-contract-fixture"]) {
    assert.match(jobDefinition(workflow, job), /npm test/u);
  }
  assert.match(jobDefinition(workflow, "release-version-availability"), /--check-availability --package @terrascale\/sdk/u);
  assert.match(jobDefinition(workflow, "release-e2e"), /secrets\.TERRASCALE_E2E_ACCESS_TOKEN/u);
  assert.doesNotMatch(jobDefinition(workflow, "release-e2e"), /^    env:/mu);
  assert.match(jobDefinition(workflow, "release-e2e"), /needs: \[release-artifacts\]/u);
  assert.match(jobDefinition(workflow, "release-e2e"), /require-release-e2e-config\.mjs/u);
  assert.match(jobDefinition(workflow, "release-e2e"), /run-packed-release-e2e\.mjs --tarball/u);
  assert.match(jobDefinition(workflow, "release-e2e"), /npm test/u);
  assert.match(jobDefinition(workflow, "release-e2e"), /--build-receipt artifacts\/handoff\/evidence\/npm\.build-receipt\.json/u);
  assert.ok(jobDefinition(workflow, "release-e2e").indexOf("--verify-only") < jobDefinition(workflow, "release-e2e").indexOf("TERRASCALE_E2E_ACCESS_TOKEN"));
  assert.match(jobDefinition(workflow, "release-tests"), /npm test/u);
  assert.doesNotMatch(jobDefinition(workflow, "release-tests"), /terrascale-deploy ci release-test/u);
  assert.doesNotMatch(jobDefinition(workflow, "release-e2e"), /terrascale-deploy ci release-test/u);
  const releaseArtifacts = jobDefinition(workflow, "release-artifacts");
  assert.match(releaseArtifacts, /node-version: "22\.23\.2"/u);
  assert.match(releaseArtifacts, /node-version: "24\.20\.0"/u);
  assert.equal((releaseArtifacts.match(/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020/gu) ?? []).length, 2);
  assert.match(releaseArtifacts, /--expected-tree "\$\{EXPECTED_TREE\}"/u);
  assert.match(releaseArtifacts, /--receipt "artifacts\/evidence\/\$\{BUILD_TARGET\}\.build-receipt\.json"/u);
  assert.match(releaseArtifacts, /path: artifacts/u);
  assert.match(releaseArtifacts, /node scripts\/provision-reviewed-npm\.mjs/u);
  assert.ok(releaseArtifacts.indexOf("node-version: \"24.20.0\"") < releaseArtifacts.indexOf("node scripts/provision-reviewed-npm.mjs"));
  assert.ok(releaseArtifacts.indexOf("node scripts/provision-reviewed-npm.mjs") < releaseArtifacts.indexOf("ci build-candidate"));
  const releaseBrowser = jobDefinition(workflow, "release-browser-candidate");
  assert.match(releaseBrowser, /needs: \[release-artifacts\]/u);
  assert.match(releaseBrowser, /playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
  assert.match(releaseBrowser, /--build-receipt artifacts\/handoff\/evidence\/npm\.build-receipt\.json/u);
  assert.match(releaseBrowser, /TERRASCALE_BROWSER_CANDIDATE_TARBALL/u);
  assert.match(releaseBrowser, /terrascale-sdk-\$\(node -p 'require\("\.\/package\.json"\)\.version'\)\.tgz/u);
  assert.doesNotMatch(releaseBrowser, /terrascale-sdk-\d+\.\d+\.\d+[^\s"']*\.tgz/u);
  assert.doesNotMatch(releaseBrowser, /npm run build|npm pack/u);
  for (const job of ["release-e2e", "release-browser-candidate", "release-version-availability", "candidate", "promote"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /node-version: "24\.20\.0"/u);
    assert.match(definition, /node scripts\/provision-reviewed-npm\.mjs/u);
    assert.ok(
      definition.indexOf("node scripts/provision-reviewed-npm.mjs") < definition.indexOf("node scripts/promote-npm-candidate.mjs"),
      `${job} must provision reviewed npm before invoking the promotion verifier`,
    );
  }
  for (const name of [
    "TERRASCALE_API_BASE_URL",
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
  assert.match(jobDefinition(workflow, "candidate"), /release-browser-candidate/u);
  assert.doesNotMatch(jobDefinition(workflow, "candidate"), /NPM_TOKEN/u);
  assert.match(jobDefinition(workflow, "candidate"), /name: candidate-npm/u);
  assert.match(jobDefinition(workflow, "candidate"), /--build-receipt artifacts\/handoff\/evidence\/npm\.build-receipt\.json/u);
  assert.match(jobDefinition(workflow, "candidate"), /--verify-only/u);
  assert.match(jobDefinition(workflow, "candidate"), /name: admitted-npm-candidate/u);
  assert.doesNotMatch(workflow, /ci (?:candidate-manifest|candidate-store|promote)\b/u);
  assert.doesNotMatch(jobDefinition(workflow, "candidate"), /^    env:/mu);
  assert.match(jobDefinition(workflow, "promote"), /promote-npm-candidate\.mjs/u);
  assert.match(jobDefinition(workflow, "promote"), /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.doesNotMatch(jobDefinition(workflow, "promote"), /^    env:/mu);
  assert.match(jobDefinition(workflow, "promote"), /needs: \[candidate\]/u);
  assert.match(jobDefinition(workflow, "promote"), /name: admitted-npm-candidate/u);
  assert.ok(jobDefinition(workflow, "promote").indexOf("--verify-only") < jobDefinition(workflow, "promote").indexOf("NPM_TOKEN:"));
  // 0addfcf: the bootstrap env var DEPLOY_CLI_READ_TOKEN is now fed by the
  // org registry token instead of a dedicated secret.
  assertCredentialStepIsFinal(workflow, "bootstrap", "REGISTRY_TOKEN");
  assertCredentialStepIsFinal(workflow, "release-e2e", "TERRASCALE_E2E_ACCESS_TOKEN");
  assertCredentialStepIsFinal(workflow, "promote", "NPM_TOKEN");
});

test("release E2E configuration guard fails closed before skip-capable tests", async () => {
  const guard = path.join(repositoryRoot, "scripts", "require-release-e2e-config.mjs");
  const environment = {
    PATH: process.env.PATH ?? "",
    TERRASCALE_API_BASE_URL: "https://api.example.test",
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
    await run("npx", ["vitest", "run", "src/public-schema-parity.test.ts"], { cwd: checkout });
    await run("node", ["scripts/generate-client-route-manifest.mjs", "--check"], { cwd: checkout });
    const packed = await run("npm", ["pack", "--pack-destination", packages], { cwd: checkout });
    const tarball = path.join(packages, packed.stdout.trim().split("\n").at(-1)!);
    const listing = await run("tar", ["-tf", tarball]);

    assert.match(listing.stdout, /^package\/dist\/index\.js$/mu);
    assert.match(listing.stdout, /^package\/dist\/index\.d\.ts$/mu);
    assert.match(listing.stdout, /^package\/dist\/local\/index\.js$/mu);
    assert.match(listing.stdout, /^package\/dist\/local\/index\.d\.ts$/mu);
    assert.match(listing.stdout, /^package\/dist\/local\/cli\.js$/mu);
    assert.match(listing.stdout, /^package\/sdk-route-manifest\.json$/mu);
    assert.doesNotMatch(
      listing.stdout,
      /^package\/(?:Documentation\/|node_modules\/|test\/|dist\/.*(?:\.map$|\.test\.|capture|internal))/mu,
    );

    const packedManifest = JSON.parse(
      await readPackedEntry(tarball, "package/sdk-route-manifest.json"),
    ) as PackedRouteManifest;
    assert.ok(Array.isArray(packedManifest.routes));
    for (const route of packedManifest.routes) {
      const routeDescription = [route.client, route.operation, route.routeName, route.pathTemplate]
        .filter((value): value is string => value !== undefined)
        .join(" ");
      assert.doesNotMatch(routeDescription, forbiddenRoutePattern);
    }

    const packedSources = await Promise.all([
      ["dist/index.d.ts", "package/dist/index.d.ts"] as const,
      ["dist/index.js", "package/dist/index.js"] as const,
      ["dist/client.d.ts", "package/dist/client.d.ts"] as const,
      ["dist/client.js", "package/dist/client.js"] as const,
      ["dist/local/index.d.ts", "package/dist/local/index.d.ts"] as const,
      ["dist/local/index.js", "package/dist/local/index.js"] as const,
    ].map(async ([name, entry]) => [name, await readPackedEntry(tarball, entry)] as const));
    for (const [name, source] of packedSources) {
      assert.doesNotMatch(source, forbiddenSymbolPattern, `${name} contains a removed SDK symbol`);
      assert.doesNotMatch(source, forbiddenRoutePattern, `${name} contains a removed SDK route`);
    }
    assert.doesNotMatch(packedSources.find(([name]) => name === "dist/client.d.ts")?.[1] ?? "", /readonly (?:auth|apiKeys|m2m|billing|pricing|security)\??:/u);
    assert.doesNotMatch(packedSources.find(([name]) => name === "dist/client.js")?.[1] ?? "", /this\.(?:auth|apiKeys|m2m|billing|pricing|security)\s*=/u);
    for (const method of [
      "updateAccountInformation",
      "createPaymentSession",
      "validatePaymentMethod",
      "registerOnboardingPaymentMethod",
      "updateMfaDecision",
    ]) {
      assert.doesNotMatch(
        packedSources.find(([name]) => name === "dist/client.d.ts")?.[1] ?? "",
        new RegExp(`\\b${method}\\b`, "u"),
      );
      assert.doesNotMatch(
        packedSources.find(([name]) => name === "dist/client.js")?.[1] ?? "",
        new RegExp(`\\b${method}\\b`, "u"),
      );
    }
    await run("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball], { cwd: consumer });
    const journey = path.join(consumer, "packed-journey.mjs");
    await writeFile(journey, packedConsumerJourney);
    await run("node", [journey], { cwd: consumer });
    await run(
      "node",
      [
        "--input-type=module",
        "--eval",
        "import { createTerraScaleClient } from '@terrascale/sdk'; import { createTerraScaleAdminClient } from '@terrascale/sdk/admin'; const client = createTerraScaleClient({ apiBaseUrl: 'https://api.example.test' }); if (typeof client.databases.list !== 'function') throw new Error('Typed project databases are missing'); for (const name of ['auth', 'apiKeys', 'm2m', 'billing', 'pricing', 'security']) { if (name in client) throw new Error(`Removed root client property remains: ${name}`); } for (const name of ['updateAccountInformation', 'createPaymentSession', 'validatePaymentMethod', 'registerOnboardingPaymentMethod', 'updateMfaDecision']) { if (name in client.onboarding) throw new Error(`Removed onboarding method remains: ${name}`); } createTerraScaleAdminClient({ apiBaseUrl: 'https://api.example.test' });",
      ],
      { cwd: consumer },
    );
    await run(
      "node",
      [
        "--input-type=module",
        "--eval",
        "import path from 'node:path'; import { createTerraScaleSqliteRuntime } from '@terrascale/sdk/local'; const local = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: path.resolve('packed-local.sqlite') } }); await local.close();",
      ],
      { cwd: consumer, env: localSqliteTestChildEnvironment() },
    );
    const cli = await run(
      path.join(consumer, "node_modules", ".bin", "terrascale-local"),
      ["--help"],
      { cwd: consumer },
    );
    assert.match(cli.stdout, /Usage: terrascale-local/u);
    for (const declaration of ["dist/index.d.ts", "dist/admin.d.ts"]) {
      const source = await readFile(path.join(checkout, declaration), "utf8");
      assert.doesNotMatch(source, /(?:node:sqlite|\.\/local\/)/u);
    }
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
  "TERRASCALE_E2E_ACCESS_TOKEN",
  "TERRASCALE_E2E_TENANT_ID",
  "TERRASCALE_E2E_PROJECT_ID",
  "TERRASCALE_E2E_DATABASE_ID",
  "TERRASCALE_E2E_DATABASE_WRITE_LEASE",
];
const missing = required.filter(name => (process.env[name] ?? "").trim().length === 0);
if (missing.length > 0) {
  console.log("Packed live journey skipped: " + missing.join(", ") + " is not configured.");
  process.exit(0);
}

const tenantId = toUlid(process.env.TERRASCALE_E2E_TENANT_ID);
const projectId = toUlid(process.env.TERRASCALE_E2E_PROJECT_ID);
const databaseId = toUlid(process.env.TERRASCALE_E2E_DATABASE_ID);
const leasePath = process.env.TERRASCALE_E2E_DATABASE_WRITE_LEASE.trim();
if (!path.isAbsolute(leasePath) || path.dirname(leasePath) === leasePath) {
  throw new TypeError("TERRASCALE_E2E_DATABASE_WRITE_LEASE must be an absolute file path.");
}
const client = createTerraScaleClient({
  apiBaseUrl: process.env.TERRASCALE_API_BASE_URL,
  accessToken: process.env.TERRASCALE_E2E_ACCESS_TOKEN,
  projectId,
});
const leaseScope = {
  tenantId,
  projectId,
  databaseId,
  apiBaseUrl: process.env.TERRASCALE_API_BASE_URL,
  credentialFingerprint: createHash("sha256").update(process.env.TERRASCALE_E2E_ACCESS_TOKEN).digest("hex"),
};

let lease;
try {
  lease = await open(leasePath, "wx");
} catch (error) {
  if (error instanceof Error && "code" in error && error.code === "EEXIST") {
    throw new Error("TERRASCALE_E2E_DATABASE_WRITE_LEASE is already held by another process.");
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

  const read = await client.databases.get(databaseId);
  assert.equal(read.ok, true, read.ok ? undefined : read.error.message);
  assert.equal(read.value.databaseId, databaseId);
  console.log("Packed live journey completed for the /api/v1 database read.");
} finally {
  await lease.close();
  await unlink(leasePath).catch(error => {
    if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) throw error;
  });
}
`;

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

async function readPackedEntry(tarball: string, entry: string): Promise<string> {
  const result = await run("tar", ["-xOf", tarball, entry]);
  return result.stdout;
}

type DeployCommand = {
  readonly description: string;
  readonly command: string;
  readonly arguments: readonly string[];
};

type DeployConfiguration = {
  readonly releaseProfiles: readonly { readonly name: string; readonly required: boolean; readonly commands: readonly DeployCommand[] }[];
  readonly buildTargets: readonly { readonly outputPath?: string; readonly commands: readonly DeployCommand[] }[];
};
