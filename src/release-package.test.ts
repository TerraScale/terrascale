import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
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

test("workflow delegates host-mode verification to the Deploy CLI", async () => {
  const workflow = await readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8");

  assert.doesNotMatch(workflow, /(?:container:|\bimage:|docker|podman|REGISTRY_|DEPLOY_CLI_READ_TOKEN)/iu);
  assert.match(workflow, /runs-on: terrascale-builder/u);
  assert.match(workflow, /actions\/checkout@d23441a48e516b6c34aea4fa41551a30e30af803/u);
  assert.match(workflow, /TS_DEPLOY_VERSION: \$\{\{ vars\.TS_DEPLOY_VERSION \}\}/u);
  assert.match(workflow, /case "\$\(uname -m\)" in[\s\S]*aarch64\|arm64\) asset=ts-deploy-linux-arm64[\s\S]*x86_64\|amd64\) asset=ts-deploy-linux-amd64/u);
  assert.match(workflow, /release="\$\{TS_DEPLOY_VERSION:-latest\}"/u);
  assert.match(workflow, /case "\$\{release\}" in[\s\S]*latest\|v\[0-9\]\*\.\[0-9\]\*\.\[0-9\]\*\)/u);
  assert.match(workflow, /fj release view "\$\{release\}" --repo TerraScale\/ts-deploy/u);
  assert.match(workflow, /fj release asset download "\$\{release\}" "\$\{asset\}" --repo TerraScale\/ts-deploy --output ts-deploy/u);
  assert.match(workflow, /chmod \+x ts-deploy/u);
  assert.match(workflow, /\.\/ts-deploy ci --repo ts-typescript/u);
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
  const producerContracts = path.join(workspace, "terrabase", "contracts");
  const packages = path.join(workspace, "packages");
  const consumer = path.join(workspace, "consumer");

  try {
    await cp(repositoryRoot, checkout, {
      recursive: true,
      filter: (source) => ![".git", "node_modules", "dist"].includes(path.basename(source)),
    });
    await mkdir(path.dirname(producerContracts), { recursive: true });
    await cp(path.resolve(repositoryRoot, "../terrabase/contracts"), producerContracts, { recursive: true });
    await mkdir(packages);
    await mkdir(consumer);
    const checkoutEnvironment = { ...process.env, CONTRACT_SOURCE: "../terrabase/contracts" };
    await run("npm", ["ci", "--ignore-scripts", "--no-audit", "--no-fund"], { cwd: checkout });
    await run("npm", ["run", "build"], { cwd: checkout });
    await run("npx", ["vitest", "run", "src/public-schema-parity.test.ts"], { cwd: checkout, env: checkoutEnvironment });
    await run("node", ["scripts/generate-client-route-manifest.ts", "--check"], { cwd: checkout, env: checkoutEnvironment });
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
    await run("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball], { cwd: consumer });
    const journey = path.join(consumer, "packed-journey.ts");
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

async function readPackedEntry(tarball: string, entry: string): Promise<string> {
  const result = await run("tar", ["-xOf", tarball, entry]);
  return result.stdout;
}
