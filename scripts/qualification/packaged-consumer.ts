import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { promisify } from "node:util";

const exec = promisify(execFile);
const required = [
  "TERRASCALE_API_URL", "TERRASCALE_AUTH_URL", "TERRASCALE_PROJECT_ID",
  "TERRASCALE_DATABASE_ID", "TERRASCALE_API_KEY", "TERRASCALE_JOURNEY_RUN_ID",
  "TERRASCALE_SDK_TARBALL", "TERRASCALE_SDK_TARBALL_SHA256", "TERRASCALE_DENY_API_KEY",
] as const;
const journeyPeers = ["typescript", "go", "rust", "dotnet", "cli"] as const;
type Config = Record<(typeof required)[number], string>;
type Phase = "seed" | "verify" | "exercise" | "cleanup";

if (process.argv[1] && path.resolve(process.argv[1]) === new URL(import.meta.url).pathname) await main();

async function main() {
const phase = parsePhase(process.argv.slice(2));
const config = readConfig();
const work = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-consumer-"));
try {
  const packageTar = path.resolve(config.TERRASCALE_SDK_TARBALL);
  if (!path.isAbsolute(config.TERRASCALE_SDK_TARBALL)) throw new Error("TERRASCALE_SDK_TARBALL must be an absolute path");
  const digest = (await import("node:crypto")).createHash("sha256").update(await readFile(packageTar)).digest("hex");
  if (!/^[0-9a-f]{64}$/iu.test(config.TERRASCALE_SDK_TARBALL_SHA256) || digest !== config.TERRASCALE_SDK_TARBALL_SHA256.toLowerCase()) {
    throw new Error(`TERRASCALE_SDK_TARBALL_SHA256 mismatch (${digest})`);
  }
  const consumer = path.join(work, "consumer");
  await exec("mkdir", [consumer]);
  await writeFile(path.join(consumer, "package.json"), JSON.stringify({ private: true, type: "module", devDependencies: { typescript: "5.9.3", "@types/node": "22.15.0" } }));
  await exec("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", packageTar], { cwd: consumer });
  const journey = path.join(consumer, "journey.ts");
  await writeFile(journey, journeySource(phase));
  await exec("npx", ["tsc", "--target", "ES2023", "--module", "NodeNext", "--moduleResolution", "NodeNext", "--strict", "--skipLibCheck", "--outDir", "out", journey], { cwd: consumer });
  let result: { stdout: string };
  try {
    result = await exec(process.execPath, [path.join(consumer, "out", "journey.js")], { cwd: consumer, maxBuffer: 1024 * 1024 });
  } catch (error) {
    throw new Error(`packaged consumer failed: ${safeError(error, config.TERRASCALE_API_KEY, process.env.TERRASCALE_DENY_API_KEY)}`);
  }
  process.stdout.write(result.stdout);
} finally {
  await rm(work, { recursive: true, force: true });
}
}

function safeError(error: unknown, ...secrets: (string | undefined)[]): string {
  if (typeof error !== "object" || error === null) return "unknown failure";
  const record = error as { code?: unknown; message?: unknown; stdout?: unknown };
  const message = typeof record.message === "string" ? record.message.slice(0, 2000) : "child process failed";
  const stdout = typeof record.stdout === "string" ? record.stdout.slice(0, 2000) : undefined;
  const redact = (value: string): string => secrets.filter((secret): secret is string => Boolean(secret)).reduce((text, secret) => text.replaceAll(secret, "[redacted]"), value.replace(/(?:tsauth_[A-Za-z0-9._-]+|Bearer\s+\S+)/giu, "[redacted]"));
  return JSON.stringify({ code: typeof record.code === "string" ? record.code.slice(0, 80) : undefined, message: redact(message), stdout: typeof stdout === "string" ? redact(stdout) : undefined });
}

function parsePhase(args: string[]): Phase {
  if (args.length !== 2 || args[0] !== "--phase" || !["seed", "verify", "exercise", "cleanup"].includes(args[1]!)) {
    throw new Error("usage: node scripts/qualification/packaged-consumer.ts --phase seed|verify|exercise|cleanup");
  }
  return args[1] as Phase;
}

