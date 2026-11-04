import { readFile } from "node:fs/promises";
import { spawn, execFileSync } from "node:child_process";
import { createServer, createConnection } from "node:net";
import { createInterface } from "node:readline";
import { hostname } from "node:os";
import { pathToFileURL } from "node:url";
import { ProxyAgent, WebSocket } from "../../sdk/js/node_modules/undici/index.js";
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
const stats = (values, expected) => ({ count: values.length, p50_ms: values.length === expected ? percentile(values, 0.5) : null, p99_ms: values.length === expected ? percentile(values, 0.99) : null });
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
      const invalid = !Number.isFinite(sample.cpu_percent) || !Number.isFinite(sample.cores) || sample.cores <= 0;
      if (sample.error || invalid || sample.cpu_percent > 70) {
        emit({ type: "guard", ...sample });
        controller.abort(new Error(sample.error || invalid ? "CPU_UNAVAILABLE" : "CPU_LIMIT"));
        clearTimeout(deadline);
        reject(controller.signal.reason);
        return;
      }
      cpu.push(sample);
      if (!ready) { ready = true; emit({ type: "cpu_capacity", ...sample }); clearTimeout(deadline); resolve(); }
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
  const forwardingDeadline = performance.now() + 10_000;
  for (;;) {
    controller.signal.throwIfAborted();
    if (tunnel.exitCode !== null) throw new Error("EGRESS_TUNNEL_EXITED");
    const ready = await new Promise(resolve => {
      const socket = createConnection({ host: "127.0.0.1", port: local });
      socket.once("connect", () => { socket.destroy(); resolve(true); });
      socket.once("error", () => { socket.destroy(); resolve(false); });
      socket.setTimeout(1000, () => { socket.destroy(); resolve(false); });
    });
    if (ready) break;
    if (performance.now() >= forwardingDeadline) throw new Error("EGRESS_FORWARD_NOT_READY");
    await delay(100);
  }
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

