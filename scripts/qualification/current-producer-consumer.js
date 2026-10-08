#!/usr/bin/env node
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import path from "node:path";
import { journeyConfiguration } from "./journey-configuration.js";

/**
 * @returns {{ tarball: string; digest: string; routing: "atlas" | "direct" }}
 */
export function qualificationConfiguration() {
  const configuration = journeyConfiguration(process.env, "native");
  if (configuration === undefined) {
    throw new Error("Missing real allocation and artifact; real producer qualification is incomplete.");
  }
  return {
    tarball: path.resolve(configuration.inputs.TERRASCALE_SDK_TARBALL),
    digest: configuration.inputs.TERRASCALE_SDK_TARBALL_SHA256,
    routing: configuration.routing,
  };
}

const consumerSource = String.raw`
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { createTerraBaseClient, generateId } from "@terrascale/sdk";

const env = process.env;
const scope = { tenant: env.TERRABASE_TENANT, database: env.TERRABASE_DATABASE, shard: BigInt(env.TERRABASE_SHARD) };
const config = { ...scope, apiKey: env.TERRABASE_API_KEY, ...(env.TERRABASE_ENDPOINT === undefined ? {} : { endpoint: env.TERRABASE_ENDPOINT }) };
const client = createTerraBaseClient(config);
const collection = env.TERRABASE_COLLECTION;
const document = generateId();
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


const atlasConsumerSource = String.raw`
import assert from "node:assert/strict";
import { createHash, randomBytes } from "node:crypto";
import { createTerraBaseClient, generateId } from "@terrascale/sdk";

