import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";
import { qualifyRuntimeDependencies } from "../scripts/npm-runtime-dependencies.js";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = promisify(execFile);

test("packed SDK independently installs its project-scoped JavaScript and declarations", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-package-"));
  try {
    const consumer = path.join(workspace, "consumer");
    await mkdir(consumer);
    const npmEntrypoint = process.env.npm_execpath;
    assert.ok(npmEntrypoint, "Run package qualification through npm test.");
    assert.equal((await run(process.execPath, [npmEntrypoint, "--version"])).stdout.trim(), "11.18.0");
    const npmRun = (/** @type {string[]} */ args, /** @type {string} */ cwd) =>
      run(process.execPath, [npmEntrypoint, ...args], { cwd, maxBuffer: 4 * 1024 * 1024 });
    await npmRun(["run", "build"], root);
    const packed = await npmRun(["pack", "--ignore-scripts", "--json", "--pack-destination", workspace], root);
    const filename = JSON.parse(packed.stdout)[0].filename;
    assert.equal(path.basename(filename), filename);
    const tarball = path.join(workspace, filename);
    const listing = (await run("tar", ["-tf", tarball])).stdout;
    for (const entry of ["src/index.js", "src/v1/terrabase-v1.js", "types/index.d.ts", "sdk-v1-route-manifest.json", "sdk-public-v1-route-manifest.json"])
      assert.ok(listing.includes(`package/${entry}\n`), entry);
    assert.doesNotMatch(listing, /^package\/(?:test|scripts|contracts|node_modules)\/|\.test\.js$/mu);
    assert.doesNotMatch(listing, /^package\/(?:src\/better-auth\.js|types\/better-auth\.d\.ts)$/mu);
    assert.doesNotMatch(listing, /^package\/(?:src|types)\/(?:local\/|tanstack\/|(?:postgres|schema|sql|database-view|database-codec|database-types)\.(?:js|d\.ts)$)/mu);
    await writeFile(path.join(consumer, "package.json"), JSON.stringify({ type: "module" }));
    await npmRun(["install", "--ignore-scripts", "--no-audit", "--no-fund", tarball, "typescript@5.9.3", "react@19.2.8"], consumer);
    const manifest = JSON.parse(await readFile(path.join(consumer, "node_modules/@terrascale/sdk/package.json"), "utf8"));
    await qualifyRuntimeDependencies(consumer, manifest.dependencies);
    assert.equal(Object.hasOwn(manifest.bin, "terrascale-local"), false);
    await writeFile(path.join(consumer, "caller.ts"), installedCaller);
    await npmRun(["exec", "--", "tsc", "--noEmit", "--strict", "--skipLibCheck", "--target", "ES2023", "--module", "NodeNext", "--moduleResolution", "NodeNext", "caller.ts"], consumer);
    await run(process.execPath, ["caller.ts"], { cwd: consumer });
    await writeFile(path.join(consumer, "shape-identity.js"), `
      import { createTerraBaseClient } from "@terrascale/sdk";
      import { verifyShapeTokenIdentity } from ${JSON.stringify(new URL("../test/support/shape-token-identity.js", import.meta.url).href)};
      await verifyShapeTokenIdentity(createTerraBaseClient, true);
    `);
    await run(process.execPath, ["shape-identity.js"], { cwd: consumer });
    await run(process.execPath, ["--conditions=browser", "--input-type=module", "--eval",
      "import assert from 'node:assert/strict'; for (const entry of ['local','better-auth','postgres','schema','sql','tanstack']) await assert.rejects(import('@terrascale/sdk/' + entry), { code: 'ERR_PACKAGE_PATH_NOT_EXPORTED' });"], { cwd: consumer });
    for (const conditions of [[], ["--conditions=workerd"]]) {
      await run(process.execPath, [...conditions, "--input-type=module", "--eval",
        "import assert from 'node:assert/strict'; for (const entry of ['better-auth','local','postgres','schema','sql','tanstack']) await assert.rejects(import('@terrascale/sdk/' + entry), { code: 'ERR_PACKAGE_PATH_NOT_EXPORTED' });"], { cwd: consumer });
    }
    const declarations = await readFile(path.join(root, "types/index.d.ts"), "utf8");
    assert.doesNotMatch(declarations, /node:sqlite|local\/|database-types|discovery\.js/u);
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

const installedCaller = `import * as react from "@terrascale/sdk/react";
import * as svelte from "@terrascale/sdk/svelte";
if ("createDatabaseQuery" in react || "createDatabaseQuery" in svelte) throw new Error("Retired binding exported.");
if (typeof react.useQuery !== "function" || typeof svelte.createQuery !== "function") throw new Error("Missing lifecycle utilities.");
import { createTerraBaseClient, createTerraBaseV1Client, TerraBaseV1Client, ExactDecimal, TerraBaseV1Error } from "@terrascale/sdk";
import { createTerraScaleManagementClient, createTerraScaleIdentityManagementClient, createTerraScaleAccountDeletionClient, createTerraScaleProjectIdentityClient, requireTerraScaleManagement } from "@terrascale/sdk/management";
import { createTerraScaleAdministrationClient, requireTerraScaleAdministration } from "@terrascale/sdk/admin";
const token = "eyJ0eXAiOiJhdCtqd3QifQ.eyJwcm9qZWN0X2lkIjoicHJvamVjdCJ9.c2lnbmF0dXJl";
const config = { project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", baseUrl: "http://127.0.0.1:18447", token };
const management: import("@terrascale/sdk/management").TerraScaleManagementClient = createTerraScaleManagementClient(config);
const administration: import("@terrascale/sdk/admin").TerraScaleAdministrationClient = createTerraScaleAdministrationClient(config);
const identity = createTerraScaleIdentityManagementClient({ apiBaseUrl: "http://127.0.0.1:18447", signInToken: token });
const deletion: import("@terrascale/sdk/management").TerraScaleAccountDeletionClient = createTerraScaleAccountDeletionClient({ apiBaseUrl: "http://127.0.0.1:18447" });
const assertion: import("@terrascale/sdk/management").TerraScaleProjectIdentityClient = createTerraScaleProjectIdentityClient({ apiBaseUrl: "http://127.0.0.1:18447", project: config.project, projectApiKey: config.project + "_" + "a".repeat(52) });
if (!deletion.accountDeletionStatus || !assertion.mintEndUserToken || !identity.getAccountMfa || !identity.lookupInvitation) throw new Error("Missing typed identity boundaries.");
if (!requireTerraScaleManagement(config).databases || !requireTerraScaleAdministration(config).databases || !identity.createProject || !management.database || !administration.document) throw new Error("Missing named management APIs.");
const client = createTerraBaseClient({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", baseUrl: "http://127.0.0.1:18447", token });
const tokenProvider: import("@terrascale/sdk").V1TokenProvider = () => token;
for (const token of [tokenProvider, async () => tokenProvider()]) {
  createTerraBaseClient({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", baseUrl: "http://127.0.0.1:18447", token });
}
if (!(client instanceof TerraBaseV1Client) || createTerraBaseClient !== createTerraBaseV1Client) throw new Error("Multiple client implementations.");
const db = client.database("main");
const collection = db.collection("users");
for (const name of ["get", "put", "create", "patch", "delete", "query", "list", "getSchema", "putSchema", "deleteSchema", "subscribeShape", "decryptShapeObject"] as const) if (typeof collection[name] !== "function") throw new Error(name);
const subscribe: (request?: { where?: import("@terrascale/sdk").V1Filter }, options?: import("@terrascale/sdk").V1ShapeSubscribeOptions) => import("@terrascale/sdk").V1ShapeSubscription = collection.subscribeShape.bind(collection);
const decrypt: (bytes: Uint8Array, position: import("@terrascale/sdk").V1SealedShapePosition) => Promise<import("@terrascale/sdk").V1ShapePage> = collection.decryptShapeObject.bind(collection);
const shapeState: (subscription: import("@terrascale/sdk").V1ShapeSubscription) => Map<string, import("@terrascale/sdk").V1ShapeDocument> = subscription => subscription.state;
if (!subscribe || !decrypt || !shapeState) throw new Error("Missing typed shape APIs.");
for (const name of ["sql", "checkSql", "explainSql", "changes"] as const) if (typeof db[name] !== "function") throw new Error(name);
for (const name of ["create", "list", "get", "update", "delete", "branch"] as const) if (typeof client.databases[name] !== "function") throw new Error(name);
const sql: (statement: string, options?: import("@terrascale/sdk").V1RequestOptions) => Promise<import("@terrascale/sdk").V1SqlResult> = db.sql.bind(db);
const schema: (value: boolean | import("@terrascale/sdk").V1JsonInputObject) => Promise<import("@terrascale/sdk").V1CollectionSchema> = collection.putSchema.bind(collection);
const preview: (value: boolean | import("@terrascale/sdk").V1JsonInputObject, request?: { cursor?: string }) => Promise<import("@terrascale/sdk").V1SchemaPreview> = collection.previewSchema.bind(collection);
const changes: (request?: import("@terrascale/sdk").V1ChangeStreamRequest) => AsyncGenerator<import("@terrascale/sdk").V1DatabaseChange | import("@terrascale/sdk").V1DatabaseChangeCursor, void, undefined> = db.changes.bind(db);
if (!sql || !schema || !preview || !changes) throw new Error("Missing typed APIs.");
const error = new TerraBaseV1Error({ status: 403, code: "forbidden_scope", body: { error: { code: "forbidden_scope", details: ["kept"] } }, responseText: "fixture", headers: new Headers({ "x-request-id": "fixture-id" }) });
const errorBody: import("@terrascale/sdk").V1JsonValue | undefined = error.body;
if (!errorBody || error.requestId !== "fixture-id" || error.responseText !== "fixture") throw new Error("Lost typed diagnostics.");
if (new ExactDecimal("9007199254740993").toString() !== "9007199254740993") throw new Error("Lost exact integer.");
if (JSON.stringify(client).includes(token)) throw new Error("Exposed credential.");
`;