async function main({ readerCounts = [1, 10, 100, 1000], regions = ["gru", "sjc", "fra"], holdMs = 0, flightMetrics = false, tailOnly = false, shapes = ["table", "where_columns"] } = {}) {
  const input = [];
  for await (const part of process.stdin) input.push(part);
  const credentials = JSON.parse(Buffer.concat(input).toString("utf8"));
  const completed = new Set(credentials.completed ?? []);
  if ([...completed].some(value => typeof value !== "string" || !/^f10_(gru|sjc|fra)_(table|where_columns)_(1|10|100|1000)_[123]$/.test(value))) throw new Error("RESUME_INVALID");
  let currentToken;
  let refreshAt = 0;
  let refreshing;
  const accessToken = async () => {
    if (currentToken && performance.now() < refreshAt) return currentToken;
    if (!refreshing) refreshing = (async () => {
      const auth = await fetch("https://auth.terrascale.tech/terrascale/oauth/token", {
        method: "POST", headers: { authorization: `Basic ${Buffer.from(`${credentials.apiKey.slice(0, 26)}:${credentials.apiKey}`).toString("base64")}`, "content-type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({ grant_type: "client_credentials", project: credentials.project }), signal: AbortSignal.timeout(30_000),
      });
      if (auth.status !== 200) throw new Error("TOKEN_EXCHANGE_FAILED");
      const result = await auth.json();
      if (!Number.isSafeInteger(result.expires_in) || result.expires_in <= 0) throw new Error("TOKEN_LIFETIME_INVALID");
      currentToken = result.access_token;
      refreshAt = performance.now() + result.expires_in * 1000 - Math.min(30_000, result.expires_in * 250);
      return currentToken;
    })().finally(() => { refreshing = undefined; });
    return refreshing;
  };
  const config = { project: credentials.project, token: accessToken, baseUrl: api };
  const writer = createTerraBaseClient(config);
  const sha = execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim();
  emit({ type: "method", sha, host: hostname(), node: process.version, resumed_cohorts: [...completed],
    rows: 16, body_bytes: 128, repeats: 3, readers: readerCounts, regions, tail_only: tailOnly, transport_sharing: tailOnly ? "Independent WebSocket tail connections; one authenticated bootstrap supplies the common handle" : "Identical subscriptions on one SDK client share one upstream stream", hold_ms: holdMs, cpu_limit_percent: 70, cohort_deadline_seconds: 180,
    cpu: "Process CPU ticks divided by the minimum of affinity and cgroup v2 ancestor quotas; stop on process or capacity change",
    latency: "Dev-host SDK through regional SSH CONNECT egress; includes tunnel and write request latency; nearest-rank percentiles per repeat" });
  await monitor();
  for (const previous of (await writer.databases.list({ limit: 100 }, { signal: signal() })).items) {
    if (!previous.name.startsWith("f10-")) throw new Error("OWNED_CLEANUP_REFUSED");
    await writer.deleteDatabase(credentials.project, previous.id, { signal: signal() });
    emit({ type: "cleanup", previous_database_deleted: true });
  }
  phase = "create_database";
  const catalog = await writer.createDatabase(credentials.project, `f10-${Date.now().toString(36)}`, { homeRegion: "gru", ttlSeconds: 7200, signal: signal() });
  const db = writer.database(catalog.id);
  const path = `${api}/v1/projects/${credentials.project}/databases/${catalog.id}`;
  const origin = async (url, mode = "follow") => {
    const response = await fetch(url, { headers: { authorization: `Bearer ${await accessToken()}` }, redirect: mode, signal: signal() });
    if (![200, 307].includes(response.status)) throw new Error(`SETUP_HTTP_${response.status}`);
    return response;
  };
  try {
    for (const readers of readerCounts) for (const region of regions) {
      phase = `egress_${region}_${readers}`;
      const route = await egress(region);
      try {
        for (const shape of shapes) for (let repeat = 1; repeat <= 3; repeat++) {
          controller.signal.throwIfAborted();
          phase = `setup_${region}_${shape}_${readers}_${repeat}`;
          const table = `f10_${region}_${shape}_${readers}_${repeat}`;
          if (completed.has(table)) continue;
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
          const monthBefore = (await writer.metrics.usage({}, { signal: signal() })).month;
          phase = `readers_${region}_${shape}_${readers}_${repeat}`;
          const started = Date.now();
          const flightBefore = flightMetrics ? await shapeFlights() : undefined;
          const requests = { sockets: 0, hints: 0, bootstrap: 0, manifest: 0, keys: 0, snapshot_payload: 0, catchup_payload: 0, head: 0, tail: 0, cdn: 0 };
          const statuses = {};
          const originStatuses = {};
          const colos = {};
          const snapshot = [];
          const caughtUp = [];
          const tail = [];
          const subscriptions = [];
          const waiters = [];
          let tailStarted = 0;
          let live = 0;
          let outcome = "complete";
          let rejectReader;
          const readerFailed = new Promise((_, reject) => { rejectReader = reject; });
          // Raw sockets can fail while the rest of the cohort is still connecting.
          readerFailed.catch(() => {});
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
            } else originStatuses[response.status] = (originStatuses[response.status] ?? 0) + 1;
            return response;
          };
          class MeasuredSocket extends WebSocket {
            constructor(url, options) {
              super(url, { ...(Array.isArray(options) ? { protocols: options } : options), dispatcher: route.agent });
              requests.sockets++;
              this.addEventListener("message", event => {
                let frame;
                try { frame = JSON.parse(event.data); } catch { controller.abort(new Error("HINT_FRAME_INVALID")); return; }
                if (frame.type === "ready") live++;
                if (frame.type === "new_data") requests.hints++;
              });
            }
          }
          const reader = createTerraBaseClient({ ...config, fetch: measuredFetch, WebSocket: MeasuredSocket }).database(catalog.id);
          const deadline = AbortSignal.any([controller.signal, AbortSignal.timeout(180_000)]);
          try {
            if (tailOnly) {
              refreshAt = 0;
              const bearer = await accessToken();
              for (let index = 0; index < readers; index++) {
                deadline.throwIfAborted();
                const connected = performance.now();
                let readyResolve;
                let tailResolve;
                const readyPromise = new Promise(resolve => { readyResolve = resolve; });
                const tailPromise = new Promise(resolve => { tailResolve = resolve; });
                const url = new URL(`${shapePath}/ws?${query(offset)}`);
                url.protocol = "wss:";
                const socket = new MeasuredSocket(url, { protocols: ["terrascale.shape.v1"], headers: { authorization: `Bearer ${bearer}` } });
                let delivered = false;
                socket.addEventListener("message", event => {
                  const frame = JSON.parse(event.data);
                  if (frame.type === "ready") { caughtUp.push(performance.now() - connected); readyResolve(); }
                  if (frame.type === "new_data" && tailStarted && !delivered) {
                    if (frame.handle !== handle || frame.offset !== offset || frame.next_offset === offset || "changes" in frame)
                      return rejectReader(new Error("HINT_BINDING_INVALID"));
                    delivered = true;
                    tail.push(performance.now() - tailStarted);
                    tailResolve();
                    socket.close();
                  }
                  if (frame.type === "error") rejectReader(new Error("TAIL_REFUSED"));
                });
                socket.addEventListener("error", () => rejectReader(new Error("TAIL_SOCKET_ERROR")));
                socket.addEventListener("close", () => { if (!delivered && !deadline.aborted) rejectReader(new Error("TAIL_SOCKET_CLOSED")); });
                const close = () => socket.close();
                deadline.addEventListener("abort", close, { once: true });
                subscriptions.push({ close: () => { deadline.removeEventListener("abort", close); socket.close(); }, done: Promise.resolve() });
                waiters.push({ ready: readyPromise, tail: tailPromise });
                await Promise.race([delay(25), readerFailed]);
              }
            } else for (let index = 0; index < readers; index++) {
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
              subscription.done.catch(error => rejectReader(error));
            }
            const failed = new Promise((_, reject) => deadline.addEventListener("abort", () => reject(deadline.reason), { once: true }));
            await Promise.race([Promise.all(waiters.map(waiter => waiter.ready)), failed, readerFailed]);
            for (let attempt = 0; live < 1 && attempt < 100; attempt++) { deadline.throwIfAborted(); await delay(10); }
            if (live < 1) throw new Error("LIVE_NOT_READY");
            if (holdMs) await delay(holdMs);
            deadline.throwIfAborted();
            tailStarted = performance.now();
            await db.execute(`UPDATE ${table} SET version = 2 WHERE id = 1`, undefined, { signal: deadline });
            await Promise.race([Promise.all(waiters.map(waiter => waiter.tail)), failed, readerFailed]);
          } catch (error) {
            if (!controller.signal.aborted && deadline.aborted && deadline.reason?.name === "TimeoutError") outcome = "timeout";
            else throw error;
          } finally {
            for (const subscription of subscriptions) subscription.close();
            await Promise.allSettled(subscriptions.map(subscription => subscription.done));
          }
          const ended = Date.now();
          const after = await db.metrics.usage({}, { signal: signal() });
          const monthAfter = (await writer.metrics.usage({}, { signal: signal() })).month;
          const flightAfter = flightMetrics ? await shapeFlights() : undefined;
          const samples = cpu.filter(sample => sample.at * 1000 >= started && sample.at * 1000 <= ended);
          emit({ type: "cell", sha, outcome, region, shape, readers, repeat, start: new Date(started).toISOString(), end: new Date(ended).toISOString(),
            shared_flights_started: flightBefore === undefined ? undefined : flightAfter - flightBefore,
            snapshot: stats(snapshot, readers), catchup: stats(caughtUp, readers), tail: stats(tail, readers), requests, statuses, origin_statuses: originStatuses, colos,
            tail_only: tailOnly, cdn_hit_ratio: requests.cdn ? (statuses.HIT ?? 0) / requests.cdn : null,
            cpu_samples: samples.length, cpu_max_percent: samples.length ? Math.max(...samples.map(sample => sample.cpu_percent)) : null,
            usage_reads_before: before.reads, usage_reads_after: after.reads,
            month_before: monthBefore, month_after: monthAfter, sql_read_units: monthAfter.readUnits - monthBefore.readUnits,
            billed_read_units_before: monthBefore.units - monthBefore.writeUnits, billed_read_units_after: monthAfter.units - monthAfter.writeUnits });
          if ((outcome === "complete" && ((!tailOnly && snapshot.length !== readers) || tail.length !== readers)) || after.reads !== before.reads || monthAfter.readUnits !== monthBefore.readUnits
            || monthAfter.units !== monthAfter.writeUnits) throw new Error("MEASUREMENT_INCOMPLETE");
          if (!tailOnly && readers === 1 && (requests.snapshot_payload || requests.catchup_payload || requests.cdn < 2)) throw new Error("CDN_UNVERIFIED");
        }
      } finally { await route.close(); }
    }
  } finally {
    await writer.deleteDatabase(credentials.project, catalog.id, { signal: AbortSignal.timeout(30_000) });
    emit({ type: "cleanup", database_deleted: true });
  }
}

