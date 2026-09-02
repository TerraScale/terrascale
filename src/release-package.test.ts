import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "node:test";

import { localSqliteTestChildEnvironment } from "./local/test-environment.test.js";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);
const forbiddenRoutePattern = /(?:\/api\/v\d+\/(?:auth|account|billing|pricing|queues|onboarding\/payment)(?:[\/"'`: ]|$)|\/api\/v\d+\/projects\/\{[^}]+\}\/(?:auth|security)(?:[\/"'`: ]|$)|\b(?:auth|apiKeys|m2m|billing|pricing|payment|security)\.[a-z])/iu;
const forbiddenSymbolPattern = /\b(?:AuthClient|ProjectAuth\w*|authBaseUrl|ApiKey\w*|M2M\w*|GlobalQueue\w*|OnboardingPayment\w*|CreateOnboardingPayment\w*|ValidateOnboardingPayment\w*|RegisterOnboardingPayment\w*|PaymentMethod|Billing\w*|Pricing\w*|Security(?:Client|Settings\w*))\b/u;

type PackedRouteManifest = {
  readonly routes?: readonly {
    readonly client?: string;
    readonly operation?: string;
    readonly routeName?: string;
    readonly pathTemplate?: string;
  }[];
};

test("trunk pushes verify the SDK and main publishes one unchanged npm candidate", async () => {
  const [deploymentJson, workflow] = await Promise.all([
    readFile(path.join(repositoryRoot, ".terrascale", "deploy.json"), "utf8"),
    readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8"),
  ]);
  const deployment = JSON.parse(deploymentJson) as DeployConfiguration;

  assert.equal(deployment.repository, "ts-typescript");
  assert.deepEqual(deployment.dependencies, [{
    source: "src/",
    target: "ts-dashboard",
    reason: "SDK changes require the dashboard build and browser E2E",
  }]);
  assert.deepEqual(deployment.prProfiles.map((profile) => profile.name), ["static", "tests", "browser"]);
  assert.deepEqual(profileDescriptions(deployment, "static"), [
    "Product name policy tests",
    "Tracked product name sweep",
    "Clean install",
    "Typecheck",
    "Build SDK",
    "Public contract fixture",
    "OpenAPI contract descriptor",
    "OpenAPI drift",
    "Route drift",
    "Forbidden SDK surface",
  ]);
  assert.deepEqual(deployment.prProfiles.find((profile) => profile.name === "static")?.commands.slice(0, 2), [
    { description: "Product name policy tests", command: "node", arguments: ["--test", "test/product-name-check.test.mjs"] },
    { description: "Tracked product name sweep", command: "node", arguments: ["scripts/check-product-name.mjs", "--tracked-only"] },
  ]);
  assert.deepEqual(profileDescriptions(deployment, "tests"), ["Clean install", "Build SDK", "Complete SDK tests"]);
  assert.deepEqual(profileDescriptions(deployment, "browser"), ["Clean install", "Build SDK", "Browser transports"]);
  assert.deepEqual(
    deployment.prProfiles.find((profile) => profile.name === "static")?.commands.find((command) => command.description === "OpenAPI drift")?.arguments,
    ["run", "check:openapi-drift", "--", "--strict"],
  );
  assert.deepEqual(
    deployment.prProfiles.find((profile) => profile.name === "tests")?.commands.at(-1),
    { description: "Complete SDK tests", command: "npm", arguments: ["test"] },
  );
  assert.deepEqual(
    deployment.prProfiles.find((profile) => profile.name === "browser")?.commands.at(-1),
    { description: "Browser transports", command: "npm", arguments: ["run", "test:browser"] },
  );
  for (const commands of [...deployment.releaseProfiles, ...deployment.buildTargets].map((profile) => profile.commands)) {
    assert.deepEqual(commands.slice(0, 2).map((command) => command.description), ["Clean install", "Build SDK"]);
  }
  assert.deepEqual(
    deployment.buildTargets[0]?.commands.map((command) => command.description),
    ["Clean install", "Build SDK", "Forbidden SDK surface", "Unit and contract regression", "Qualify npm release candidate"],
  );
  assert.equal(deployment.buildTargets[0]?.outputPath, "artifacts/candidate");
  assert.deepEqual(deployment.buildTargets[0]?.commands.at(-1), {
    description: "Qualify npm release candidate",
    command: "node",
    arguments: ["scripts/qualify-npm-artifact.mjs", "--release", "--output", "artifacts/candidate"],
  });
  assert.doesNotMatch(deploymentJson, /"command": "npm", "arguments": \["pack"/u);
  assert.equal(deployment.releaseProfiles.every((profile) => profile.required), true);
  assert.match(workflow, /^on:\n  push:\n    branches: \[develop, main\]$/mu);
  assert.match(workflow, /^permissions:\n  contents: read$/mu);
  assert.doesNotMatch(workflow, /pull_request|forgejo\.base_ref|forgejo\.head_ref/u);
  assert.match(workflow, /TS_DEPLOY_REPOSITORY: "TerraScale-Old\/TerraScale\.Deploy"/u);
  assert.match(workflow, /repository: \$\{\{ env\.TS_DEPLOY_REPOSITORY \}\}/u);
  assert.doesNotMatch(workflow, /(?:TerraScale\.TypeScript|TerraScale\.Deploy\.Cli|NuGet|\.NET|dotnet|REGISTRY_TOKEN|FORGEJO_TOKEN)/iu);
  assert.doesNotMatch(workflow, /(?:bf9703ffca63|ce0183692c02|EXPECTED_DEPLOY_CLI_SHA|ts-deploy\/cmd\/terrascale-deploy@v)/u);
  assert.doesNotMatch(workflow, /TERRASCALE_SOURCE_READ_TOKEN/u);
  assert.equal((workflow.match(/NPM_TOKEN/gu) ?? []).length, 2);
  assert.equal((workflow.match(/secrets\.NPM_TOKEN/gu) ?? []).length, 1);
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
    "setup-go@40f1582b2485089dde7abd97c1529aa768e1baff",
  ]) {
    assert.match(workflow, new RegExp(`uses: https://data\\.forgejo\\.org/actions/${action}`, "u"));
  }
  const allJobs = [
    "bootstrap", "plan", "verify-static", "verify-node-22", "verify-node-24", "verify-browser",
    "candidate-build", "candidate-browser", "candidate-e2e", "candidate-version", "candidate", "publish",
  ];
  for (const job of allJobs) {
    assert.match(jobDefinition(workflow, job), /timeout-minutes: (?:30|45|60)/u);
  }
  for (const job of ["candidate-build", "candidate-browser", "candidate-e2e", "candidate-version", "candidate", "publish"]) {
    assert.match(jobDefinition(workflow, job), /forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main'/u);
  }
  for (const job of ["plan", "verify-node-22", "verify-node-24", "verify-browser"]) {
    assert.doesNotMatch(jobDefinition(workflow, job), /refs\/heads\/main/u);
    assert.doesNotMatch(jobSteps(workflow, job), /secrets\.[A-Z0-9_]+/u);
  }

  const bootstrap = jobDefinition(workflow, "bootstrap");
  assert.match(bootstrap, /name: Require the least-privileged Deploy repository token/u);
  assert.match(bootstrap, /TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN: \$\{\{ secrets\.TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN \}\}/u);
  assert.match(bootstrap, /repository: \$\{\{ env\.TS_DEPLOY_REPOSITORY \}\}/u);
  assert.match(bootstrap, /token: \$\{\{ secrets\.TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN \}\}/u);
  assert.match(bootstrap, /persist-credentials: false/u);
  assert.match(bootstrap, /TS_DEPLOY_REVISION|TS_DEPLOY_EXPECTED_TREE/u);
  assert.match(bootstrap, /exit 1/u);
  assert.match(bootstrap, /setup-go@40f1582b2485089dde7abd97c1529aa768e1baff/u);
  assert.match(bootstrap, /go build -trimpath -o/u);
  assert.doesNotMatch(bootstrap, /TERRASCALE_SOURCE_READ_TOKEN|REGISTRY_TOKEN|github\.token|forgejo\.token|gitea\.token/u);

  const checkoutBlocks = [...workflow.matchAll(/uses: https:\/\/data\.forgejo\.org\/actions\/checkout@[^\n]+\n\s+with:\n([\s\S]*?)(?=\n      - |\n    [a-z-]+:|\n$)/gu)].map((match) => match[1] ?? "");
  assert.equal(checkoutBlocks.length, 13);
  assert.equal(checkoutBlocks.filter((block) => /token: \$\{\{ secrets\.TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN \}\}/u.test(block)).length, 1);
  assert.equal(checkoutBlocks.filter((block) => /token: \$\{\{ secrets\.TERRASCALE_DASHBOARD_REPOSITORY_READ_TOKEN \}\}/u.test(block)).length, 1);
  for (const block of checkoutBlocks.filter((block) => !/TERRASCALE_(?:DEPLOY|DASHBOARD)_REPOSITORY_READ_TOKEN/u.test(block))) {
    assert.match(block, /token: ""/u);
    assert.doesNotMatch(block, /(?:github|forgejo|gitea)\.token/u);
  }

  const plan = jobDefinition(workflow, "plan");
  assert.match(plan, /ci plan --repository-root ts-typescript --require-clean-source/u);
  for (const job of ["plan", "verify-static", "verify-node-22", "verify-node-24", "verify-browser", "candidate-build"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /needs: (?:bootstrap|\[[^\]]*\bbootstrap\b[^\]]*\])/u);
    assert.match(definition, /name: deploy-cli, path: \.tools/u);
  }

  const staticJob = jobDefinition(workflow, "verify-static");
  assert.match(staticJob, /runs-on: terrascale-untrusted/u);
  assert.match(staticJob, /repository: TerraScale\/ts-dashboard/u);
  assert.match(staticJob, /ref: develop/u);
  assert.match(staticJob, /path: ts-typescript\/Dashboard/u);
  assert.match(staticJob, /token: \$\{\{ secrets\.TERRASCALE_DASHBOARD_REPOSITORY_READ_TOKEN \}\}/u);
  assert.equal((staticJob.match(/secrets\./gu) ?? []).length, 1);
  assert.doesNotMatch(staticJob, /TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN|TERRASCALE_SOURCE_READ_TOKEN|NPM_TOKEN|TERRASCALE_E2E_ACCESS_TOKEN/u);
  assert.match(staticJob, /ci pr --repository-root ts-typescript --profile static/u);

  for (const [job, version] of [["verify-node-22", "22.23.2"], ["verify-node-24", "24.20.0"]] as const) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, new RegExp(`node-version: "${version.replaceAll(".", "\\.")}"`, "u"));
    assert.match(definition, /runs-on: terrascale-untrusted/u);
    assert.match(definition, /ci pr --repository-root ts-typescript --profile tests/u);
  }
  const browserJob = jobDefinition(workflow, "verify-browser");
  assert.match(browserJob, /runs-on: terrascale-untrusted/u);
  assert.match(browserJob, /ci pr --repository-root ts-typescript --profile browser/u);

  const candidateBuild = jobDefinition(workflow, "candidate-build");
  assert.match(candidateBuild, /runs-on: terrascale-untrusted/u);
  assert.match(candidateBuild, /needs: \[bootstrap, plan, verify-static, verify-node-22, verify-node-24, verify-browser\]/u);
  assert.match(candidateBuild, /node-version: "22\.23\.2"/u);
  assert.match(candidateBuild, /node-version: "24\.20\.0"/u);
  assert.equal((candidateBuild.match(/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020/gu) ?? []).length, 2);
  assert.match(candidateBuild, /\.tools\/terrascale-deploy ci build-candidate --repository-root ts-typescript --target npm/u);
  assert.match(candidateBuild, /--expected-tree "\$\{EXPECTED_TREE\}"/u);
  assert.match(candidateBuild, /--receipt ts-typescript\/artifacts\/evidence\/npm\.build-receipt\.json/u);
  assert.match(candidateBuild, /name: candidate-npm/u);
  assert.doesNotMatch(candidateBuild, /candidate_command|eval|(?:ba|z|k)?sh -c/u);

  for (const job of ["verify-static", "verify-node-22", "verify-node-24", "verify-browser", "candidate-build", "candidate-browser"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /runs-on: terrascale-untrusted/u);
    assert.match(definition, /image: mcr\.microsoft\.com\/playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
    assert.doesNotMatch(definition, /secrets\.(?:NPM_TOKEN|TERRASCALE_E2E_\w+)/u);
  }

  const candidateBrowser = jobDefinition(workflow, "candidate-browser");
  assert.match(candidateBrowser, /needs: \[candidate-build\]/u);
  assert.match(candidateBrowser, /npm ci/u);
  assert.match(candidateBrowser, /TERRASCALE_BROWSER_CANDIDATE_TARBALL/u);
  assert.match(candidateBrowser, /npm run test:browser/u);
  assert.ok(candidateBrowser.indexOf("npm ci") < candidateBrowser.indexOf("npm run test:browser"));
  assert.doesNotMatch(candidateBrowser, /npm run build|npm pack/u);

  const candidateE2e = jobDefinition(workflow, "candidate-e2e");
  assert.match(candidateE2e, /needs: \[candidate-build\]/u);
  assert.match(candidateE2e, /require-release-e2e-config\.mjs/u);
  assert.match(candidateE2e, /run-packed-release-e2e\.mjs --tarball/u);
  assert.doesNotMatch(candidateE2e, /npm run build|npm run test:integration|npm pack/u);
  assert.ok(candidateE2e.indexOf("--verify-only") < candidateE2e.indexOf("TERRASCALE_E2E_ACCESS_TOKEN"));
  assert.match(jobDefinition(workflow, "candidate-version"), /--check-availability --package @terrascale\/sdk/u);

  for (const job of ["candidate-browser", "candidate-e2e", "candidate-version", "candidate", "publish"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /node-version: "24\.20\.0"/u);
    assert.match(definition, /node scripts\/provision-reviewed-npm\.mjs/u);
  }
  for (const name of [
    "TERRASCALE_API_BASE_URL",
    "TERRASCALE_E2E_PROJECT_ID",
    "TERRASCALE_E2E_ORGANIZATION_ID",
    "TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID",
  ]) {
    assert.match(candidateE2e, new RegExp(`${name}: \\$\\{\\{ vars\\.${name} \\}\\}`, "u"));
  }
  assertCredentialStepIsFinal(workflow, "candidate-e2e", "TERRASCALE_E2E_ACCESS_TOKEN");

  const candidate = jobDefinition(workflow, "candidate");
  assert.match(candidate, /needs: \[candidate-build, candidate-browser, candidate-e2e, candidate-version\]/u);
  assert.match(candidate, /name: candidate-npm/u);
  assert.match(candidate, /--verify-only/u);
  assert.match(candidate, /name: admitted-npm-candidate/u);
  assert.doesNotMatch(candidate, /NPM_TOKEN|secrets\./u);

  const publish = jobDefinition(workflow, "publish");
  assert.match(publish, /needs: \[candidate\]/u);
  assert.match(publish, /name: admitted-npm-candidate/u);
  assert.match(publish, /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.ok(publish.indexOf("--verify-only") < publish.indexOf("NPM_TOKEN:"));
  assertCredentialStepIsFinal(workflow, "publish", "NPM_TOKEN");
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
    await run("node", ["--test", "dist/public-schema-parity.test.js"], { cwd: checkout });
    await run("npm", ["run", "check:route-manifest"], { cwd: checkout });
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
        "import { createTerraScaleClient } from '@terrascale/sdk'; import { createTerraScaleAdminClient } from '@terrascale/sdk/admin'; const client = createTerraScaleClient({ apiBaseUrl: 'https://api.example.test' }); if (typeof client.queues.list !== 'function') throw new Error('Typed project queues are missing'); for (const name of ['auth', 'apiKeys', 'm2m', 'billing', 'pricing', 'security']) { if (name in client) throw new Error(`Removed root client property remains: ${name}`); } for (const name of ['updateAccountInformation', 'createPaymentSession', 'validatePaymentMethod', 'registerOnboardingPaymentMethod', 'updateMfaDecision']) { if (name in client.onboarding) throw new Error(`Removed onboarding method remains: ${name}`); } createTerraScaleAdminClient({ apiBaseUrl: 'https://api.example.test' });",
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

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}

function profileDescriptions(configuration: DeployConfiguration, name: string): string[] {
  const profile = configuration.prProfiles.find((candidate) => candidate.name === name);
  if (profile === undefined) assert.fail(`Deploy profile ${name} is missing.`);
  return profile.commands.map((command) => command.description);
}

/** Return the job without its container block so step-secret checks stay local to steps. */
function jobSteps(workflow: string, job: string): string {
  return jobDefinition(workflow, job).replace(/\n    container:\n(?:[ ]{6,}.*\n?)+/u, "");
}

function assertCredentialStepIsFinal(workflow: string, job: string, credential: string): void {
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
  readonly repository: string;
  readonly dependencies: readonly { readonly source: string; readonly target: string; readonly reason: string }[];
  readonly prProfiles: readonly { readonly name: string; readonly commands: readonly DeployCommand[] }[];
  readonly releaseProfiles: readonly { readonly name: string; readonly required: boolean; readonly commands: readonly DeployCommand[] }[];
  readonly buildTargets: readonly { readonly outputPath?: string; readonly commands: readonly DeployCommand[] }[];
};
