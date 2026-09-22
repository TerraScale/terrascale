import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";

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

test("trunk pushes run native SDK checks while npm publication stays gated", async () => {
  const [deploymentJson, workflow, packageJson, packageLock] = await Promise.all([
    readFile(path.join(repositoryRoot, ".terrascale", "deploy.json"), "utf8"),
    readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8"),
    readFile(path.join(repositoryRoot, "package.json"), "utf8"),
    readFile(path.join(repositoryRoot, "package-lock.json"), "utf8"),
  ]);
  const deployment = JSON.parse(deploymentJson) as DeployConfiguration;
  const packageMetadata = JSON.parse(packageJson) as { readonly packageManager?: string };
  const lock = JSON.parse(packageLock) as {
    readonly lockfileVersion?: number;
    readonly packages?: { readonly "": { readonly name?: string; readonly version?: string } };
  };

  assert.equal(deployment.repository, "ts-typescript");
  assert.deepEqual(deployment.dependencies, [{
    source: "src/",
    target: "ts-dashboard",
    reason: "SDK changes require the dashboard build and browser E2E",
  }]);
  assert.deepEqual(deployment.prProfiles.map((profile) => profile.name), ["static", "tests", "browser"]);
  assert.deepEqual(profileDescriptions(deployment, "static"), [
    "Clean install",
    "Typecheck",
    "Build SDK",
    "Public contract fixture",
    "OpenAPI contract descriptor",
    "Route drift",
    "Forbidden SDK surface",
  ]);
  assert.deepEqual(deployment.prProfiles.find((profile) => profile.name === "static")?.commands.at(-1), {
    description: "Forbidden SDK surface",
    command: "npm",
    arguments: ["run", "check:forbidden-surface"],
  });
  assert.deepEqual(profileDescriptions(deployment, "tests"), ["Clean install", "Build SDK", "Complete SDK tests"]);
  assert.deepEqual(profileDescriptions(deployment, "browser"), ["Clean install", "Build SDK", "Browser transports"]);
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
    arguments: ["scripts/qualify-npm-artifact.ts", "--release", "--output", "artifacts/candidate"],
  });
  assert.doesNotMatch(deploymentJson, /"command": "npm", "arguments": \["pack"/u);
  assert.equal(packageMetadata.packageManager, "npm@11.18.0");
  assert.equal(lock.lockfileVersion, 3);
  assert.equal(lock.packages?.[""]?.name, "@terrascale/sdk");
  assert.equal(lock.packages?.[""]?.version, "0.3.0");
  assert.equal(deployment.releaseProfiles.every((profile) => profile.required), true);
  assert.match(workflow, /^on:\n  push:\n    branches: \[develop, main\]$/mu);
  assert.match(workflow, /^  workflow_dispatch:\n$/mu);
  assert.match(workflow, /^permissions:\n  contents: read$/mu);
  assert.doesNotMatch(workflow, /pull_request|schedule|workflow_call/u);
  assert.doesNotMatch(workflow, /(?:ts-deploy|TERRASCALE_DEPLOY|NPM_TOKEN|TERRASCALE_E2E_)/u);

  const jobs = ["static", "node", "browser"];
  for (const job of jobs) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /runs-on: terrascale-builder/u);
    assert.match(definition, /timeout-minutes: (?:45|60)/u);
    assert.doesNotMatch(definition, /container:\s*\n\s+image:/u);
    assert.match(definition, /actions\/checkout@v6\.1\.0/u);
    assert.match(definition, /ref: \$\{\{ forgejo\.sha \}\}/u);
    assert.match(definition, /token: "\$\{\{ secrets\.REGISTRY_TOKEN \}\}"/u);
    assert.match(definition, /persist-credentials: false/u);
  }

  const staticJob = jobDefinition(workflow, "static");
  for (const command of [
    "npm run typecheck",
    "npm run build",
    "npm run check:public-contract-fixture",
    "npm run check:openapi-contract-descriptor",
    "npm run check:route-manifest",
    "npm run check:forbidden-surface",
  ]) {
    assert.match(staticJob, new RegExp(escapeRegExp(command), "u"));
  }
  for (const job of [staticJob, jobDefinition(workflow, "node")]) {
    assert.match(job, /repository: TerraScale\/ts-rust/u);
    assert.match(job, /repository: TerraScale\/ts-atlas/u);
  }

  const nodeJob = jobDefinition(workflow, "node");
  assert.match(nodeJob, /strategy:\r?\n      fail-fast: false\r?\n      matrix:/u);
  assert.match(nodeJob, /node: \["22\.23\.2", "24\.20\.0"\]/u);
  assert.match(nodeJob, /Install Node\.js \$\{\{ matrix\.node \}\}/u);
  assert.match(nodeJob, /sha256sum --check --strict/u);
  assert.match(nodeJob, /test "\$\(node --version\)" = "v\$\{\{ matrix\.node \}\}"/u);
  assert.match(nodeJob, /architecture: process\.arch/u);
  assert.match(nodeJob, /uname -m/u);
  assert.match(nodeJob, /npm test/u);

  const browserJob = jobDefinition(workflow, "browser");
  assert.match(browserJob, /Install the repository Node\.js version/u);
  assert.match(browserJob, /test "\$\(node --version\)" = v24\.20\.0/u);
  assert.match(browserJob, /architecture: process\.arch/u);
  assert.match(browserJob, /uname -m/u);
  assert.match(browserJob, /npx playwright install chromium firefox webkit/u);
  assert.match(browserJob, /npm run test:browser/u);

  assert.doesNotMatch(workflow, /(?:candidate|admit|publish|promote-npm-candidate)/iu);
});
test("release E2E configuration guard fails closed before skip-capable tests", async () => {
  const guard = path.join(repositoryRoot, "scripts", "require-release-e2e-config.ts");
  const environment = {
    PATH: process.env.PATH ?? "",
    TERRASCALE_API_BASE_URL: "https://api.example.test",
    TERRASCALE_E2E_ACCESS_TOKEN: "test-token",
    TERRASCALE_E2E_PROJECT_ID: "01J00000000000000000000000",
    TERRASCALE_E2E_ORGANIZATION_ID: "01J00000000000000000000001",
    TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID: "01J00000000000000000000002",
  };

  await assert.rejects(run(process.execPath, [guard], { env: { PATH: environment.PATH } }), /Release E2E configuration is missing/u);
  await assert.rejects(
    run(process.execPath, [guard], { env: { ...environment, TERRASCALE_API_BASE_URL: "not-a-url", TERRASCALE_E2E_PROJECT_ID: "not-a-ulid" } }),
    /Release E2E configuration is invalid: TERRASCALE_API_BASE_URL, TERRASCALE_E2E_PROJECT_ID/u,
  );
  await run(process.execPath, [guard], { env: environment });
});

