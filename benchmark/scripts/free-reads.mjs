import { readFile } from "node:fs/promises";
import { spawn, execFileSync } from "node:child_process";
import { createServer } from "node:net";
import { createInterface } from "node:readline";
import { hostname } from "node:os";
import { ProxyAgent } from "../../sdk/js/node_modules/undici/index.js";
import { createTerraBaseClient } from "../../sdk/js/src/index.js";

const api = "https://api.terrascale.tech";
const controller = new AbortController();
const cpu = [];
const children = [];
let phase = "startup";
const emit = value => process.stdout.write(`${JSON.stringify(value)}\n`);
const quote = value => `'${value.replaceAll("'", "'\\''")}'`;
const signal = () => AbortSignal.any([controller.signal, AbortSignal.timeout(60_000)]);
const percentile = (values, p) => {
  const sorted = values.toSorted((a, b) => a - b);
  return sorted.length ? sorted[Math.max(0, Math.ceil(p * sorted.length) - 1)] : null;
};
const stats = values => ({ count: values.length, p50_ms: percentile(values, 0.5), p99_ms: percentile(values, 0.99) });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function remote(host, file) {
  const source = await readFile(new URL(file, import.meta.url), "utf8");
  const child = spawn("ssh", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", host, `python3 -u -c ${quote(source)}`], { stdio: ["pipe", "pipe", "pipe"] });
  children.push(child);
  child.stderr.resume();
  return child;
}

async function monitor() {
  const child = await remote("ts-gru-1", "./free-reads-cpu.py");
  let ready = false;
  await new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(new Error("CPU_START_FAILED")), 10_000);
    createInterface({ input: child.stdout }).on("line", line => {
      const sample = JSON.parse(line);
      if (sample.error || sample.cpu_percent > 70) {
        controller.abort(new Error(sample.error ? "CPU_UNAVAILABLE" : "CPU_LIMIT"));
        clearTimeout(deadline);
        reject(controller.signal.reason);
        return;
      }
      cpu.push(sample);
      if (!ready) { ready = true; clearTimeout(deadline); resolve(); }
    });
    child.on("exit", () => {
      if (!controller.signal.aborted) controller.abort(new Error("CPU_MONITOR_EXITED"));
    });
  });
}

async function egress(region) {
  const host = `ts-${region}-1`;
  const server = await remote(host, "./free-reads-egress.py");
  const port = await new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(new Error("EGRESS_START_FAILED")), 10_000);
    createInterface({ input: server.stdout }).once("line", line => { clearTimeout(deadline); resolve(Number(line)); });
  });
  const reservation = createServer();
  await new Promise(resolve => reservation.listen(0, "127.0.0.1", resolve));
  const local = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const tunnel = spawn("ssh", ["-N", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "ConnectTimeout=5", "-L", `127.0.0.1:${local}:127.0.0.1:${port}`, host], { stdio: ["ignore", "ignore", "pipe"] });
  children.push(tunnel);
  tunnel.stderr.resume();
  const agent = new ProxyAgent({ uri: `http://127.0.0.1:${local}`, connections: 1024 });
  for (let attempt = 0; ; attempt++) {
    try {
      await (await fetch(`${api}/healthz`, { dispatcher: agent, signal: AbortSignal.timeout(5000) })).arrayBuffer();
      break;
    } catch (error) {
      if (attempt === 2) throw error;
      await delay(300);
    }
  }
  return { agent, async close() { await agent.destroy(); tunnel.kill(); server.stdin.end(); server.kill(); } };
}