function readConfig(): Config {
  const values = Object.fromEntries(required.map((key) => [key, process.env[key]?.trim()])) as Partial<Config>;
  const missing = required.filter((key) => !values[key]);
  if (missing.length) throw new Error(`missing required qualification configuration: ${missing.join(", ")}`);
  for (const key of ["TERRASCALE_API_URL", "TERRASCALE_AUTH_URL"] as const) {
    let parsed: URL;
    try { parsed = new URL(values[key]!); } catch { throw new Error(`${key} must be an absolute HTTP(S) URL`); }
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") throw new Error(`${key} must be an absolute HTTP(S) URL`);
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/u.test(values.TERRASCALE_JOURNEY_RUN_ID!)) {
    throw new Error("TERRASCALE_JOURNEY_RUN_ID must match [A-Za-z0-9][A-Za-z0-9_-]{0,63}");
  }
  return values as Config;
}

export function journeySource(phase: Phase): string {
  return `import assert from "node:assert/strict";
import { createTerraScaleClient, type TerraScaleResult } from "@terrascale/sdk";
const phase: string = ${JSON.stringify(phase)};
function required(name: string): string { const value = process.env[name]?.trim(); if (!value) throw new Error("missing required environment variable " + name); return value; }
const apiUrl = required("TERRASCALE_API_URL");
const projectId = required("TERRASCALE_PROJECT_ID");
const databaseId = required("TERRASCALE_DATABASE_ID");
const apiKey = required("TERRASCALE_API_KEY");
const runId = required("TERRASCALE_JOURNEY_RUN_ID");
const denyKey = required("TERRASCALE_DENY_API_KEY");
// Credentials are inherited through the process environment and are never
// serialized into this generated consumer source.
const client = createTerraScaleClient({ apiBaseUrl: apiUrl, projectId, accessToken: apiKey, ...(process.env.TERRASCALE_SYNC_URL ? { syncBaseUrl: process.env.TERRASCALE_SYNC_URL } : {}), idempotencyKeyFactory: () => runId + "-typescript" });
const own = runId + "-typescript";
const journeyPeers = ${JSON.stringify(journeyPeers)};
const ids = journeyPeers.map((sdk) => runId + "-" + sdk);
const body = { sdk: "typescript", runId };
function ok<T>(result: TerraScaleResult<T>, label: string): T { assert.equal(result.ok, true, label + ": " + JSON.stringify(result.ok ? undefined : result.error)); if (!result.ok) throw new Error(label); return result.value; }
function observe<T>(label: string, result: TerraScaleResult<T>): void { const out = { label, ok: result.ok, status: result.status, code: result.ok ? undefined : result.error.code }; process.stdout.write(JSON.stringify(out) + "\\n"); }
if (phase === "seed") { const result = await client.documents.put(databaseId, "sdk_journey", own, body, { idempotencyKey: own + "-seed" }); observe("seed", result); ok(result, "seed"); }
if (phase === "verify") { for (const id of ids) { const result = await client.documents.get(databaseId, "sdk_journey", id); observe("verify:" + id, result); const sdk = id.slice(runId.length + 1); assert.deepEqual(ok(result, "verify:" + id), { sdk, runId }); } }
if (phase === "exercise") {
  const createKey = own + "-create";
  const createdResult = await client.documents.put(databaseId, "sdk_journey", own, body, { idempotencyKey: createKey });
  const created = ok(createdResult, "create");
  const replay = await client.documents.put(databaseId, "sdk_journey", own, body, { idempotencyKey: createKey }); observe("idempotency-replay", replay);
  const replayed = ok(replay, "idempotency-replay");
  assert.equal(createdResult.ok, true, "create must return a success receipt");
  assert.equal(replay.ok, true, "idempotency replay must return a success receipt");
  assert.equal(replayed.revision, created.revision, "idempotent replay must retain the revision");
  assert.equal(replay.headers.get("etag"), createdResult.headers.get("etag"), "idempotent replay must retain the ETag");
  const stale = await client.documents.patch(databaseId, "sdk_journey", own, { stale: true }, { ifMatch: '"stale-revision"', idempotencyKey: own + "-stale" }); observe("stale-if-match", stale); assert.equal(stale.ok, false); assert.equal(stale.status, 412);
  const sql = await client.sql.execute(databaseId, { sql: "SELECT * FROM sdk_journey WHERE id = :id", parameters: { id: own } }); observe("parameterized-sql", sql); const sqlValue = ok(sql, "parameterized-sql"); assert.ok("rows" in sqlValue); assert.ok(sqlValue.rows.some((row) => { const value = row.value; if (typeof value !== "object" || value === null || Array.isArray(value)) return false; const objectValue = value as { sdk?: unknown; runId?: unknown }; return row.id === own && objectValue.sdk === body.sdk && objectValue.runId === body.runId; }));
  const controller = new AbortController(); const live = await client.reactive.subscribe({ databaseId, subscription: own, query: "SELECT * FROM sdk_journey", parameters: new Uint8Array(), autoStart: false }); process.stdout.write(JSON.stringify({ label: "reactive-attach", ok: true, status: 200 }) + "\\n");
  try {
    const attachedCheckpoint = live.checkpoint(); assert.ok(attachedCheckpoint, "reactive attach must provide an opaque checkpoint");
    const changed = await client.documents.patch(databaseId, "sdk_journey", own, { sdk: "typescript", runId, changed: true }, { ifMatch: created.revision, idempotencyKey: own + "-update" }); observe("reactive-write", changed); ok(changed, "reactive-write");
    const frame = await live.poll(AbortSignal.timeout(30_000)); assert.ok(frame, "reactive poll must return an update frame"); const current = live.view.getItems().find((item) => item.identity === own); assert.ok(current, "the reactive view must carry the updated row"); const decode = Object.fromEntries(Object.entries(current.value).map(([field, bytes]) => [field, JSON.parse(new TextDecoder().decode(bytes))])); assert.deepEqual(decode, { sdk: "typescript", runId, changed: true });
    const firstCheckpoint = live.checkpoint(); assert.ok(firstCheckpoint, "reactive update must retain an opaque checkpoint"); assert.notDeepEqual(firstCheckpoint.toBytes(), attachedCheckpoint.toBytes(), "reactive update must advance the checkpoint");
    await live.reconnect(); assert.deepEqual(live.checkpoint()?.toBytes(), firstCheckpoint.toBytes(), "reconnect must resume from the last checkpoint");
    const changedAgain = await client.documents.patch(databaseId, "sdk_journey", own, { sdk: "typescript", runId, changed: true, reconnected: true }, { ifMatch: changed.value.revision, idempotencyKey: own + "-reconnect-update" }); observe("reactive-reconnect-write", changedAgain); ok(changedAgain, "reactive-reconnect-write");
    const resumed = await live.poll(AbortSignal.timeout(30_000)); assert.ok(resumed, "reactive poll after reconnect must return an update frame"); const resumedCheckpoint = live.checkpoint(); assert.ok(resumedCheckpoint, "reactive reconnect update must retain an opaque checkpoint"); assert.notDeepEqual(resumedCheckpoint.toBytes(), firstCheckpoint.toBytes(), "reactive reconnect update must advance the checkpoint");
  } finally { live.close(); controller.abort(); }
  const cancelledController = new AbortController(); cancelledController.abort(); const cancelled = await client.documents.get(databaseId, "sdk_journey", own, { signal: cancelledController.signal }); observe("cancellation", cancelled); assert.equal(cancelled.ok, false); if (cancelled.ok) throw new Error("pre-aborted request unexpectedly succeeded"); assert.equal(cancelled.error.code, "request_cancelled");
  const missing = await client.documents.get(databaseId, "sdk_journey", own + "-missing"); observe("missing-document", missing); assert.equal(missing.ok, false); assert.equal(missing.status, 404);
  assert.notEqual(denyKey, apiKey, "TERRASCALE_DENY_API_KEY must differ from the authorized key");
  const denied = createTerraScaleClient({ apiBaseUrl: apiUrl, projectId, accessToken: denyKey });
  const deniedRead = await denied.documents.get(databaseId, "sdk_journey", own);
  observe("denied-scope", deniedRead);
  assert.equal(deniedRead.ok, false, "denied API key unexpectedly read document");
  if (deniedRead.ok) throw new Error("denied API key unexpectedly read document");
  assert.ok(deniedRead.status === 401 || deniedRead.status === 403, "denied scope must return 401/403");
  assert.equal(deniedRead.error.code, deniedRead.status === 401 ? "unauthorized" : "forbidden", "denied scope must preserve the producer error code");
}
if (phase === "cleanup") { const result = await client.documents.delete(databaseId, "sdk_journey", own, { idempotencyKey: own + "-cleanup" }); observe("cleanup", result); ok(result, "cleanup"); }
`;
}
