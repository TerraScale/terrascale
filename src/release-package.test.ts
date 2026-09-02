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

test("verification and promotion workflow follows the current delivery path", async () => {
  const [workflow, packageJson, packageLock] = await Promise.all([
    readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8"),
    readFile(path.join(repositoryRoot, "package.json"), "utf8"),
    readFile(path.join(repositoryRoot, "package-lock.json"), "utf8"),
  ]);
  const packageMetadata = JSON.parse(packageJson) as { readonly packageManager?: string };
  const lock = JSON.parse(packageLock) as {
    readonly lockfileVersion?: number;
    readonly packages?: { readonly "": { readonly name?: string; readonly version?: string } };
  };

  assert.match(workflow, /^on:\n  push:\n    branches: \[develop, main\]/mu);
  assert.doesNotMatch(workflow, /pull_request|dotnet|nuget|terrascale-builder:\s*\S+:/iu);
  assert.match(workflow, /permissions:\n  contents: read/u);
  assert.match(workflow, /repository: TerraScale-Old\/TerraScale\.Deploy/u);
  assert.match(workflow, /ref: 9a39633f88b085a6d40090ab451b1d71c89e3712/u);
  assert.match(workflow, /TERRASCALE_SOURCE_READ_TOKEN/u);
  assert.match(workflow, /setup-go@40f1582b2485089dde7abd97c1529aa768e1baff/u);
  assert.match(workflow, /GOBIN: \$\{\{ runner\.temp \}\}\/terrascale-deploy\/bin/u);
  assert.match(workflow, /retention-days: 1/u);
  assert.match(workflow, /buildvcs=true/u);
  assert.match(workflow, /internal\/cli\.Version=/u);

  const actionReferences = [...workflow.matchAll(/^\s*- uses: \S+@(\S+)$/gmu)];
  assert.ok(actionReferences.length > 0);
  for (const reference of actionReferences) {
    assert.match(reference[1] ?? "", /^[a-f0-9]{40}$/u);
  }
  for (const action of [
    "checkout@d23441a48e516b6c34aea4fa41551a30e30af803",
    "setup-go@40f1582b2485089dde7abd97c1529aa768e1baff",
    "setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020",
    "upload-artifact@ff15f0306b3f739f7b6fd43fb5d26cd321bd4de5",
  ]) {
    assert.match(workflow, new RegExp(`uses: https://data\\.forgejo\\.org/actions/${action}`, "u"));
  }

  for (const job of ["static", "build", "tests", "browser"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /needs: bootstrap/u);
    assert.match(definition, /runs-on: terrascale-untrusted/u);
    assert.match(definition, /image: mcr\.microsoft\.com\/playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
    assert.match(definition, /node scripts\/provision-reviewed-npm\.mjs/u);
    assert.match(definition, /npm install/u);
  }
  const staticJob = jobDefinition(workflow, "static");
  assert.match(staticJob, /repository: TerraScale\/ts-dashboard/u);
  assert.match(staticJob, /ref: develop/u);
  assert.match(staticJob, /path: Dashboard/u);
  for (const command of [
    "npm run typecheck",
    "npm run build",
    "npm run check:openapi-drift",
    "npm run check:route-manifest",
    "npm run check:forbidden-surface",
  ]) {
    assert.ok(staticJob.includes(command), `static job is missing ${command}`);
  }
  assert.match(jobDefinition(workflow, "tests"), /npm test/u);
  assert.match(jobDefinition(workflow, "browser"), /playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
  assert.match(jobDefinition(workflow, "browser"), /npm run test:browser/u);

  const bootstrap = jobDefinition(workflow, "bootstrap");
  assert.match(bootstrap, /ref: 9a39633f88b085a6d40090ab451b1d71c89e3712/u);
  assert.match(bootstrap, /token: \$\{\{ secrets\.TERRASCALE_SOURCE_READ_TOKEN \}\}/u);
  assert.match(bootstrap, /git -C deploy-source rev-parse HEAD/u);
  assert.match(bootstrap, /git -C deploy-source status --porcelain/u);
  assert.match(bootstrap, /upload-artifact/u);

  for (const job of ["static", "build", "tests", "browser", "candidate", "browser-candidate"]) {
    assert.match(jobDefinition(workflow, job), /image: mcr\.microsoft\.com\/playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
    assert.doesNotMatch(jobDefinition(workflow, job), /(?:NPM_TOKEN|REGISTRY_(?:USER|TOKEN)|TERRASCALE_E2E_\w+)/u);
  }
  const candidate = jobDefinition(workflow, "candidate");
  assert.match(candidate, /if: \$\{\{ forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main' \}\}/u);
  assert.match(candidate, /needs: \[bootstrap, static, build, tests, browser\]/u);
  assert.match(candidate, /ci build-candidate/u);
  assert.match(candidate, /--target npm/u);
  assert.match(candidate, /--expected-tree "\$\(git rev-parse HEAD\^\{tree\}\)"/u);
  assert.match(candidate, /--receipt artifacts\/evidence\/npm\.build-receipt\.json/u);
  assert.match(candidate, /name: candidate-npm/u);
  assert.match(candidate, /retention-days: 7/u);
  assert.equal((candidate.match(/node-version: "(?:22\.23\.2|24\.20\.0)"/gu) ?? []).length, 2);

  const candidateBrowser = jobDefinition(workflow, "browser-candidate");
  assert.match(candidateBrowser, /needs: \[candidate\]/u);
  assert.match(candidateBrowser, /TERRASCALE_BROWSER_CANDIDATE_TARBALL/u);
  assert.match(candidateBrowser, /npm run test:browser/u);
  assert.match(candidateBrowser, /--verify-only/u);
  assert.match(candidateBrowser, /playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);

  const publish = jobDefinition(workflow, "publish");
  assert.match(publish, /if: \$\{\{ forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main' \}\}/u);
  assert.match(publish, /needs: \[browser-candidate\]/u);
  assert.match(publish, /name: Publish the verified candidate/u);
  assert.match(publish, /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.equal((workflow.match(/NPM_TOKEN/gu) ?? []).length, 2);
  const tokenIndex = publish.indexOf("NPM_TOKEN:");
  assert.ok(tokenIndex > publish.indexOf("Verify immutable candidate"));
  assert.doesNotMatch(publish.slice(0, tokenIndex), /secrets\./u);

  assert.equal(packageMetadata.packageManager, "npm@11.18.0");
  assert.equal(lock.lockfileVersion, 3);
  assert.equal(lock.packages?.[""]?.name, "@terrascale/sdk");
  assert.equal(lock.packages?.[""]?.version, "0.3.0");
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

async function readPackedEntry(tarball: string, entry: string): Promise<string> {
  const result = await run("tar", ["-xOf", tarball, entry]);
  return result.stdout;
}