export async function runFreeReads(options) {
  try { await main(options); }
  catch (error) { emit({ type: "failure", phase, error: controller.signal.aborted ? controller.signal.reason.message : /^[A-Z][A-Z0-9_]+$/.test(error.message) ? error.message : error.name,
    cause: typeof error.cause?.code === "string" && /^[A-Z0-9_]+$/.test(error.cause.code) ? error.cause.code : undefined,
    frames: error.stack?.split("\n").slice(1).filter(line => line.includes("/benchmark/") || line.includes("/sdk/js/")).slice(0, 5) }); process.exitCode = 1; }
  finally { for (const child of children) { child.stdin?.end(); child.kill(); } }
}

async function shapeFlights() {
  const source = "import urllib.request; text=urllib.request.urlopen('http://127.0.0.1:9464/metrics',timeout=5).read().decode(); values=[float(line.split()[-1]) for line in text.splitlines() if line.startswith('terrabase_shape_flights_started_total ')]; assert values, 'FLIGHT_METRIC_MISSING'; print(sum(values))";
  const child = spawn("ssh", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "ts-gru-1", `python3 -c ${quote(source)}`], { stdio: ["ignore", "pipe", "ignore"] });
  let output = "";
  child.stdout.on("data", part => { output += part; });
  await new Promise((resolve, reject) => { child.on("exit", code => code === 0 ? resolve() : reject(new Error("FLIGHT_METRICS_UNAVAILABLE"))); });
  const count = Number(output.trim());
  if (!Number.isSafeInteger(count) || count < 0) throw new Error("FLIGHT_METRICS_INVALID");
  return count;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await runFreeReads();
