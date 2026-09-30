#!/usr/bin/env node
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import path from "node:path";

function required(name) {
  const value = process.env[name];
  if (typeof value !== "string" || value.length === 0) throw new Error(`Missing ${name}; real producer qualification is incomplete.`);
  return value;
}

export function qualificationConfiguration() {
  const tarball = path.resolve(required("TERRASCALE_SDK_TARBALL"));
  const digest = required("TERRASCALE_SDK_TARBALL_SHA256");
  if (!/^[0-9a-f]{64}$/u.test(digest)) throw new Error("Artifact SHA-256 is not canonical.");
  for (const name of ["TERRABASE_ENDPOINT", "TERRABASE_TENANT", "TERRABASE_DATABASE", "TERRABASE_COLLECTION", "TERRABASE_SHARD", "TERRABASE_SCHEMA_GENERATION", "TERRABASE_SCHEMA_DIGEST", "TERRABASE_API_KEY", "TERRABASE_DENY_API_KEY", "TERRABASE_TEST_FIELD_ID", "TERRABASE_TEST_FIELD_TYPE"]) required(name);
  if (process.env.TERRABASE_API_KEY === process.env.TERRABASE_DENY_API_KEY) throw new Error("Denied-scope credential must differ from the allowed credential.");
  if (!["utf8", "i64"].includes(process.env.TERRABASE_TEST_FIELD_TYPE)) throw new Error("Qualification schema must admit an exact utf8 or i64 field.");
  if (process.env.TERRABASE_ENDPOINT !== undefined) {
    const endpoint = new URL(process.env.TERRABASE_ENDPOINT);
    const hostname = endpoint.hostname.replace(/\.+$/u, "");
    if (hostname === "api.terrascale.tech" || hostname.endsWith(".api.terrascale.tech")) throw new Error("The test API host is forbidden.");
  }
  return { tarball, digest };
}

