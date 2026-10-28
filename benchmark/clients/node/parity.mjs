// Node SDK versus raw HTTPS parity on an owned synthetic tenant; see README.md.
import { closeSync, existsSync, openSync, readFileSync, readdirSync, writeSync } from "node:fs";
import { createHash } from "node:crypto";
import { subscribe } from "node:diagnostics_channel";
import { hostname } from "node:os";
import { dirname, extname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { Agent, getGlobalDispatcher, setGlobalDispatcher } from "undici";
import { createTerraBaseV1Client, generateId, isValidId, V1NotFoundError } from "@terrascale/sdk";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";

const ORIGIN = "https://api.terrascale.tech";
const HOST = "api.terrascale.tech";
// The Python runner's default issuer; ts-auth is never region-pinned.
const AUTH_ORIGIN = "https://auth.terrascale.tech";
const AUTH_SLUG = "terrascale";
const SIZES = [256, 4096];
const CONCURRENCY = [1, 16];
const LANES = ["raw-h1", "sdk-h1", "raw-h2", "sdk-h2"];
const PROTOCOLS = { "raw-h1": "HTTP/1.1", "sdk-h1": "HTTP/1.1", "raw-h2": "HTTP/2", "sdk-h2": "HTTP/2" };
const COLLECTION = "bench";
const PIN_HEADER = "X-TerraScale-Region-Pin";
const REGION_HEADER = "x-terrabase-region";
const RESERVOIR = 16_384;
const API_KEY = /^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$/u;
const WARMUP_S = 2;
const MEASURE_S = 5;
const DRAIN_S = 30;
const READY_S = 60;
const READY_OP_S = 10;
const OP_S = 60;
const CLEANUP_S = 30;
const RUN_S = 1800;

class Rejected extends Error {}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const round3 = (value) => Math.round(value * 1000) / 1000;
const pct = (ordered, q) => (ordered.length ? round3(ordered[Math.ceil(q * ordered.length) - 1]) : null);

/** Resolves true when the promise settles first; the timer never keeps the process alive. */
function within(promise, ms) {
  let timer;
  const deadline = new Promise((resolve) => { timer = setTimeout(() => resolve(false), ms); });
  return Promise.race([promise.then(() => true), deadline]).finally(() => clearTimeout(timer));
}

// Cells run one at a time, so one observer is live at a time; undici publishes these process-wide.
let active = null;
export function setActive(observer) { active = observer; }
subscribe("undici:client:connected", ({ connectParams }) => {
  if (active && connectParams.hostname === HOST && Number(connectParams.port || 443) === 443) active.connects += 1;
});
subscribe("undici:client:sendHeaders", ({ request, socket }) => {
  const observer = active;
  if (!observer?.measured || !observer.pinned(new URL(request.path, String(request.origin)), request.method)) return;
  const version = socket?.alpnProtocol === "h2" ? "HTTP/2" : "HTTP/1.1";
  observer.sent += 1;
  observer.versions[version] = (observer.versions[version] ?? 0) + 1;
  if (observer.protocol !== undefined && version !== observer.protocol) observer.protocolBad += 1;
});

/** Counts every pinned attempt, including SDK retries, and validates each response. */
export class Observer {
  connects = 0; // TCP connects to the API host, readiness included.
  constructor(routes, region, protocol = undefined, measured = true) {
    Object.assign(this, { routes, region, protocol, measured });
    this.window();
  }
  window() {
    this.attempts = this.good = this.sent = this.protocolBad = 0;
    this.versions = {};
  }
  pinned(url, method) {
    return url.protocol === "https:" && url.hostname === HOST && (url.port === "" || url.port === "443")
      && this.routes.has(`${method} ${url.pathname}`);
  }
  /** The fetch given to the SDK's public `fetch` option and used by raw lanes; pins exact routes only. */
  observe = async (input, init = {}) => {
    const url = new URL(String(input));
    if (!this.pinned(url, init.method ?? "GET")) return fetch(url, init);
    const headers = new Headers(init.headers);
    headers.set(PIN_HEADER, this.region);
    if (this.measured) this.attempts += 1;
    const response = await fetch(url, { ...init, headers });
    if (this.measured && response.status === 200 && response.headers.get(REGION_HEADER) === this.region) this.good += 1;
    return response;
  };
}

/** One API-key exchange per lane, cached and refreshed a minute early as in the Python token source. */
function tokenSource(key, project) {
  const auth = createTsAuthClient({ apiBaseUrl: AUTH_ORIGIN, instanceSlug: AUTH_SLUG });
  let cached;
  let pending;
  const exchange = async () => {
    const started = Date.now();
    const result = await auth.oidc.token(key.slice(0, 26), key, { grant_type: "client_credentials", project });
    const lifetime = result.ok ? result.value.expires_in : NaN;
    if (!result.ok || !Number.isFinite(lifetime) || lifetime <= 0) throw new Error("token exchange failed");
    cached = { token: result.value.access_token, refreshAt: started + (lifetime - Math.min(60, lifetime / 2)) * 1000 };
    return cached.token;
  };
  return async () => {
    if (cached && Date.now() < cached.refreshAt) return cached.token;
    pending ??= exchange().finally(() => { pending = undefined; });
    return pending;
  };
}

export function check(seed, raw) {
  if (raw?.state !== "value" || raw.id !== seed.id || raw.version !== seed.version
    || JSON.stringify(raw.document) !== JSON.stringify(seed.body)) {
    throw new Error("document identity, version, state or body differs");
  }
}

async function readyOp(op, seed, run) {
  const deadline = Date.now() + READY_S * 1000;
  for (;;) {
    try {
      await op(seed, AbortSignal.any([run, AbortSignal.timeout(READY_OP_S * 1000)]));
      return true;
    } catch {
      run.throwIfAborted(); // A new seed may not have reached this region yet.
    }
    if (Date.now() >= deadline) return false;
    await sleep(100);
  }
}

async function measure(op, seed, concurrency, run) {
  const stats = { latencies: [], started: 0, ok: 0, failed: 0, stuck: false };
  const from = performance.now() + WARMUP_S * 1000;
  const until = from + MEASURE_S * 1000;
  const cancel = new AbortController();
  const stop = AbortSignal.any([run, cancel.signal]);
  const worker = async () => {
    while (performance.now() < until && stats.failed === 0) {
      const began = performance.now();
      stats.started += 1;
      try {
        await op(seed, stop);
      } catch {
        if (!stop.aborted) stats.failed += 1; // Cancellation is not an operation failure.
        return;
      }
      const done = performance.now();
      if (began >= from && done <= until) {
        stats.ok += 1;
        const slot = stats.ok <= RESERVOIR ? stats.ok - 1 : Math.floor(Math.random() * stats.ok);
        if (slot < RESERVOIR) stats.latencies[slot] = done - began;
      }
    }
  };
  const tasks = Promise.all(Array.from({ length: concurrency }, worker));
  stats.stuck = !(await within(tasks, (WARMUP_S + MEASURE_S + DRAIN_S) * 1000));
  cancel.abort();
  await within(tasks, DRAIN_S * 1000);
  return stats;
}

function credentials(path) {
  let body;
  try {
    body = JSON.parse(readFileSync(path, "utf8"));
  } catch {
    throw new Rejected("credentials could not be loaded");
  }
  const { api_key: key, project } = body ?? {};
  if (typeof key !== "string" || !API_KEY.test(key) || !isValidId(project)) throw new Rejected("credentials could not be loaded");
  return { key, project };
}

export function discoverRegions() {
  let lines;
  try { lines = readFileSync(process.env.TS_PEERS_FILE, "utf8").split("\n"); }
  catch { throw new Rejected("TS_PEERS_FILE could not be read"); }
  const regions = new Set();
  for (const line of lines) {
    const parts = line.trim().split(/\s+/u);
    if (!line.trim() || line.trim().startsWith("#")) continue;
    if (parts.length !== 3 || !/^[a-z][a-z0-9-]*$/u.test(parts[1])) throw new Rejected("peer catalog is malformed");
    regions.add(parts[1]);
  }
  if (["gru", "sjc", "fra"].some(region => !regions.has(region))) throw new Rejected("a required serving region is absent");
  return [...regions].sort();
}

/** The installed package root, found from its resolved entry; logs keep no paths. */
function packageRoot(specifier) {
  let dir = dirname(fileURLToPath(import.meta.resolve(specifier)));
  while (!existsSync(join(dir, "package.json")) && dirname(dir) !== dir) dir = dirname(dir);
  return dir;
}

const readVersion = (root) => JSON.parse(readFileSync(join(root, "package.json"), "utf8")).version;

function sdkSourceDigest(root) {
  const digest = createHash("sha256");
  for (const file of readdirSync(join(root, "src"), { recursive: true }).filter((name) => extname(name) === ".js").sort()) {
    digest.update(`${file}\0`).update(readFileSync(join(root, "src", file))).update("\0");
  }
  return digest.digest("hex");
}

export function cellRejection(ready, stats, observer) {
  if (!ready) return "readiness";
  if (stats.failed) return "operation_error";
  if (stats.stuck) return "drain_timeout";
  if (observer.attempts !== stats.started || observer.attempts !== observer.good || observer.sent !== observer.attempts || observer.protocolBad) return "attempt_mismatch";
  return stats.latencies.length ? null : "empty";
}

export class Tenant {
  seeds = new Map();
  constructor({ key, project, db, out, run }) {
    Object.assign(this, { key, project, db, out, run });
    this.dbPath = `/v1/projects/${project}/databases/${db}`;
    this.docPath = `${this.dbPath}/collections/${COLLECTION}/documents/by-key`;
    this.setupRoutes = new Set([`POST /v1/projects/${project}/databases`, `DELETE ${this.dbPath}`,
      `POST ${this.dbPath}/collections`, `PUT ${this.docPath}`]);
  }
  emit(row) {
    writeSync(this.out, `${JSON.stringify(row)}\n`);
  }
  /** The SDK reads through the observed fetch from its public `fetch` option; its transport is unchanged. */
  laneOp(lane, observer) {
    const token = tokenSource(this.key, this.project);
    if (lane.startsWith("sdk-")) {
      const collection = createTerraBaseV1Client({ project: this.project, token, baseUrl: ORIGIN, fetch: observer.observe })
        .database(this.db).collection(COLLECTION);
      return async (seed, signal) => check(seed, await collection.get(seed.id, { signal }));
    }
    return async (seed, signal) => {
      const response = await observer.observe(`${ORIGIN}${this.docPath}?${new URLSearchParams({ id: seed.id })}`, {
        method: "GET",
        redirect: "manual",
        signal: AbortSignal.any([signal, AbortSignal.timeout(OP_S * 1000)]),
        headers: { authorization: `Bearer ${await token()}`, accept: "application/json" },
      });
      if (!response.ok) {
        await response.body?.cancel();
        throw new Error("raw read failed");
      }
      check(seed, JSON.parse(await response.text()));
    };
  }
  async cell(region, repeat, lane, size, concurrency) {
    this.run.throwIfAborted();
    const seed = this.seeds.get(size);
    const observer = new Observer(new Set([`GET ${this.docPath}`]), region, PROTOCOLS[lane]);
    const previous = getGlobalDispatcher();
    const agent = new Agent({ allowH2: lane.endsWith("h2") });
    setGlobalDispatcher(agent);
    active = observer;
    let ready;
    let stats;
    try {
      const op = this.laneOp(lane, observer);
      ready = await readyOp(op, seed, this.run);
      observer.window();
      stats = ready ? await measure(op, seed, concurrency, this.run) : { latencies: [], started: 0, ok: 0, failed: 0, stuck: false };
    } finally {
      active = null;
      setGlobalDispatcher(previous);
      await agent.destroy();
    }
    this.run.throwIfAborted();
    const ordered = [...stats.latencies].sort((a, b) => a - b);
    const reject = cellRejection(ready, stats, observer);
    this.emit({
      type: "cell", region, repeat, lane, payload_bytes: size, concurrency, ready, ok: stats.ok, sampled: ordered.length,
      ops_per_s: round3(stats.ok / MEASURE_S), p50_ms: pct(ordered, 0.5), p95_ms: pct(ordered, 0.95), p99_ms: pct(ordered, 0.99),
      operations_started: stats.started, attempts: observer.attempts, good_attempts: observer.good, bad_attempts: observer.attempts - observer.good,
      sends: observer.sent, protocol_mismatches: observer.protocolBad, protocols: observer.versions,
      connects: observer.connects, operation_failures: stats.failed, stuck: stats.stuck, reject,
    });
    if (reject) throw new Rejected(`cell rejected: ${reject}`);
  }
  async setup(admin, home) {
    await admin.databases.create({ name: this.db, homeRegion: home, allRegions: true });
    const database = admin.database(this.db);
    await database.collections.create(COLLECTION);
    const collection = database.collection(COLLECTION);
    for (const size of SIZES) {
      const body = { payload: "x".repeat(size - 14) }; // The compact JSON document is exactly `size` bytes.
      const id = generateId();
      this.seeds.set(size, { id, version: (await collection.put(id, body)).version, body });
    }
  }
  /** Deletes the exact owned name directly; the server coordinates remote erasure. */
  async cleanup(admin) {
    let row = { accepted: true };
    try {
      await admin.databases.delete(this.db, { signal: AbortSignal.timeout(CLEANUP_S * 1000) });
    } catch (error) {
      if (!(error instanceof V1NotFoundError)) {
        row = { accepted: false, status: error?.status ?? null, error: error?.constructor?.name ?? "Error" };
      }
    }
    this.emit({ type: "cleanup", database: this.db, ...row });
    return row.accepted;
  }
}

async function session(args, run) {
  const { key, project } = credentials(args.credentials);
  let fd;
  try {
    fd = openSync(args.out, "wx", 0o600);
  } catch {
    throw new Rejected("output must be a new writable file");
  }
  try {
    const regions = discoverRegions();
    const home = regions[0];
    const tenant = new Tenant({ key, project, db: `api-perf-${generateId().toLowerCase()}`, out: fd, run });
    const sdkRoot = packageRoot("@terrascale/sdk");
    tenant.emit({
      type: "identity", started: new Date().toISOString(), host: hostname(), node: process.version,
      terrascale: readVersion(sdkRoot), source: sdkSourceDigest(sdkRoot), undici: readVersion(packageRoot("undici")),
      regions, home, database: tenant.db, warmup_s: WARMUP_S, window_s: MEASURE_S,
    });
    // Setup and cleanup use the shipped SDK with an unmeasured observer pinned to the home region.
    const setupObserver = new Observer(tenant.setupRoutes, home, undefined, false);
    const admin = createTerraBaseV1Client({ project, token: tokenSource(key, project), baseUrl: ORIGIN, fetch: setupObserver.observe });
    const previous = getGlobalDispatcher();
    const setupAgent = new Agent({ allowH2: true });
    setGlobalDispatcher(setupAgent);
    let cleaned;
    try {
      try {
        await tenant.setup(admin, home);
      } catch (error) {
        tenant.emit({ type: "setup_failure", status: error?.status ?? null, error: error?.constructor?.name ?? "Error" });
        throw new Rejected("setup failed");
      }
      for (const region of regions) {
        for (let repeat = 1; repeat <= 3; repeat++) {
          for (const size of SIZES) {
            for (const concurrency of CONCURRENCY) {
              for (const lane of repeat % 2 ? LANES : [...LANES].reverse()) {
                await tenant.cell(region, repeat, lane, size, concurrency);
              }
            }
          }
        }
      }
    } finally {
      cleaned = await tenant.cleanup(admin);
      setGlobalDispatcher(previous);
      await setupAgent.destroy();
    }
    if (!cleaned) throw new Rejected("cleanup failed");
  } finally {
    closeSync(fd);
  }
}

const interrupt = new AbortController();

async function main(argv) {
  let args = {};
  try {
    ({ values: args } = parseArgs({ args: argv, options: { credentials: { type: "string" }, out: { type: "string" } }, strict: true }));
  } catch {
    // Reported below with the usage line.
  }
  if (!args.credentials || !args.out) {
    console.error("usage: parity.mjs --credentials PRIVATE.json --out NEW.jsonl");
    return 2;
  }
  const run = AbortSignal.any([AbortSignal.timeout(RUN_S * 1000), interrupt.signal]);
  try {
    await session(args, run);
    return 0;
  } catch (error) {
    if (error instanceof Rejected) {
      console.error(`parity rejected: ${error.message}`);
      return 1;
    }
    if (interrupt.signal.aborted) {
      console.error("parity interrupted");
      return 130;
    }
    console.error(`parity failed: ${error?.name ?? "Error"}`); // The type only: messages can echo server text.
    return 1;
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  for (const signal of ["SIGINT", "SIGTERM"]) process.once(signal, () => interrupt.abort());
  process.exitCode = await main(process.argv.slice(2));
}