async function main() {
  const input = [];
  for await (const part of process.stdin) input.push(part);
  const credentials = JSON.parse(Buffer.concat(input).toString("utf8"));
  const auth = await fetch("https://auth.terrascale.tech/terrascale/oauth/token", {
    method: "POST", headers: { authorization: `Basic ${Buffer.from(`${credentials.apiKey.slice(0, 26)}:${credentials.apiKey}`).toString("base64")}`, "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "client_credentials", project: credentials.project }), signal: AbortSignal.timeout(30_000),
  });
  if (auth.status !== 200) throw new Error("TOKEN_EXCHANGE_FAILED");
  const token = (await auth.json()).access_token;
  const config = { project: credentials.project, token, baseUrl: api };
  const writer = createTerraBaseClient(config);
  emit({ type: "method", sha: execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim(), host: hostname(), node: process.version,
    rows: 16, body_bytes: 128, repeats: 3, readers: [1, 10, 100, 1000], cpu_limit_percent: 70,
    latency: "Dev-host SDK through regional SSH CONNECT egress; includes tunnel and write request latency; nearest-rank percentiles per repeat" });
  await monitor();
  phase = "create_database";
  const catalog = await writer.createDatabase(credentials.project, `f10-${Date.now().toString(36)}`, { homeRegion: "gru", ttlSeconds: 3600, signal: signal() });
  const db = writer.database(catalog.id);
  const path = `${api}/v1/projects/${credentials.project}/databases/${catalog.id}`;
  const origin = async (url, mode = "follow") => {
    const response = await fetch(url, { headers: { authorization: `Bearer ${token}` }, redirect: mode, signal: signal() });
    if (![200, 307].includes(response.status)) throw new Error(`SETUP_HTTP_${response.status}`);
    return response;
  };
  try {
    for (const region of ["gru", "sjc", "fra"]) {
      const route = await egress(region);
      try {
        for (const shape of ["table", "where_columns"]) for (const readers of [1, 10, 100, 1000]) for (let repeat = 1; repeat <= 3; repeat++) {
          controller.signal.throwIfAborted();
          phase = `setup_${region}_${shape}_${readers}_${repeat}`;
          const table = `f10_${region}_${shape}_${readers}_${repeat}`;
          const filter = shape === "table" ? {} : { where: "active = 1", columns: ["id", "version"] };
          await db.execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, active INTEGER NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL)`, undefined, { signal: signal() });
          await db.execute(`INSERT INTO ${table} VALUES ${Array.from({ length: 16 }, (_, i) => `(${i + 1},${i % 2 === 0 ? 1 : 0},0,'${"x".repeat(128)}')`).join(",")}`, undefined, { signal: signal() });
          const filterQuery = shape === "table" ? {} : { where: filter.where, columns: filter.columns.join(",") };
          const shapePath = `${path}/tables/${table}/shape`;
          const bootstrap = await origin(`${shapePath}?${new URLSearchParams({ ...filterQuery, offset: "-1" })}`, "manual");
          const first = new URL(bootstrap.headers.get("location"), shapePath);
          const handle = first.searchParams.get("handle");
          const firstOffset = first.searchParams.get("offset");
          const page = await (await origin(first)).json();
          let offset = page.next_offset;
          const snapshotBoundary = offset;
          const query = at => new URLSearchParams({ ...filterQuery, handle, offset: at });
          await db.execute(`UPDATE ${table} SET version = 1 WHERE id = 1`, undefined, { signal: signal() });
          let head;
          for (let attempt = 0; attempt < 8; attempt++) {
            head = await (await origin(`${shapePath}?${query(offset)}`)).json();
            if (head.next_offset !== offset) offset = head.next_offset;
            if (head.up_to_date) break;
          }
          let objects;
          for (let attempt = 0; attempt < 8; attempt++) {
            objects = (await (await origin(`${shapePath}/manifest?${query(firstOffset)}`)).json()).objects;
            if (objects.length >= 2) break;
            await delay(250);
          }
          if (objects.length < 2 || !objects.some(item => !item.offset.startsWith("snap."))) throw new Error("MIRROR_NOT_READY");
          const before = await db.metrics.usage({}, { signal: signal() });
          phase = `readers_${region}_${shape}_${readers}_${repeat}`;
          const started = Date.now();
          const requests = { bootstrap: 0, manifest: 0, keys: 0, snapshot_payload: 0, catchup_payload: 0, head: 0, tail: 0, cdn: 0 };
          const statuses = {};
          const colos = {};
          const snapshot = [];
          const caughtUp = [];
          const tail = [];
          const subscriptions = [];
          const waiters = [];
          let tailStarted = 0;
          let live = 0;
          const measuredFetch = async (input, init) => {
            const url = new URL(String(input));
            if (url.hostname.startsWith("shapes-")) requests.cdn++;
            else if (url.pathname.endsWith("/manifest")) requests.manifest++;
            else if (url.pathname.endsWith("/keys")) requests.keys++;
            else if (url.searchParams.get("offset") === "-1") requests.bootstrap++;
            else if (url.searchParams.has("live")) { requests.tail++; live++; }
            else if (url.searchParams.get("offset").startsWith("snap.")) requests.snapshot_payload++;
            else if (url.searchParams.get("offset") === offset) requests.head++;
            else requests.catchup_payload++;
            const response = await fetch(input, { ...init, dispatcher: route.agent });
            if (url.hostname.startsWith("shapes-")) {
              const status = response.headers.get("cf-cache-status") ?? "absent";
              const colo = response.headers.get("cf-ray")?.split("-").at(-1) ?? "absent";
              statuses[status] = (statuses[status] ?? 0) + 1;
              colos[colo] = (colos[colo] ?? 0) + 1;
            }
            return response;
          };
          const reader = createTerraBaseClient({ ...config, fetch: measuredFetch }).database(catalog.id);
          const deadline = AbortSignal.any([controller.signal, AbortSignal.timeout(60_000)]);
          try {
            for (let index = 0; index < readers; index++) {
              const begin = performance.now();
              let snapped = false;
              let ready = false;
              let readyResolve;
              let tailResolve;
              const readyPromise = new Promise(resolve => { readyResolve = resolve; });
              const tailPromise = new Promise(resolve => { tailResolve = resolve; });
              const subscription = reader.shape(table, filter).subscribe(state => {
                if (!snapped && state.offset === snapshotBoundary) { snapped = true; snapshot.push(performance.now() - begin); }
                const row = state.rows.get("1");
                if (state.upToDate && row?.version === 1 && !ready) { ready = true; caughtUp.push(performance.now() - begin); readyResolve(); }
                if (row?.version === 2 && tailStarted) { tail.push(performance.now() - tailStarted); tailResolve(); subscription.close(); }
              }, { signal: deadline });
              subscriptions.push(subscription);
              waiters.push({ ready: readyPromise, tail: tailPromise });
              subscription.done.catch(() => undefined);
            }
            const failed = new Promise((_, reject) => deadline.addEventListener("abort", () => reject(deadline.reason), { once: true }));
            await Promise.race([Promise.all(waiters.map(waiter => waiter.ready)), failed]);
            for (let attempt = 0; live < readers && attempt < 100; attempt++) { deadline.throwIfAborted(); await delay(10); }
            if (live < readers) throw new Error("LIVE_NOT_READY");
            tailStarted = performance.now();
            await db.execute(`UPDATE ${table} SET version = 2 WHERE id = 1`, undefined, { signal: deadline });
            await Promise.race([Promise.all(waiters.map(waiter => waiter.tail)), failed]);
          } finally {
            for (const subscription of subscriptions) subscription.close();
            await Promise.allSettled(subscriptions.map(subscription => subscription.done));
          }
          const ended = Date.now();
          const after = await db.metrics.usage({}, { signal: signal() });
          const samples = cpu.filter(sample => sample.at * 1000 >= started && sample.at * 1000 <= ended);
          emit({ type: "cell", region, shape, readers, repeat, start: new Date(started).toISOString(), end: new Date(ended).toISOString(),
            snapshot: stats(snapshot), catchup: stats(caughtUp), tail: stats(tail), requests, statuses, colos,
            cdn_hit_ratio: (statuses.HIT ?? 0) / requests.cdn,
            cpu_samples: samples.length, cpu_max_percent: samples.length ? Math.max(...samples.map(sample => sample.cpu_percent)) : null,
            billed_reads_before: before.reads, billed_reads_after: after.reads, billed_read_units: after.reads - before.reads });
          if (snapshot.length !== readers || tail.length !== readers || after.reads !== before.reads) throw new Error("MEASUREMENT_INCOMPLETE");
        }
      } finally { await route.close(); }
    }
  } finally {
    await writer.deleteDatabase(credentials.project, catalog.id, { signal: AbortSignal.timeout(30_000) });
    emit({ type: "cleanup", database_deleted: true });
  }
}

try { await main(); }
catch (error) { emit({ type: "failure", phase, error: controller.signal.aborted ? controller.signal.reason.message : /^[A-Z][A-Z0-9_]+$/.test(error.message) ? error.message : error.name,
  frames: error.stack?.split("\n").slice(1).filter(line => line.includes("/benchmark/") || line.includes("/sdk/js/")).slice(0, 5) }); process.exitCode = 1; }
finally {
  for (const child of children) { child.stdin?.end(); child.kill(); }
}
