import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const run = promisify(execFile);

test("deploy profiles retain mandatory current-source, framework and deployed release gates", async () => {
  const deployment = JSON.parse(
    await readFile(
      path.join(repositoryRoot, ".terrascale/deploy.json"),
      "utf8",
    ),
  );
  for (const profile of [
    ...deployment.prProfiles,
    ...deployment.releaseProfiles,
    ...deployment.buildTargets,
  ]) {
    assert.deepEqual(
      profile.commands
        .slice(0, 2)
        .map((command: { description: string }) => command.description),
      ["Clean install", profile.name === "static" ? "Typecheck" : "Build SDK"],
    );
    if (profile.name !== "browser")
      assert.ok(
        profile.commands.some((command: { arguments: string[] }) =>
          command.arguments.includes("check:current-contract-inputs"),
        ),
      );
  }
  const release = deployment.releaseProfiles.find(
    (profile: { name: string }) => profile.name === "release-e2e",
  );
  assert.equal(release.required, true);
  assert.ok(
    release.commands.some((command: { arguments: string[] }) =>
      command.arguments.includes("qualify:release-e2e"),
    ),
  );
  for (const profile of [
    deployment.prProfiles.find(
      (item: { name: string }) => item.name === "tests",
    ),
    deployment.buildTargets[0],
  ]) {
    assert.ok(
      profile.commands.some((command: { arguments: string[] }) =>
        command.arguments.includes("check:frameworks"),
      ),
    );
  }
});

test("required release E2E refuses unavailable production capabilities even with credentials configured", async () => {
  const guard = path.join(
    repositoryRoot,
    "scripts/require-release-e2e-config.ts",
  );
  for (const env of [
    { PATH: process.env.PATH ?? "" },
    {
      ...process.env,
      TERRABASE_API_KEY: "test-config-only",
      TERRABASE_ENDPOINT: "https://gru.terrascale.tech",
    },
  ]) {
    await assert.rejects(
      run(process.execPath, [guard], { env }),
      /(?:unavailable|blocked|admission)/iu,
    );
  }
});