test("a clean SDK-only checkout runs fixture tests and installs a usable npm pack", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-release-package-"));
  const checkout = path.join(workspace, "sdk");
  const producerContracts = path.join(workspace, "terrabase", "contracts");
  const edgeContracts = path.join(workspace, "edge", "contracts");
  const packages = path.join(workspace, "packages");
  const consumer = path.join(workspace, "consumer");

  try {
    await cp(repositoryRoot, checkout, {
      recursive: true,
      filter: (source) => ![".git", "node_modules", "dist"].includes(path.basename(source)),
    });
    await mkdir(path.dirname(producerContracts), { recursive: true });
    const { resolveContractBundle } = await import(pathToFileURL(path.join(repositoryRoot, "scripts", "openapi-contract-paths.ts")).href);
    const bundle = await resolveContractBundle();
    try {
      await cp(bundle.root, producerContracts, { recursive: true });
    } finally {
      await bundle.cleanup();
    }
    await cp(path.join(repositoryRoot, "..", "ts-go", "contracts", "edge", "v1"), edgeContracts, { recursive: true });
    await mkdir(packages);
    await mkdir(consumer);
    const checkoutEnvironment = { ...process.env };
    for (const key of Object.keys(checkoutEnvironment)) {
      if (/^(?:TERRASCALE_)?CONTRACT_(?:SOURCE|URL|REGISTRY(?:_URL)?)$/u.test(key)) delete checkoutEnvironment[key];
    }
    checkoutEnvironment.CONTRACT_SOURCE = producerContracts;
    checkoutEnvironment.EDGE_CONTRACT_SOURCE = edgeContracts;
    await run("npm", ["ci", "--ignore-scripts", "--no-audit", "--no-fund"], { cwd: checkout });
    await run("npm", ["run", "build"], { cwd: checkout });
    await run("npx", ["vitest", "run", "src/public-schema-parity.test.ts"], { cwd: checkout, env: checkoutEnvironment });
    await run(process.execPath, ["scripts/generate-client-route-manifest.ts", "--check"], { cwd: checkout, env: checkoutEnvironment });
    const packed = await run("npm", ["pack", "--pack-destination", packages], { cwd: checkout });
    const tarball = path.join(packages, packed.stdout.trim().split("\n").at(-1)!);
    const listing = await run("tar", ["-tf", tarball]);

    assert.match(listing.stdout, /^package\/dist\/index\.js$/mu);
    assert.match(listing.stdout, /^package\/dist\/index\.d\.ts$/mu);
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
    // The generated journeys are native ESM TypeScript files. Give the
    // temporary consumer the same package boundary a real ESM consumer has;
    // otherwise Node 24 interprets the fixture as CommonJS before the packed
    // SDK can be exercised.
    await writeFile(path.join(consumer, "package.json"), JSON.stringify({ type: "module" }));
    await run("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball, "@better-auth/core@1.7.2", "react@18.3.1"], { cwd: consumer });
    const journey = path.join(consumer, "packed-journey.ts");
    await writeFile(journey, packedConsumerJourney);
    await run(process.execPath, [journey], { cwd: consumer });
    const adapterJourney = path.join(consumer, "packed-adapters.ts");
    await writeFile(adapterJourney, packedAdapterJourney);
    await run(process.execPath, [adapterJourney], { cwd: consumer });
    await run(process.execPath, ["--conditions=workerd", "--conditions=browser", "--input-type=module", "--eval",
      "import { createTerraScaleBetterAuthAdapter } from '@terrascale/sdk/better-auth'; if (typeof createTerraScaleBetterAuthAdapter !== 'function') throw new Error('Workerd server adapter is missing');",
    ], { cwd: consumer });
    await run(process.execPath, ["--conditions=browser", "--input-type=module", "--eval",
      "import assert from 'node:assert/strict'; await assert.rejects(import('@terrascale/sdk/better-auth'), { code: 'ERR_PACKAGE_PATH_NOT_EXPORTED' });",
    ], { cwd: consumer });
    await run(
      "node",
      [
        "--input-type=module",
        "--eval",
        "import { createTerraScaleClient } from '@terrascale/sdk'; import { createTerraScaleAdminClient } from '@terrascale/sdk/admin'; const client = createTerraScaleClient({ apiBaseUrl: 'https://api.example.test' }); if (typeof client.databases.list !== 'function') throw new Error('Typed project databases are missing'); for (const name of ['auth', 'apiKeys', 'm2m', 'billing', 'pricing', 'security']) { if (name in client) throw new Error(`Removed root client property remains: ${name}`); } for (const name of ['updateAccountInformation', 'createPaymentSession', 'validatePaymentMethod', 'registerOnboardingPaymentMethod', 'updateMfaDecision']) { if (name in client.onboarding) throw new Error(`Removed onboarding method remains: ${name}`); } createTerraScaleAdminClient({ apiBaseUrl: 'https://api.example.test' });",
      ],
      { cwd: consumer },
    );
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
import { createTerraScaleClient } from "@terrascale/sdk";

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

const tenantId = process.env.TERRASCALE_E2E_TENANT_ID;
const projectId = process.env.TERRASCALE_E2E_PROJECT_ID;
const databaseId = process.env.TERRASCALE_E2E_DATABASE_ID;
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

const packedAdapterJourney = String.raw`import assert from "node:assert/strict";
import { ReactiveQueryObserver } from "@terrascale/sdk/react";
import { TerraScaleSyncV1Client } from "@terrascale/sdk";
import { terrascaleCollectionOptions } from "@terrascale/sdk/tanstack";

let aborted = false;
const query = new ReactiveQueryObserver({
  subscribe: (_observer, context) => {
    context.signal.addEventListener("abort", () => { aborted = true; }, { once: true });
  },
});
const stopQuery = query.subscribe(() => undefined);
assert.equal(query.getSnapshot().isFetching, true);
stopQuery();
assert.equal(aborted, true);

const client = new TerraScaleSyncV1Client();
const frames = [];
const collection = terrascaleCollectionOptions({
  client,
  subscription: "packaged-consumer",
  query: "from todos",
  getKey: row => String(row.id),
  transport: { send: frame => { frames.push(frame); } },
  mutate: async request => {
    assert.equal(request.operation, "insert");
    assert.equal(typeof request.idempotencyKey, "string");
    assert.equal(request.signal.aborted, false);
    return { status: "rejected", error: new Error("controlled producer rejection") };
  },
});
const cleanup = collection.adapter.options.sync.sync({
  collection: {}, begin: () => undefined, write: () => undefined,
  commit: () => true, markReady: () => undefined, markError: error => { throw error; }, truncate: () => undefined,
});
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(frames.length, 1);
await assert.rejects(
  collection.onInsert({ transaction: {
    id: "tx-packaged",
    mutations: [{ type: "insert", mutationId: "m-1", key: "todo-1", modified: { id: "todo-1", done: true } }],
  } }),
  /controlled producer rejection/u,
);
cleanup?.();
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(frames.length, 2);
collection.adapter.dispose();
console.log("Packaged React/TanStack adapter consumer qualification passed.");
`;

function profileDescriptions(configuration: DeployConfiguration, name: string): string[] {
  const profile = configuration.prProfiles.find((candidate) => candidate.name === name);
  if (profile === undefined) assert.fail(`Deploy profile ${name} is missing.`);
  return profile.commands.map((command) => command.description);
}

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
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
