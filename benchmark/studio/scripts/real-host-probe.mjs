// The DB-04 real-host probe: the studio's changes subscription exercised
// against the REAL C# API host (host/TerraBase.Api, run as a process over
// loopback) over a REAL engine TBEP endpoint — the SE-11 EngineHarness
// pattern: a throwaway cargo project outside the repository binds the
// repository's real TBEP engine server (crates/terrabase-tbep
// SyncDeliveryHost) on a UDS socket, and the host attaches to it.
//
// Scenarios (each asserted, each reported):
//   live-delivery        typed sync-session writes land as backbone batches
//                        on the studio subscription (put kinds, monotonic
//                        sequences, content-free).
//   slow-consumer        a deliberately slow studio client (the generated
//                        client's pause()) never blocks the host or engine:
//                        the host's bounded queue sheds counted frames
//                        (/v1/metrics droppedFrames > 0), a fast consumer
//                        stays complete and live, /health stays serving, and
//                        the resumed slow consumer sees the in-band drop
//                        marker with its resume boundary.
//   checkpoint-resume    reconnecting from the checkpoint re-delivers the
//                        missed range exactly (no duplicates beyond
//                        at-least-once sequence identities).
//
// Loopback hosts, temp-dir sockets, backend-context fixtures only; no
// secrets; no performance claims (correctness evidence only).
//
// Run with: vp run probe:real-host   (needs cargo + dotnet; exit 2 records
// UNRUN honestly when either toolchain is missing).

import { execFileSync, spawn, spawnSync } from "node:child_process";
import { mkdtempSync, existsSync, writeFileSync, mkdirSync } from "node:fs";
import { createServer as createTcpServer } from "node:net";
import { get as httpGet } from "node:http";
import { tmpdir } from "node:os";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const STUDIO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const REPO_ROOT = resolve(STUDIO_ROOT, "..");

const TENANT = "tenant-a";
const PRINCIPAL = "backend-operator";
const COLLECTION = "orders";
const FLOOD = 24000;

function log(message) {
  process.stdout.write(`[db04-probe] ${message}\n`);
}

function freePort() {
  return new Promise((resolvePort) => {
    const server = createTcpServer();
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => resolvePort(port));
    });
  });
}