const consumerSource = String.raw`
import assert from "node:assert/strict";
import { randomBytes, randomUUID } from "node:crypto";
import { createTerraBaseClient } from "@terrascale/sdk";

const env = process.env;
const scope = { tenant: env.TERRABASE_TENANT, database: env.TERRABASE_DATABASE, shard: BigInt(env.TERRABASE_SHARD) };
const config = { ...scope, apiKey: env.TERRABASE_API_KEY, ...(env.TERRABASE_ENDPOINT === undefined ? {} : { endpoint: env.TERRABASE_ENDPOINT }) };
const client = createTerraBaseClient(config);
const collection = env.TERRABASE_COLLECTION;
const document = randomUUID();
const fieldId = Number(env.TERRABASE_TEST_FIELD_ID);
assert.ok(Number.isInteger(fieldId) && fieldId > 0);
const makeValue = (suffix) => env.TERRABASE_TEST_FIELD_TYPE === "i64"
  ? { type: "i64", value: suffix === "initial" ? -9223372036854775808n : 9223372036854775807n }
  : { type: "utf8", value: "sdk-package-" + document + "-" + suffix };
const initialValue = makeValue("initial");
const updatedValue = makeValue("updated");
const binding = { collection, document, schema_generation: BigInt(env.TERRABASE_SCHEMA_GENERATION), schema_digest: env.TERRABASE_SCHEMA_DIGEST };
const authority = { mode: "leaderless" };
const identity = () => ({ expires_at_unix_ms: BigInt(Date.now() + 10 * 60 * 1000), nonce: randomBytes(16).toString("hex") });
const request = (condition, observed, value, transaction_id = identity()) => ({ transaction_id, authority, writes: [{ ...binding, condition, observed, value }] });
const live = (value) => ({ kind: "live", fields: [{ id: fieldId, value }] });
const observe = (label, result) => {
  process.stdout.write(JSON.stringify({ label, ok: result.ok, ...(result.ok ? { status: result.status } : { code: result.error.code, status: result.error.status, mutation_outcome: result.error.mutation_outcome }) }) + "\n");
  return result;
};
const successful = (label, result) => { observe(label, result); assert.equal(result.ok, true, label); return result.value; };
const rejected = (label, result, code) => { observe(label, result); assert.equal(result.ok, false, label); assert.equal(result.error.code, code, label); };
const read = async () => successful("read", await client.read(collection, document));
let cleanupVerified = false;
try {
  assert.equal((await read()).state, "absent");
  const createRequest = request({ kind: "absent" }, [], live(initialValue));
  const prepared = await client.prepareCommit(createRequest);
  const created = successful("create", await client.commit(prepared));
  const replayed = successful("exact-retry", await client.commit(prepared));
  assert.deepEqual(replayed, created);
  const changed = await client.prepareCommit({ ...createRequest, writes: [{ ...createRequest.writes[0], value: live(updatedValue) }] });
  rejected("changed-effect", await client.commit(changed), "idempotency_conflict");
  const durable = successful("committed-status", await client.transactionStatus(prepared));
  assert.equal(durable.state, "committed");
  assert.deepEqual(durable.outcome.receipt, created);
  let point = await read();
  assert.equal(point.state, "value");
  assert.deepEqual(point.siblings[0].fields, live(initialValue).fields);
  const stale = await client.prepareCommit(request({ kind: "version", version: "00".repeat(32) }, point.context, live(updatedValue)));
  rejected("stale-version", await client.commit(stale), "precondition_failed");
  const rejectedStatus = successful("rejected-status", await client.transactionStatus(stale));
  assert.equal(rejectedStatus.state, "rejected");
  const updated = await client.prepareCommit(request({ kind: "version", version: point.state_version }, point.context, live(updatedValue)));
  successful("version-update", await client.commit(updated));
  const previousVersion = point.state_version;
  point = await read();
  assert.notEqual(point.state_version, previousVersion);
  assert.deepEqual(point.siblings[0].fields, live(updatedValue).fields);
  const predicate = { op: "equal", path: [fieldId], value: updatedValue };
  const page = successful("query", await client.query(collection, { predicate, limit: 128 }));
  assert.ok(page.rows.some(row => row.document === document && row.point.state === "value"));
  let snapshot = successful("subscription-snapshot", await client.subscription(collection, { phase: "snapshot", predicate, limit: 128 }));
  let pages = 0;
  while ("next_snapshot_cursor" in snapshot) {
    assert.ok(++pages < 20, "bounded subscription bootstrap");
    snapshot = successful("subscription-snapshot-page", await client.subscription(collection, { phase: "snapshot", predicate, limit: 128, snapshot_cursor: snapshot.next_snapshot_cursor }));
  }
  assert.ok("poll_cursor" in snapshot);
  const deleted = await client.prepareCommit(request({ kind: "version", version: point.state_version }, point.context, { kind: "tombstone" }));
  successful("version-delete", await client.commit(deleted));
  const poll = successful("subscription-poll", await client.subscription(collection, { phase: "poll", predicate, poll_cursor: snapshot.poll_cursor }));
  assert.equal(poll.phase, "poll");
  assert.ok(poll.groups.some(group => group.effects.some(effect => effect.document === document && effect.point === null)));
  point = await read();
  assert.equal(point.state, "deleted");
  assert.ok(point.state_version);
  assert.notEqual(point.state_version, previousVersion);
  const recreateAbsent = await client.prepareCommit(request({ kind: "absent" }, [], live(initialValue)));
  rejected("deleted-is-not-absent", await client.commit(recreateAbsent), "precondition_failed");
  const denied = await createTerraBaseClient({ ...config, apiKey: env.TERRABASE_DENY_API_KEY }).read(collection, document);
  observe("denied-scope", denied);
  assert.equal(denied.ok, false);
  assert.ok(["invalid_key", "forbidden_scope"].includes(denied.error.code));
  const unknown = successful("unknown-status", await client.transactionStatus({ transaction_id: identity(), authority, effect_digest: prepared.effect_digest }));
  assert.equal(unknown.state, "unknown");
  assert.equal(unknown.outcome, null);
  cleanupVerified = true;
} finally {
  if (!cleanupVerified) {
    const point = await client.read(collection, document);
    if (point.ok && point.value.state === "value") {
      const cleanup = await client.prepareCommit(request({ kind: "version", version: point.value.state_version }, point.value.context, { kind: "tombstone" }));
      successful("failure-cleanup", await client.commit(cleanup));
    }
  }
  const final = successful("cleanup-readback", await client.read(collection, document));
  assert.ok(final.state === "deleted" || final.state === "absent");
  process.stdout.write(JSON.stringify({ label: "cleanup", document, outcome: final.state, retained_tombstone: final.state === "deleted" }) + "\n");
}
`;

export async function qualifyCurrentProducer() {
  const config = qualificationConfiguration();
  const artifact = await readFile(config.tarball);
  const digest = createHash("sha256").update(artifact).digest("hex");
  if (digest !== config.digest) throw new Error("SDK artifact digest mismatch.");
  const consumer = await mkdtemp(path.join(tmpdir(), "terrascale-typescript-current-consumer-"));
  try {
    await writeFile(path.join(consumer, "package.json"), JSON.stringify({ private: true, type: "module", dependencies: { "@terrascale/sdk": `file:${config.tarball}` } }));
    await writeFile(path.join(consumer, "journey.mjs"), consumerSource);
    const installation = spawnSync("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", "--cache", path.join(consumer, ".npm-cache")], { cwd: consumer, encoding: "utf8", timeout: 120000 });
    if (installation.status !== 0) throw new Error("Independent package installation failed.");
    const run = spawnSync(process.execPath, ["journey.mjs"], { cwd: consumer, encoding: "utf8", timeout: 120000, env: process.env });
    process.stdout.write(run.stdout ?? "");
    if (run.status !== 0) throw new Error("Real packaged consumer journey failed; retain the redacted observations and producer allocation for reconciliation.");
    process.stdout.write(JSON.stringify({ label: "artifact", sha256: digest, package_consumer: "independent-install", producer: "configured-real-service" }) + "\n");
  } finally { await rm(consumer, { recursive: true, force: true }); }
}

if (process.argv[1] !== undefined && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname)) {
  qualifyCurrentProducer().catch(error => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
}