const env = process.env;
const scope = { tenant: env.TERRABASE_TENANT, database: env.TERRABASE_DATABASE, shard: BigInt(env.TERRABASE_SHARD) };
const nativeOrigin = "https://" + env.TERRABASE_EXPECTED_REGION.toLowerCase() + ".api.terrascale.tech";
let atlasCalls = 0, nativeCalls = 0;
const trace = async (input, init) => {
  assert.ok(input instanceof Request, "the public SDK owns each typed request");
  const origin = new URL(input.url).origin;
  assert.ok(origin === "https://atlas.terrascale.tech" || origin === nativeOrigin, "actual receiver-ranked native origin");
  assert.equal(input.redirect, "manual");
  assert.equal(input.credentials, "omit");
  if (origin === nativeOrigin && nativeCalls === 0) {
    assert.equal(input.method, "GET", "first regional operation is a document read");
    assert.equal(new URL(input.url).pathname, "/v1/tenants/" + scope.tenant + "/databases/" + scope.database + "/shards/" + scope.shard + "/collections/" + collection + "/documents/" + document);
  }
  if (origin === "https://atlas.terrascale.tech") atlasCalls++; else nativeCalls++;
  const result = await fetch(input, init);
  if (origin === "https://atlas.terrascale.tech" && result.status === 200) {
    const bytes = Buffer.from(await result.clone().arrayBuffer());
    const routing = JSON.parse(bytes.toString("utf8"));
    assert.deepEqual(routing.scope, { tenant_id: scope.tenant, database_id: scope.database, shard_id: scope.shard.toString() });
    assert.equal(routing.schema_version, 1);
    const selected = [...routing.endpoints].sort((a, b) => a.priority - b.priority).find(endpoint => endpoint.capabilities.client_v1 && endpoint.capabilities.read && endpoint.capabilities.write && endpoint.capabilities.query && endpoint.capabilities.subscriptions);
    assert.ok(selected, "admitted HTTP read/write/query/subscription endpoint");
    assert.equal(selected.url, nativeOrigin, "closest eligible region independently supplied by allocation");
    assert.equal(selected.capabilities.conditional_mutation, false);
    assert.equal(selected.capabilities.atomic_groups, false);
    assert.equal(selected.capabilities.security_state, false);
    assert.equal(selected.security_writer, null);
    process.stdout.write(JSON.stringify({ label: "atlas-readback", status: result.status, response_sha256: createHash("sha256").update(bytes).digest("hex"), authority: routing.authority, revision: routing.revision, published_at: routing.published_at, expires_at: routing.expires_at, refreshSeconds: routing.refreshSeconds, selected_origin: selected.url }) + "\n");
  }
  return result;
};
const config = { ...scope, apiKey: env.TERRABASE_API_KEY, fetch: trace };
const client = createTerraBaseClient(config);
assert.equal(atlasCalls + nativeCalls, 0, "construction does not dispatch or prove readiness");
const collection = env.TERRABASE_COLLECTION, document = generateId();
const fieldId = Number(env.TERRABASE_TEST_FIELD_ID);
assert.ok(Number.isInteger(fieldId) && fieldId > 0);
const value = env.TERRABASE_TEST_FIELD_TYPE === "i64" ? { type: "i64", value: -9223372036854775808n } : { type: "utf8", value: "atlas-package-" + document };
const binding = { collection, document, schema_generation: BigInt(env.TERRABASE_SCHEMA_GENERATION), schema_digest: env.TERRABASE_SCHEMA_DIGEST };
const authority = { mode: "leaderless" };
const identity = () => ({ expires_at_unix_ms: BigInt(Date.now() + 10 * 60 * 1000), nonce: randomBytes(16).toString("hex") });
const mutation = (condition, observed, value) => ({ transaction_id: identity(), authority, writes: [{ ...binding, condition, observed, value }] });
const observe = (label, result) => {
  process.stdout.write(JSON.stringify({ label, ok: result.ok, ...(result.ok ? { status: result.status } : { code: result.error.code, status: result.error.status, mutation_outcome: result.error.mutation_outcome }) }) + "\n");
  return result;
};
const success = (label, result) => { observe(label, result); assert.equal(result.ok, true, label); return result.value; };
let pending;
const reportPending = () => {
  if (pending !== undefined) process.stdout.write(JSON.stringify({ label: "unresolved-operation", document, transaction_id: { expires_at_unix_ms: pending.transaction_id.expires_at_unix_ms.toString(), nonce: pending.transaction_id.nonce }, effect_digest: pending.effect_digest, allocation: env.TERRASCALE_QUALIFICATION_ALLOCATION_ID }) + "\n");
};
const commit = async (label, request) => {
  const prepared = await client.prepareCommit(request);
  pending = prepared;
  const result = await client.commit(prepared);
  if (!result.ok) {
    observe(label, result);
    let resolved;
    try { resolved = success(label + "-resolution", await client.transactionStatus(prepared)); }
    catch (error) { reportPending(); throw error; }
    if (resolved.state === "committed") { pending = undefined; return { prepared, receipt: resolved.outcome.receipt }; }
    if (resolved.state === "rejected") pending = undefined;
    reportPending();
    throw new Error("Native mutation failed or remains unresolved; retain the allocation and operation identity.");
  }
  pending = undefined;
  return { prepared, receipt: success(label, result) };
};
try {
  assert.equal(success("initial-read", await client.read(collection, document)).state, "absent");
  const created = await commit("unconditional-create", mutation({ kind: "none" }, [], { kind: "live", fields: [{ id: fieldId, value }] }));
  assert.deepEqual(success("exact-retry", await client.commit(created.prepared)), created.receipt);
  const retained = success("committed-status", await client.transactionStatus(created.prepared));
  assert.equal(retained.state, "committed"); assert.deepEqual(retained.outcome.receipt, created.receipt);
  const point = success("read-value", await client.read(collection, document));
  assert.equal(point.state, "value"); assert.deepEqual(point.siblings[0].fields, [{ id: fieldId, value }]);
  const predicate = { op: "equal", path: [fieldId], value };
  assert.ok(success("query", await client.query(collection, { predicate, limit: 128 })).rows.some(row => row.document === document));
  let snapshot = success("subscription-snapshot", await client.subscription(collection, { phase: "snapshot", predicate, limit: 128 }));
  let pages = 0;
  while ("next_snapshot_cursor" in snapshot) {
    assert.ok(++pages < 20);
    snapshot = success("subscription-page", await client.subscription(collection, { phase: "snapshot", predicate, limit: 128, snapshot_cursor: snapshot.next_snapshot_cursor }));
  }
  const beforeAdvanced = nativeCalls;
  const conditional = await client.prepareCommit(mutation({ kind: "version", version: point.state_version }, point.context, { kind: "tombstone" }));
  const deniedConditional = observe("conditional-capability-closed", await client.commit(conditional));
  assert.equal(deniedConditional.ok, false); assert.equal(deniedConditional.error.code, "capability_unavailable"); assert.equal(deniedConditional.error.mutation_outcome, "unknown");
  const groupRequest = mutation({ kind: "none" }, point.context, { kind: "tombstone" });
  const groupWrites = [...groupRequest.writes, { ...groupRequest.writes[0], document: generateId() }].sort((a, b) => a.document < b.document ? -1 : 1);
  const group = await client.prepareCommit({ ...groupRequest, writes: groupWrites });
  const deniedGroup = observe("atomic-group-capability-closed", await client.commit(group));
  assert.equal(deniedGroup.ok, false); assert.equal(deniedGroup.error.code, "capability_unavailable");
  assert.equal(nativeCalls, beforeAdvanced, "unsupported capabilities do no native dispatch");
  const denied = observe("denied-scope", await createTerraBaseClient({ ...config, apiKey: env.TERRABASE_DENY_API_KEY }).read(collection, document));
  assert.equal(denied.ok, false); assert.ok(["discovery_forbidden", "invalid_key", "forbidden_scope"].includes(denied.error.code));
  await commit("unconditional-delete", mutation({ kind: "none" }, point.context, { kind: "tombstone" }));
  const poll = success("subscription-poll", await client.subscription(collection, { phase: "poll", predicate, poll_cursor: snapshot.poll_cursor }));
  assert.equal(poll.phase, "poll");
  assert.ok(poll.groups.some(group => group.effects.some(effect => effect.document === document && effect.point === null)));
  const unknown = success("unknown-status", await client.transactionStatus({ transaction_id: identity(), authority, effect_digest: created.prepared.effect_digest }));
  assert.equal(unknown.state, "unknown"); assert.equal(unknown.outcome, null);
} finally {
  if (pending !== undefined) {
    reportPending();
    throw new Error("Pending native effect prevents cleanup; reconcile the retained operation before releasing this allocation.");
  }
  let final = success("cleanup-read", await client.read(collection, document));
  if (final.state === "value" || final.state === "conflict") {
    await commit("failure-cleanup", mutation({ kind: "none" }, final.context, { kind: "tombstone" }));
    final = success("cleanup-readback", await client.read(collection, document));
  }
  assert.ok(final.state === "deleted" || final.state === "absent");
  process.stdout.write(JSON.stringify({ label: "cleanup", document, outcome: final.state, retained_tombstone: final.state === "deleted", allocation: env.TERRASCALE_QUALIFICATION_ALLOCATION_ID }) + "\n");
}
assert.ok(atlasCalls > 0 && nativeCalls > 0);
process.stdout.write(JSON.stringify({ label: "hosted-native-atlas", atlas_calls: atlasCalls, native_calls: nativeCalls, native_origin: nativeOrigin, receiver_contract_sha256: "7cd7d037772cd79d662e1117912673e8fd6fe684286099e5b7f3271358526547", advanced_capabilities: "withheld" }) + "\n");
`;

/** @returns {Promise<void>} */
export async function qualifyCurrentProducer() {
  const config = qualificationConfiguration();
  const artifact = await readFile(config.tarball);
  const digest = createHash("sha256").update(artifact).digest("hex");
  if (digest !== config.digest) throw new Error("SDK artifact digest mismatch.");
  const consumer = await mkdtemp(path.join(tmpdir(), "terrascale-typescript-current-consumer-"));
  try {
    await writeFile(path.join(consumer, "package.json"), JSON.stringify({ private: true, type: "module", dependencies: { "@terrascale/sdk": `file:${config.tarball}` } }));
    await writeFile(path.join(consumer, "journey.mjs"), config.routing === "atlas" ? atlasConsumerSource : consumerSource);
    const installation = spawnSync("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", "--cache", path.join(consumer, ".npm-cache")], { cwd: consumer, encoding: "utf8", timeout: 120000 });
    if (installation.status !== 0) throw new Error("Independent package installation failed.");
    const run = spawnSync(process.execPath, ["journey.mjs"], { cwd: consumer, encoding: "utf8", timeout: 120000, env: process.env });
    process.stdout.write(run.stdout ?? "");
    if (run.status !== 0) throw new Error("Real packaged consumer journey failed; retain the redacted observations and producer allocation for reconciliation.");
    process.stdout.write(JSON.stringify({ label: "artifact", sha256: digest, package_consumer: "independent-install", producer: "configured-real-service", routing: config.routing, scope: config.routing === "atlas" ? "admitted-http-discovery-operations-only" : "provisioned-local-native-exclusion-atomic-conformance" }) + "\n");
  } finally { await rm(consumer, { recursive: true, force: true }); }
}

if (process.argv[1] !== undefined && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname)) {
  qualifyCurrentProducer().catch(error => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
}