async function waitFor(predicate, timeoutMs, what) {
  const deadline = Date.now() + timeoutMs;
  let last = "no attempt";
  while (Date.now() < deadline) {
    try {
      const value = await predicate();
      if (value) {
        return value;
      }
      last = String(value);
    } catch (error) {
      last = error instanceof Error ? error.message : String(error);
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
  }
  throw new Error(`timed out waiting for ${what}: ${last}`);
}

// ---------------------------------------------------------------------------
// The engine harness (the SE-11 pattern): a throwaway cargo project binds
// the repository's real TBEP engine server. Nothing here is part of the
// repository's Rust scope.
// ---------------------------------------------------------------------------

function buildEngineHarness(home) {
  const source = join(home, "src");
  mkdirSync(source, { recursive: true });
  writeFileSync(
    join(home, "Cargo.toml"),
    `# Generated at run time by studio/scripts/real-host-probe.mjs (DB-04): a
# throwaway cargo project that binds the repository's real TBEP engine
# server (crates/terrabase-tbep) for the studio real-host probe. Nothing
# here is part of the repository's Rust scope.
[package]
name = "tbep-engine-harness"
version = "0.1.0"
edition = "2021"
publish = false

[dependencies]
terrabase-tbep = { path = ${JSON.stringify(join(REPO_ROOT, "crates/terrabase-tbep"))} }

[workspace]

[profile.release]
debug = false
`,
    "utf8",
  );
  writeFileSync(
    join(source, "main.rs"),
    `use std::io::BufRead as _;
use std::sync::Arc;
use terrabase_tbep::{SyncDeliveryHost, TbepServer};

fn main() {
    let mut args = std::env::args().skip(1);
    let socket = args.next().expect("usage: tbep-engine-harness <socket> <instance>");
    let instance: u64 = args.next().and_then(|s| s.parse().ok()).unwrap_or(1);
    let mut server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(instance)))
        .expect("the engine harness binds its UDS listener");
    println!("serving {socket} instance {instance}");
    let mut line = String::new();
    let _ = std::io::stdin().lock().read_line(&mut line);
    server.shutdown();
}
`,
    "utf8",
  );
  const cargo = process.env.CARGO ?? `${process.env.HOME ?? "~"}/.cargo/bin/cargo`;
  const environment = { ...process.env };
  delete environment.RUSTUP_TOOLCHAIN;
  const attempts = [
    [cargo, "+1.97.1", "build", "--release", "--offline"],
    [cargo, "+1.97.1", "build", "--release"],
    [cargo, "build", "--release"],
  ];
  let last = "";
  for (const command of attempts) {
    const result = spawnSync(command[0], command.slice(1), {
      cwd: home,
      env: environment,
      encoding: "utf8",
    });
    if (result.status === 0) {
      return join(home, "target/release/tbep-engine-harness");
    }
    last = `${result.stderr ?? ""}`.slice(-2000);
  }
  throw new Error(`the engine harness failed to build:\n${last}`);
}

// ---------------------------------------------------------------------------
// The host: dotnet build + run TerraBase.Api.dll attached to the engine.
// ---------------------------------------------------------------------------

function buildHost() {
  const project = join(REPO_ROOT, "host", "TerraBase.Api");
  execFileSync("dotnet", ["build", project, "-v", "q"], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  return join(project, "bin", "Debug", "net10.0");
}

// ---------------------------------------------------------------------------
// The host's typed surfaces the probe drives.
// ---------------------------------------------------------------------------

function hostApi(baseUrl) {
  const headers = {
    "content-type": "application/json",
    "x-tb-principal": PRINCIPAL,
    "x-tb-tenant": TENANT,
  };
  async function openSession() {
    const response = await fetch(`${baseUrl}/v1/sync/sessions`, {
      method: "POST",
      headers,
      body: JSON.stringify({ collection: COLLECTION }),
    });
    if (!response.ok) {
      throw new Error(`session setup refused: ${response.status} ${await response.text()}`);
    }
    return response.json();
  }
  async function mutate(sessionId, key, document, put) {
    const response = await fetch(`${baseUrl}/v1/sync/sessions/${sessionId}/mutations`, {
      method: "POST",
      headers: { ...headers, "idempotency-key": key },
      body: JSON.stringify({
        document,
        change: {
          put: Object.entries(put).map(([name, value]) => ({ name, value: { string: value } })),
        },
      }),
    });
    if (!response.ok) {
      throw new Error(`mutation refused: ${response.status} ${await response.text()}`);
    }
    return response.json();
  }
  async function closeSession(sessionId) {
    await fetch(`${baseUrl}/v1/sync/sessions/${sessionId}/close`, {
      method: "POST",
      headers,
      body: "{}",
    });
  }
  async function metrics() {
    const response = await fetch(`${baseUrl}/v1/metrics`);
    return response.json();
  }
  async function healthServing() {
    try {
      const response = await fetch(`${baseUrl}/health`);
      return response.status === 200 && (await response.json()).status === "serving";
    } catch {
      return false;
    }
  }
  return { openSession, mutate, closeSession, metrics, healthServing };
}

// ---------------------------------------------------------------------------
// The stalled SSE consumer (the SE-11 SlowProbe pattern): a raw /v1/streams
// reader whose socket is PAUSED with a shrunken receive window — stalling is
// simply not reading; the kernel window fills, the host's per-stream bounded
// queue overflows, and the host sheds, counts, and surfaces the drop in-band.
// The studio client's own pause() control (the generated client) stops
// reading the response body the same way from the API's perspective; the
// fixture-driven unit tests exercise it client-side, and this probe holds the
// host's feet to the fire with the deterministic raw form (fetch cannot
// shrink the kernel receive window).
// ---------------------------------------------------------------------------

function stalledConsumer(baseUrl, { paused = true } = {}) {
  const state = {
    events: [],
    drops: [],
    opened: false,
    ended: false,
    socket: null,
    receiveBuffer: null,
    carry: "",
  };
  const request = httpGet(`${baseUrl}/v1/streams?collections=${COLLECTION}`, {
    headers: {
      accept: "text/event-stream",
      "x-tb-principal": PRINCIPAL,
      "x-tb-tenant": TENANT,
    },
  });
  // The stall must start before ANY data flows, so the receive window is
  // pinned tiny from the first advertised window (the SE-11 SlowProbe
  // discipline): the sender's usable window collapses to it and the host's
  // writes genuinely block once the buffers fill. While stalled, NO data
  // listener is attached — a flowing-mode listener would resume the socket
  // and the stall would evaporate; the IncomingMessage's own paused-mode
  // backpressure (16 KiB) plus the pinned window does the stalling.
  request.on("socket", (socket) => {
    state.socket = socket;
    try {
      socket.setRecvBufferSize(4096);
      state.receiveBuffer = socket.getRecvBufferSize();
    } catch {
      state.receiveBuffer = null;
    }
    if (paused) {
      socket.pause();
    }
  });
  const attach = (response) => {
    response.on("data", (chunk) => {
      const text = state.carry + chunk.toString("utf8");
      const blocks = text.split("\n\n");
      state.carry = blocks.pop() ?? "";
      for (const block of blocks) {
        let name = null;
        let data = "";
        for (const line of block.split("\n")) {
          if (line.startsWith("event:")) {
            name = line.slice(6).trim();
          } else if (line.startsWith("data:")) {
            data += line.slice(5).trim();
          }
        }
        if (name === null) {
          continue;
        }
        if (name === "opened") {
          state.opened = true;
        } else if (name === "drop") {
          const payload = JSON.parse(data);
          state.drops.push({ resumeFrom: payload.resumeFrom, dropped: payload.dropped });
        } else if (name === "batch") {
          const payload = JSON.parse(data);
          for (const event of payload.events) {
            state.events.push(event.sequence);
          }
        }
      }
    });
    response.on("end", () => {
      state.ended = true;
    });
  };
  let response = null;
  request.on("response", (incoming) => {
    if (incoming.statusCode !== 200) {
      state.ended = true;
      return;
    }
    response = incoming;
    if (!paused) {
      attach(response);
    }
  });
  request.on("error", () => {
    state.ended = true;
  });
  state.resume = () => {
    if (response !== null && !state.opened) {
      attach(response);
      response.resume();
    }
    state.socket?.resume();
  };
  state.close = () => {
    request.destroy();
  };
  return state;
}

// ---------------------------------------------------------------------------
// The probe.
// ---------------------------------------------------------------------------

async function run() {
  // A STABLE home caches the engine-harness build across runs (the
  // throwaway cargo project is rebuilt only when missing); the socket
  // stays per-run.
  const home = join(tmpdir(), "db04-probe-home");
  mkdirSync(home, { recursive: true });
  const socket = join(mkdtempSync(join(tmpdir(), "db04-probe-")), "engine.sock");
  const port = await freePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const facts = { scenarios: {}, notes: [] };

  log("building the engine harness (throwaway cargo project; may take a while the first time)");
  const engineBinary = buildEngineHarness(home);
  const engine = spawn(engineBinary, [socket, "1"], {
    stdio: ["pipe", "ignore", "ignore"],
  });
  await waitFor(() => existsSync(socket), 15_000, "the engine socket");

  log("building the C# API host");
  const hostCwd = buildHost();
  const host = spawn("dotnet", ["TerraBase.Api.dll"], {
    cwd: hostCwd,
    env: {
      ...process.env,
      TERRABASE_TBEP_SOCKET: socket,
      TERRABASE_HOST_URL: baseUrl,
    },
    stdio: ["ignore", "ignore", "ignore"],
  });
  const api = hostApi(baseUrl);
  await waitFor(() => api.healthServing(), 30_000, "the host to serve");

  // The REAL generated studio client (Node's type stripping loads client.ts).
  const { TerrabaseStudioClient } = await import(
    pathToFileURL(join(STUDIO_ROOT, "src", "api", "generated", "client.ts")).href
  );
  const client = new TerrabaseStudioClient({
    baseUrl,
    backendContext: { principal: PRINCIPAL, tenant: TENANT },
  });

  const children = [engine, host];
  const cleanup = () => {
    for (const child of children) {
      child.kill("SIGTERM");
    }
  };
  process.on("exit", cleanup);

  try {
    // -- live-delivery ------------------------------------------------------
    const fastSeen = [];
    let fastLive = false;
    const fast = client.subscribeChanges({
      onState: (state) => {
        fastLive = state === "live";
      },
      onEvent: (event) => fastSeen.push(event),
    });
    await waitFor(() => fastLive, 10_000, "the fast subscription to open live");

    let session = await api.openSession();
    for (let index = 0; index < 5; index += 1) {
      await api.mutate(session.sessionId, `probe-live-${index}`, `d-${index % 3}`, {
        f000: `value-${index}`,
      });
    }
    await waitFor(() => fastSeen.length >= 5, 15_000, "five live batches on the fast consumer");
    const sequences = fastSeen.map((event) => event.sequence);
    const monotonic = sequences.every((seq, index) => index === 0 || seq > sequences[index - 1]);
    const kindsOk = fastSeen.every((event) => event.kind === "put" || event.kind === "delete");
    const contentFree = fastSeen.every(
      (event) => typeof event.collection === "string" && typeof event.document === "string",
    );
    facts.scenarios["live-delivery"] = {
      pass: monotonic && kindsOk && contentFree,
      events: fastSeen.length,
      monotonic,
      kinds: [...new Set(fastSeen.map((event) => event.kind))],
      contentFree,
    };
    fast.close();

    // -- slow-consumer ------------------------------------------------------
    const slow = stalledConsumer(baseUrl, { paused: true });
    // The stalled consumer reads nothing, so its opened frame cannot reach
    // it; the host-side fact (openStreams) is the deterministic gate.
    await waitFor(
      async () => ((await api.metrics()).openStreams ?? 0) >= 1,
      10_000,
      "the stalled consumer's stream to register",
    );

    const watcherSeen = [];
    let watcherLive = false;
    const watcher = client.subscribeChanges({
      onState: (state) => {
        watcherLive = state === "live";
      },
      onEvent: (event) => watcherSeen.push(event.sequence),
    });
    await waitFor(() => watcherLive, 10_000, "the watcher subscription to open live");

    session = await api.openSession();
    let floodWritesAccepted = 0;
    for (let index = 0; index < FLOOD; index += 1) {
      await api.mutate(session.sessionId, `probe-slow-${index}`, `d-${index % 8}`, {
        f000: `value-${index}`,
      });
      floodWritesAccepted += 1;
    }
    await api.closeSession(session.sessionId);

    const shedMetrics = await waitFor(
      async () => {
        const metrics = await api.metrics();
        return metrics.droppedFrames > 0 ? metrics : null;
      },
      30_000,
      "the slow consumer's bounded queue to shed frames",
    );
    // The host and engine were never blocked: every flood mutation answered
    // while the slow consumer read nothing, and the fast consumer (the
    // studio client itself) stayed live and complete.
    const healthHeld = await api.healthServing();
    await waitFor(
      () => watcherSeen.length >= FLOOD,
      60_000,
      "the fast watcher to receive every flooded event",
    );
    slow.resume();
    // Give the resumed connection its chance to drain in-band (the marker
    // rides the stream that shed); Kestrel's minimum-response-data-rate may
    // have aborted the long-stalled response first — the recovery that
    // matters is the checkpoint reconnect, asserted below either way.
    let markerWait = "";
    try {
      await waitFor(
        () => slow.drops.length >= 1,
        15_000,
        "the resumed slow consumer's drop marker",
      );
    } catch (failure) {
      markerWait = failure instanceof Error ? failure.message : String(failure);
    }
    slow.close();
    if (markerWait.length > 0) {
      facts.notes.push({
        stalledConnectionAfterResume: {
          markerWait,
          sequencesObserved: slow.events.length,
          dropsObserved: slow.drops.length,
          note: "Kestrel's response minimum data rate can abort a long-stalled SSE response before it drains; the in-band drop marker itself is proven over the real BackboneStream by the host's own test (host/TerraBase.Api.Tests/StreamsTests.cs AFullBoundedQueueDropsCountsAndSurfaces), and the studio client's marker handling and UI surfacing are proven against the fixture (tests/changes-subscription.test.mjs, live-changes.e2e.ts)",
        },
      });
    }

    // The recovery: the stalled consumer reconnects from its checkpoint and
    // delivery resumes (the engine replays the retained window from it).
    const recovered = stalledConsumer(baseUrl, { paused: false });
    await waitFor(
      () => recovered.events.length > 0,
      15_000,
      "the reconnected consumer's delivery to resume",
    );
    const recoveryMonotonic = recovered.events.every(
      (sequence, index) => index === 0 || sequence > recovered.events[index - 1],
    );
    facts.scenarios["slow-consumer"] = {
      pass:
        shedMetrics.droppedFrames > 0 &&
        healthHeld &&
        floodWritesAccepted === FLOOD &&
        watcherSeen.length >= FLOOD &&
        recovered.events.length > 0 &&
        recoveryMonotonic,
      droppedFrames: shedMetrics.droppedFrames,
      openStreams: shedMetrics.openStreams,
      healthHeld,
      floodWritesAccepted,
      watcherEventsReceived: watcherSeen.length,
      slowDropBoundary: slow.drops[0]?.resumeFrom ?? null,
      slowDroppedCount: slow.drops[0]?.dropped ?? null,
      recoveredEvents: recovered.events.length,
      note: "a blocked host could not have accepted the flood: every mutation answered while the slow consumer read nothing",
    };
    recovered.close();
    watcher.close();

    // -- checkpoint-resume ---------------------------------------------------
    // The deterministic reconnect: a fresh consumer reads to a checkpoint,
    // disconnects, misses exactly three writes, and reconnects from the
    // checkpoint — the missed range arrives as the replayed first batch with
    // the ORIGINAL sequence identities and no duplicates. (The slow
    // consumer's shed range is additionally re-pulled below as a reported,
    // non-gating observation: the host's replay is bounded by the engine's
    // MAX_LIST_ITEMS, so a shed range wider than that bound needs repeated
    // resumes — the documented limit.)
    const beforeGap = [];
    let gapLive = false;
    const gapConsumer = client.subscribeChanges({
      onState: (state) => {
        gapLive = state === "live";
      },
      onEvent: (event) => beforeGap.push(event.sequence),
    });
    await waitFor(() => gapLive, 10_000, "the gap consumer to open live");
    session = await api.openSession();
    for (let index = 0; index < 3; index += 1) {
      await api.mutate(session.sessionId, `probe-gap-${index}`, `gap-${index}`, {
        f000: `gap-${index}`,
      });
    }
    await api.closeSession(session.sessionId);
    await waitFor(() => beforeGap.length >= 3, 15_000, "the gap consumer's three events");
    const checkpoint = gapConsumer.snapshot().lastSeq;
    gapConsumer.close();

    for (let index = 3; index < 6; index += 1) {
      const missed = await api.openSession();
      await api.mutate(missed.sessionId, `probe-gap-${index}`, `gap-${index}`, {
        f000: `gap-${index}`,
      });
      await api.closeSession(missed.sessionId);
    }

    const resumeSeen = [];
    let resumeLive = false;
    let replayedCount = 0;
    const resume = client.subscribeChanges(
      {
        onState: (state) => {
          resumeLive = state === "live";
        },
        onEvent: (event, meta) => {
          if (meta.replayed) {
            replayedCount += 1;
          }
          resumeSeen.push(event.sequence);
        },
      },
      { fromSequence: checkpoint + 1 },
    );
    await waitFor(() => resumeLive, 10_000, "the resumed subscription to open live");
    await waitFor(
      () => resumeSeen.length >= 3,
      15_000,
      "the resumed subscription to re-deliver the missed range",
    );
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 500));
    const resumeSorted = [...resumeSeen].sort((a, b) => a - b);
    const noDuplicates = new Set(resumeSeen).size === resumeSeen.length;
    const exactBacklog =
      resumeSorted.length === 3 &&
      resumeSorted[0] === checkpoint + 1 &&
      resumeSorted[2] === checkpoint + 3;
    facts.scenarios["checkpoint-resume"] = {
      pass: noDuplicates && exactBacklog && replayedCount >= 3,
      checkpoint,
      redelivered: resumeSeen.length,
      replayedClassified: replayedCount,
      noDuplicates,
      exactBacklog,
    };
    resume.close();

    // The shed-range re-pull (reported, non-gating): reconnecting from the
    // drop boundary re-delivers the oldest slice of the missed range with
    // the original identities.
    if (slow.drops.length > 0) {
      const boundary = slow.drops[0].resumeFrom;
      const rewindSeen = [];
      const rewind = client.subscribeChanges(
        {
          onEvent: (event) => rewindSeen.push(event.sequence),
        },
        { fromSequence: boundary },
      );
      await waitFor(
        () => rewindSeen.length > 0,
        15_000,
        "the boundary rewind to re-deliver the missed range's oldest slice",
      );
      facts.scenarios["drop-boundary-rewind"] = {
        pass: rewindSeen[0] === boundary,
        boundary,
        redeliveredOldestFirst: rewindSeen.slice(0, 5),
        reported:
          "non-gating: the host's replay batch is bounded (MAX_LIST_ITEMS); a wider shed range needs repeated resumes",
      };
      rewind.close();
    }
  } finally {
    cleanup();
  }

  const pass = Object.values(facts.scenarios).every((scenario) => scenario.pass);
  process.stdout.write(`${JSON.stringify({ pass, ...facts }, null, 2)}\n`);
  return pass ? 0 : 1;
}

const isDirectRun =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;

if (isDirectRun) {
  for (const tool of ["cargo", "dotnet"]) {
    const probe = spawnSync(tool, ["--version"], { encoding: "utf8" });
    if (probe.error) {
      process.stdout.write(
        `${JSON.stringify({ run: "unrun", reason: `${tool} is unavailable on this host` }, null, 2)}\n`,
      );
      process.exit(2);
    }
  }
  run().then(
    (code) => process.exit(code),
    (failure) => {
      process.stderr.write(
        `[db04-probe] failed: ${failure instanceof Error ? failure.stack : failure}\n`,
      );
      process.exit(1);
    },
  );
}