test("clean SDK-only build packs and independently installs the current typed package", async () => {
  const workspace = await mkdtemp(
    path.join(tmpdir(), "terrascale-sdk-release-package-"),
  );
  const checkout = path.join(workspace, "sdk");
  const packages = path.join(workspace, "packages");
  const consumer = path.join(workspace, "consumer");
  try {
    await cp(repositoryRoot, checkout, {
      recursive: true,
      filter: (source) =>
        ![
          ".git",
          "node_modules",
          "dist",
          "artifacts",
          "test-results",
          "playwright-report",
        ].includes(path.basename(source)),
    });
    await Promise.all([mkdir(packages), mkdir(consumer)]);
    // Use the reviewed CLI entrypoint for every subprocess, not an ambient npm.
    const { inspectNpmCli } = await import(
      pathToFileURL(path.join(repositoryRoot, "scripts/npm-cli-evidence.ts"))
        .href
    );
    const npm = await inspectNpmCli();
    assert.equal(npm.evidence.version, "11.18.0");
    const npmRun = (args: string[], cwd: string) =>
      run(process.execPath, [npm.entrypoint, ...args], {
        cwd,
        maxBuffer: 4 * 1024 * 1024,
      });
    await npmRun(
      ["ci", "--ignore-scripts", "--no-audit", "--no-fund"],
      checkout,
    );
    await npmRun(["run", "build"], checkout);
    const packed = await npmRun(
      ["pack", "--json", "--pack-destination", packages],
      checkout,
    );
    const metadata = JSON.parse(packed.stdout);
    const filename = metadata[0]?.filename;
    assert.equal(typeof filename, "string");
    assert.equal(path.basename(filename), filename);
    const tarball = path.join(packages, filename);
    const listing = (await run("tar", ["-tf", tarball])).stdout;
    for (const entry of [
      "dist/index.js",
      "dist/index.d.ts",
      "dist/database.js",
      "sdk-current-contract.json",
      "sdk-route-manifest.json",
    ])
      assert.match(
        listing,
        new RegExp(`^package/${entry.replaceAll(".", "\\.")}$`, "mu"),
      );
    assert.doesNotMatch(
      listing,
      /^package\/(?:node_modules|test|src)\/|^package\/dist\/.*(?:\.test\.|\.map$|msgpack|sync\/v1|client\.js)/mu,
    );
    await writeFile(
      path.join(consumer, "package.json"),
      JSON.stringify({ type: "module" }),
    );
    await npmRun(
      [
        "install",
        "--ignore-scripts",
        "--no-audit",
        "--no-fund",
        tarball,
        "@tanstack/db@0.9.2",
        "svelte@5.55.1",
        "react@19.2.8",
        "react-test-renderer@19.2.8",
        "@types/react@19.2.14",
        "@types/react-test-renderer@19.1.0",
        "typescript@5.9.3",
        "pg@8.23.0",
        "@types/pg@8.23.1",
        "@types/node@26.0.0",
      ],
      consumer,
    );
    const journey = path.join(consumer, "boundary.mjs");
    await writeFile(journey, installedBoundary);
    await run(process.execPath, [journey], { cwd: consumer });
    await run(
      process.execPath,
      [
        "--conditions=browser",
        "--input-type=module",
        "--eval",
        "import assert from 'node:assert/strict'; for (const entry of ['local','better-auth','postgres']) await assert.rejects(import('@terrascale/sdk/' + entry), { code: 'ERR_PACKAGE_PATH_NOT_EXPORTED' });",
      ],
      { cwd: consumer },
    );
    await run(
      process.execPath,
      [
        "--conditions=workerd",
        "--input-type=module",
        "--eval",
        "import assert from 'node:assert/strict'; for (const entry of ['local','postgres']) await assert.rejects(import('@terrascale/sdk/' + entry), { code: 'ERR_PACKAGE_PATH_NOT_EXPORTED' });",
      ],
      { cwd: consumer },
    );
    const frameworkCaller = path.join(consumer, "framework-caller.ts");
    await writeFile(frameworkCaller, installedFrameworkCaller);
    await npmRun(
      [
        "exec",
        "--",
        "tsc",
        "--noEmit",
        "--strict",
        "--skipLibCheck",
        "--target",
        "ES2023",
        "--module",
        "NodeNext",
        "--moduleResolution",
        "NodeNext",
        frameworkCaller,
      ],
      consumer,
    );
    await run(process.execPath, [frameworkCaller], { cwd: consumer });
    const declarations = await readFile(
      path.join(checkout, "dist/index.d.ts"),
      "utf8",
    );
    assert.doesNotMatch(declarations, /node:sqlite|local\//u);
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

const installedBoundary = `import assert from "node:assert/strict";
import * as sdk from "@terrascale/sdk";
import { requireTerraScaleManagement } from "@terrascale/sdk/management";
import { requireTerraScaleAdministration } from "@terrascale/sdk/admin";
import { requireTerraScaleBetterAuth } from "@terrascale/sdk/better-auth";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";
const client = sdk.createTerraBaseClient({ tenant: "00000000-0000-0000-0000-000000000001", database: "00000000-0000-0000-0000-000000000002", shard: 1n, endpoint: "https://gru.terrascale.tech", apiKey: "constructor-only" });
for (const name of ["read", "query", "subscription", "prepareCommit", "commit", "transactionStatus"]) assert.equal(typeof client[name], "function");
for (const name of ["createTerraScaleClient", "TerraScaleSyncV1Client", "TerraScaleHttp", "toUlid"]) assert.equal(name in sdk, false);
for (const gate of [requireTerraScaleManagement, requireTerraScaleAdministration, requireTerraScaleBetterAuth]) assert.throws(gate, sdk.TerraScaleCapabilityUnavailableError);
assert.equal(typeof createTsAuthClient, "function");
assert.equal(typeof sdk.collection, "function");
assert.equal(typeof sdk.raw, "function");
await assert.rejects(import("@terrascale/sdk/database-codec"), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
`;

const installedFrameworkCaller = String.raw`import assert from "node:assert/strict";
import React from "react";
import { act, create, type ReactTestRenderer } from "react-test-renderer";
import { get } from "svelte/store";
import { createCollection } from "@tanstack/db";
import { createTerraBaseClient, createDatabaseQuery, raw, type DatabasePoint } from "@terrascale/sdk";
import { PostgresClient, PostgresError, postgresQuery, type PostgresConfig, type PostgresResult } from "@terrascale/sdk/postgres";
import { createTsAuthClient, type TsAuthOidcResourceIntrospection, type TsAuthOidcTokenRequest, type TsAuthUpdateOauthResourceRequest } from "@terrascale/sdk/ts-auth";
import { useQuery, type ReactiveQuerySource } from "@terrascale/sdk/react";
import { createQuery } from "@terrascale/sdk/svelte";
import { terrascaleCollectionOptions } from "@terrascale/sdk/tanstack";

// Hosted identity calls use only named operations. This installed caller
// captures requests and fails transport; it claims no successful provider run.
const oauthRequests: Request[] = [];
const identity = createTsAuthClient({ apiBaseUrl: "https://auth.example.test", instanceSlug: "example", accessToken: "sk_installed_request_only", fetch: async input => { oauthRequests.push(new Request(input)); throw new Error("No hosted provider configured for this installed caller."); } });
const resourceAudience = "https://api.example.test/management?tenant=a%2Fb";
const refreshGrant: TsAuthOidcTokenRequest = { grant_type: "refresh_token", refresh_token: "opaque-refresh", resource: resourceAudience, scope: "management:read" };
assert.equal((await identity.oidc.token("client", "secret", refreshGrant)).ok, false);
assert.equal((await identity.oidc.deviceAuthorization("client", "secret", { resource: resourceAudience, scope: "management:read" })).ok, false);
assert.equal((await identity.oidc.introspectResource({ issuer: "https://issuer.example.test/example", audience: resourceAudience, introspection_client_id: "resource:identity", introspection_secret: "secret +%" }, "opaque", "access_token")).ok, false);
const resourcePatch: TsAuthUpdateOauthResourceRequest = { expected_auth_epoch: 1, enabled: false, rotate_secret: true };
assert.equal((await identity.oauthResources.update("resource-id", resourcePatch)).ok, false);
assert.equal((await identity.oauthClients.update("client-id", { expected_auth_epoch: 1, allowed_resource_audiences: [resourceAudience], allowed_scopes: ["management:read"], allowed_grant_types: ["client_credentials"] })).ok, false);
assert.equal(identity.oidc.authorizationUrl({ client_id: "client", redirect_uri: "https://app.example.test/callback", response_type: "code", resource: resourceAudience }).searchParams.get("resource"), resourceAudience);
assert.equal(new URLSearchParams(await oauthRequests[0]!.text()).get("resource"), resourceAudience);
assert.equal(oauthRequests[2]!.headers.get("authorization"), "Basic " + btoa("resource%3Aidentity:secret+%2B%25"));
assert.equal(new URLSearchParams(await oauthRequests[2]!.text()).has("client_secret"), false);
const resourceShape = (value: TsAuthOidcResourceIntrospection): string | null | undefined => value.active ? value.sid : undefined;
assert.equal(typeof resourceShape, "function");

// The installed Node entrypoint accepts immutable builder descriptors and keeps
// signed INT8 values exact. Actual TLS listener execution has a separate gate.
const parameters: (bigint | string)[] = [-(1n << 63n), "installed PostgreSQL boundary"];
const descriptor = raw("SELECT key, value FROM points WHERE key = $1 AND value = $2", parameters).toDescriptor();
const bound = postgresQuery(descriptor.statement, descriptor.parameters);
parameters[0] = 0n;
assert.equal(bound.parameters[0], -(1n << 63n));
for (const value of [Number.MAX_SAFE_INTEGER + 1, true, new Uint8Array([1]), null]) assert.throws(() => postgresQuery("SELECT $1", [value]), PostgresError);
const keyId = "A".repeat(22);
const invalidCertificate: PostgresConfig = { host: "127.0.0.1", port: 5432, tlsServerName: "database.example", caCertificatePem: "invalid certificate", database: "00000000-0000-0000-0000-000000000002", keyId, password: "tbk1." + keyId + "." + "A".repeat(43) };
await assert.rejects(PostgresClient.connect(invalidCertificate), error => error instanceof PostgresError && error.kind === "configuration");
const executeDescriptor = (client: PostgresClient): Promise<PostgresResult> => client.query(descriptor);
assert.equal(typeof executeDescriptor, "function");

// This installed caller proves public assignability and framework lifecycle;
// the deliberately failing transport supplies no simulated producer responses.
const client = createTerraBaseClient({ tenant: "00000000-0000-0000-0000-000000000001", database: "00000000-0000-0000-0000-000000000002", shard: 1n, endpoint: "https://gru.terrascale.tech", apiKey: "installed-caller-only", fetch: async () => { throw new Error("No producer is configured for this package boundary caller."); } });
const collectionId = "00000000-0000-0000-0000-000000000003";
const predicate = { op: "is_missing" as const, path: [1] };
const native = createDatabaseQuery(client, { collection: collectionId, predicate });
const nativeStore = createQuery(native);
assert.equal(get(nativeStore).status, "pending");

type Row = { id: string; point: DatabasePoint };
const options = terrascaleCollectionOptions({ client, collection: collectionId, predicate, retryLimit: 0, mapRow: row => ({ id: row.document, point: row.point }), getKey: (row: Row) => row.id });
const collection = createCollection(options);
try {
  await assert.rejects(collection.preload(), /transport_unavailable/u);
  await collection.cleanup();
  assert.doesNotThrow(() => collection.startSyncImmediate());
  await assert.rejects(collection.preload(), /transport_unavailable/u);
} finally { await collection.cleanup(); options.adapter.dispose(); }
assert.throws(() => collection.startSyncImmediate(), /disposed/u);

let starts = 0;
let stops = 0;
const source: ReactiveQuerySource<string> = { subscribe: observer => { starts += 1; observer.error(new Error("controlled terminal callback")); return () => { stops += 1; }; } };
const svelte = createQuery(source);
assert.equal(get(svelte).status, "error");
assert.equal(stops, starts);
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
const View = () => React.createElement("span", null, useQuery(source).status);
let renderer: ReactTestRenderer | undefined;
try {
  await act(async () => { renderer = create(React.createElement(React.StrictMode, null, React.createElement(View))); });
  assert.equal(stops, starts);
} finally { await act(async () => renderer?.unmount()); }
assert.equal(stops, starts);
`;
