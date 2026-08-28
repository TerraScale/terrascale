// TerraBase Studio dev fixture server (DB-02).
//
// Plain Node (node:http), no dependencies, no global tools. Implements the
// studio API contract in contract/terrabase-api.contract.json — the
// pre-landing shape of the SE-08 C# API host surface — so the studio wave
// can develop before the sync wave lands. When SE-08 lands, STUDIO_API_BASE_URL
// flips to the real host and this fixture keeps serving conformance vectors.
//
// The boundary is local-only (charter PD-2026-08-26-11): loopback bind, no
// authentication gate by default, no credentials on this surface, ever. The
// host-enforced operator-session bootstrap is simulated under fixture control
// (`session-gate`): typed operations answer `session.unauthenticated` with a
// `loginUrl` into this fixture's simulated login flow.
//
// Sessions tick server-side whether or not a connection is attached, so a
// dropped connection accumulates real missed deltas and a reconnect with
// Last-Event-ID replays exactly them, with their original seq identities.
//
// Run with: vp run fixture:serve

import { createHash, randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { pathToFileURL } from "node:url";

// DB-07 (TerraBase/TerraBaseV2#19): the topology ticket's synthetic-lane demo
// owns the `topology.*` route namespace served below.
import { handleTopologyOperation } from "../app/topology/fixtures/topology-demo.mjs";

const CONTRACT_VERSION = 2;
const CONTRACT_NAME = "terrabase.api.v2";

const OPERATIONS = new Set([
  "engine.status",
  "document.read",
  "document.write",
  "query.execute",
  "family.read",
  "family.write",
]);

// The closed engine family kind set — the reserved family record keys in
// src/families/state.rs (family_key). Unknown kinds fail closed.
const FAMILY_KINDS = new Set([
  "attachments",
  "counters",
  "time-series",
  "revisions",
  "compare-exchange",
  "identities",
  "lifecycle",
  "archival",
  "validation",
  "tombstones",
]);

const DEFAULTS = {
  host: process.env.STUDIO_FIXTURE_HOST ?? "127.0.0.1",
  port: Number(process.env.STUDIO_FIXTURE_PORT ?? 3032),
  tickIntervalMs: Number(process.env.FIXTURE_TICK_INTERVAL_MS ?? 250),
  ticksBeforeDrop: Number(process.env.FIXTURE_TICKS_BEFORE_DROP ?? 6),
  heartbeatIntervalMs: Number(process.env.FIXTURE_HEARTBEAT_INTERVAL_MS ?? 2000),
  sessionBufferFrames: Number(process.env.FIXTURE_SESSION_BUFFER ?? 4096),
  sessionIdleTtlMs: Number(process.env.FIXTURE_SESSION_IDLE_TTL_MS ?? 30_000),
  statusDelayMs: Number(process.env.FIXTURE_STATUS_DELAY_MS ?? 1200),
  maxSessions: 128,
};

const ENGINE = {
  engineId: "fixture-engine-01",
  engineVersion: "0.2.0-fixture",
  role: "single",
};

function canonicalJson(value) {
  if (value === null || typeof value === "number" || typeof value === "boolean") {
    return JSON.stringify(value);
  }
  if (typeof value === "string") {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => canonicalJson(item)).join(",")}]`;
  }
  const keys = Object.keys(value).sort();
  return `{${keys.map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(",")}}`;
}

function digestOf(payload) {
  const hex = createHash("sha256").update(canonicalJson(payload)).digest("hex");
  return `sha256:${hex}`;
}

function isoNow() {
  return new Date().toISOString();
}

function createFixtureState(options) {
  const documents = new Map(); // collection -> Map(id -> { value, version })
  const receipts = new Map(); // operationId -> CommitReceipt (idempotency)
  const familyStates = new Map(); // "<kind>:<scope>" -> { state, version }
  const sessions = new Map(); // sessionId -> session (LRU insertion order)
  // The backbone changes journal (DB-04): every change event the fixture's
  // /v1/streams surface serves — appended by typed writes and by fixture
  // control, replayed by sequence, mirrored on the real host's SSE
  // vocabulary (opened/batch/trigger/closed/drop).
  const streamJournal = [];
  const streamSubscribers = new Set();
  const stream = {
    nextSequence: 1,
    nextSubscriptionId: 0,
    hold: false,
    poisonNext: false,
    subscribers: streamSubscribers,
    journal: streamJournal,
  };
  const state = {
    startedAt: isoNow(),
    statusBehavior: "status-ok", // "status-ok" | "status-slow" | "status-fail"
    sessionGate: false, // simulated host-enforced operator session
    framePoison: false,
    counters: { reads: 0, writes: 0, queries: 0, familyOperations: 0 },
    documents,
    receipts,
    familyStates,
    sessions,
    stream,
  };

  const seed = new Map();
  seed.set("seed-001", { value: { kind: "studio-seed", label: "first" }, version: 3 });
  seed.set("seed-002", { value: { kind: "studio-seed", label: "second" }, version: 1 });
  documents.set("studio-seed", seed);
  const parity = new Map();
  parity.set("parity-001", { value: { kind: "parity-check", source: "v1" }, version: 7 });
  documents.set("parity", parity);
  familyStates.set("counters:studio", { state: { entries: 2, totals: { reads: 0 } }, version: 5 });

  state.reset = () => {
    state.statusBehavior = "status-ok";
    state.sessionGate = false;
    state.framePoison = false;
    state.stream.hold = false;
  };

  state.storageStats = () => {
    let docCount = 0;
    for (const docs of documents.values()) {
      docCount += docs.size;
    }
    return {
      collections: documents.size,
      documents: docCount,
      walBytes: 4096 + docCount * 512,
    };
  };

  state.engineStatus = () => ({
    engineId: ENGINE.engineId,
    engineVersion: ENGINE.engineVersion,
    role: ENGINE.role,
    startedAt: state.startedAt,
    uptimeMs: Math.max(0, Date.now() - Date.parse(state.startedAt)),
    storage: state.storageStats(),
    counters: { ...state.counters },
  });

  state.collectionSummaries = () =>
    [...documents.entries()]
      .map(([name, docs]) => ({ name, documents: docs.size, sizeBytes: docs.size * 256 }))
      .sort((a, b) => a.name.localeCompare(b.name));

  state.findDocument = (collection, documentId) => {
    const docs = documents.get(collection);
    const found = docs?.get(documentId);
    if (found === undefined) {
      return null;
    }
    return { id: documentId, collection, version: found.version, value: found.value };
  };

  // Returns the durable receipt on success, { conflict } on version mismatch;
  // an operationId seen before replays the original receipt verbatim.
  state.writeDocument = (request) => {
    const prior = receipts.get(request.operationId);
    if (prior !== undefined) {
      return { receipt: prior };
    }
    const { document, expectedVersion } = request;
    let docs = documents.get(document.collection);
    if (docs === undefined) {
      docs = new Map();
      documents.set(document.collection, docs);
    }
    const existing = docs.get(document.id);
    if (expectedVersion !== undefined && expectedVersion !== null && existing !== undefined) {
      if (existing.version !== expectedVersion) {
        return { conflict: true, expected: expectedVersion, actual: existing.version };
      }
    }
    const version = (existing?.version ?? 0) + 1;
    docs.set(document.id, { value: document.value, version });
    // The write publishes on the backbone (DB-04): the fixture's changes
    // stream carries the same content-free put event the wire carries.
    state.emitStreamEvent(
      { kind: "put", collection: document.collection, document: document.id },
      request.operationId,
    );
    const receipt = {
      operationId: request.operationId,
      collection: document.collection,
      documentId: document.id,
      version,
      committedAt: isoNow(),
    };
    receipts.set(request.operationId, receipt);
    return { receipt };
  };

  // Appends one change event to the backbone journal and fans it out to
  // every matching stream subscriber (the DB-04 live surface). Content-free
  // by construction: identities only, never a body.
  state.emitStreamEvent = (partial, operationId = `fixture-${stream.nextSequence}`) => {
    const event = {
      sequence: stream.nextSequence,
      operationId,
      kind: partial.kind,
      collection: partial.collection ?? null,
      document: partial.document ?? null,
      conflict: partial.conflict ?? null,
    };
    stream.nextSequence += 1;
    streamJournal.push(event);
    for (const subscriber of streamSubscribers) {
      subscriber.deliver(event);
    }
    return event;
  };

  state.readFamily = (request) => {
    const key = `${request.kind}:${request.scope}`;
    const entry = familyStates.get(key) ?? { state: { entries: 0 }, version: 0 };
    return { kind: request.kind, scope: request.scope, state: entry.state };
  };

  state.writeFamily = (request) => {
    const prior = receipts.get(request.operationId);
    if (prior !== undefined) {
      return { receipt: prior };
    }
    const key = `${request.kind}:${request.scope}`;
    const entry = familyStates.get(key) ?? { state: { entries: 0 }, version: 0 };
    entry.version += 1;
    entry.state = { ...entry.state, ...request.operation, updatedAt: isoNow() };
    familyStates.set(key, entry);
    // Family writes publish their backbone kinds (SE-03's parity mapping:
    // counter watch ← family counter writes, time-series watch ← family
    // time-series writes).
    if (request.kind === "counters") {
      state.emitStreamEvent({ kind: "counter", collection: request.scope }, request.operationId);
    } else if (request.kind === "time-series") {
      state.emitStreamEvent(
        { kind: "time-series", collection: request.scope },
        request.operationId,
      );
    }
    const receipt = {
      operationId: request.operationId,
      collection: `family:${request.kind}`,
      documentId: request.scope,
      version: entry.version,
      committedAt: isoNow(),
    };
    receipts.set(request.operationId, receipt);
    return { receipt };
  };

  // Sessions tick server-side for as long as they are alive, so a dropped
  // connection accumulates missed deltas in the session buffer and a
  // reconnect replays exactly them.
  state.acquireSession = (sessionId) => {
    const known =
      sessionId !== undefined && sessionId !== null ? sessions.get(sessionId) : undefined;
    if (known !== undefined) {
      sessions.delete(sessionId);
      sessions.set(sessionId, known); // refresh LRU position
      return known;
    }
    while (sessions.size >= options.maxSessions) {
      const oldest = sessions.keys().next().value;
      state.stopSessionTimers(sessions.get(oldest));
      sessions.delete(oldest);
    }
    const session = {
      sessionId: randomUUID(),
      seq: 0,
      counter: 0,
      buffer: [],
      bufferCap: options.sessionBufferFrames,
      connections: new Set(),
      tickTimer: null,
      heartbeatTimer: null,
      idleTimer: null,
      lastIdleAt: Date.now(),
    };
    sessions.set(session.sessionId, session);
    return session;
  };

  state.stopSessionTimers = (session) => {
    if (session === undefined) {
      return;
    }
    for (const key of ["tickTimer", "heartbeatTimer", "idleTimer"]) {
      if (session[key] !== null) {
        clearInterval(session[key]);
        session[key] = null;
      }
    }
  };

  state.dispose = () => {
    for (const session of sessions.values()) {
      state.stopSessionTimers(session);
      for (const connection of session.connections) {
        connection.res.end();
      }
      session.connections.clear();
    }
    sessions.clear();
    for (const subscriber of streamSubscribers) {
      subscriber.close();
    }
    streamSubscribers.clear();
    streamJournal.length = 0;
  };

  return state;
}

function sendJson(res, status, body, extraHeaders = {}) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    "content-type": "application/json",
    "cache-control": "no-store",
    ...corsHeaders(),
    ...extraHeaders,
  });
  res.end(payload);
}

function corsHeaders() {
  // Dev fixture only: the studio app and the fixture live on separate local
  // ports during development. The real SE-08 host owns its own CORS policy.
  return {
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "content-type, last-event-id, x-tb-principal, x-tb-tenant",
  };
}

function errorEnvelope(op, correlationId, code, message, retryable, status, extra = {}) {
  return {
    status,
    body: {
      v: CONTRACT_VERSION,
      op,
      correlationId,
      error: { code, message, retryable, ...extra },
    },
  };
}

async function readBody(req, limitBytes = 1024 * 1024) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > limitBytes) {
      throw new Error("body too large");
    }
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString("utf8");
}

function makeFrame(session, type, payload, { buffer = true } = {}) {
  const seq = session.seq + 1;
  session.seq = seq;
  const frame = {
    v: CONTRACT_VERSION,
    type,
    seq,
    correlationId: null,
    causationId: null,
    idempotencyKey: null,
    digest: digestOf(payload),
    payload,
  };
  if (buffer) {
    session.buffer.push({ seq, frame });
    const cap = session.bufferCap ?? DEFAULTS.sessionBufferFrames;
    if (session.buffer.length > cap) {
      session.buffer.splice(0, session.buffer.length - cap);
    }
  }
  return frame;
}

function broadcast(session, frame) {
  for (const connection of session.connections) {
    writeFrameToConnection(connection, frame);
  }
}

function writeFrameToConnection(connection, frame, { liveDelta = true } = {}) {
  const { res, session } = connection;
  if (res.writableEnded || res.destroyed) {
    session.connections.delete(connection);
    return;
  }
  // The SSE id carries the SE-04 Sync Checkpoint ('<sessionId>:<seq>') so the
  // browser's automatic reconnect path resumes from the last seen frame.
  res.write(`id: ${session.sessionId}:${frame.seq}\n`);
  res.write(`data: ${JSON.stringify(frame)}\n\n`);
  if (frame.type === "delta" && liveDelta) {
    connection.deltasDelivered += 1;
    if (connection.dropAfter > 0 && connection.deltasDelivered >= connection.dropAfter) {
      // Simulated dropped connection: the session keeps ticking server-side,
      // so the reconnect replays the deltas missed while disconnected.
      // Replayed deltas do not count toward the drop: a resume must not be
      // dropped again for the very backlog it is replaying.
      dropConnection(connection);
    }
  }
}

function dropConnection(connection) {
  const { res, session } = connection;
  session.connections.delete(connection);
  if (session.connections.size === 0) {
    session.lastIdleAt = Date.now();
  }
  if (!res.writableEnded && !res.destroyed) {
    res.end();
  }
}

function ensureSessionTimers(state, session, options) {
  if (session.tickTimer !== null) {
    return;
  }
  session.tickTimer = setInterval(() => {
    if (state.framePoison && session.connections.size > 0) {
      state.framePoison = false;
      // One deliberately unknown frame type under fixture control: clients
      // must prove fail-closed rejection (counted and surfaced). Unbuffered:
      // a poison identity never becomes part of a resumable checkpoint. The
      // flag stays armed while the connection is down so a test arming it
      // during a simulated drop still sees the frame after reconnect.
      broadcast(session, makeFrame(session, "bogus.frame", { poison: true }, { buffer: false }));
    }
    const seq = session.seq + 1;
    const payload = { seq, emittedAt: isoNow(), counter: (session.counter += 1) };
    broadcast(session, makeFrame(session, "delta", payload));
  }, options.tickIntervalMs);
  session.heartbeatTimer = setInterval(() => {
    broadcast(session, makeFrame(session, "heartbeat", { at: isoNow() }));
  }, options.heartbeatIntervalMs);
  session.idleTimer = setInterval(() => {
    if (
      session.connections.size === 0 &&
      Date.now() - session.lastIdleAt > options.sessionIdleTtlMs
    ) {
      state.stopSessionTimers(session);
    }
  }, 1000);
}

function handleTicksSubscription(req, res, url, state, options) {
  res.writeHead(200, {
    "content-type": "text/event-stream",
    "cache-control": "no-cache, no-transform",
    connection: "keep-alive",
    "x-accel-buffering": "no",
    ...corsHeaders(),
  });
  res.write("retry: 1000\n\n");

  // The browser sends Last-Event-ID automatically on reconnect of the same
  // EventSource; clients that had to recreate the source pass the same
  // checkpoint as a query parameter.
  const headerId = req.headers["last-event-id"];
  const lastEventId =
    typeof headerId === "string" && headerId.length > 0
      ? headerId
      : url.searchParams.get("lastEventId");

  let resumeSessionId = null;
  let resumeSeq = null;
  if (typeof lastEventId === "string" && lastEventId.length > 0) {
    const separator = lastEventId.lastIndexOf(":");
    if (separator > 0) {
      resumeSessionId = lastEventId.slice(0, separator);
      resumeSeq = Number.parseInt(lastEventId.slice(separator + 1), 10);
    }
  }

  const hasResume = resumeSessionId !== null && state.sessions.has(resumeSessionId);
  let checkpointEvicted = false;
  if (hasResume && Number.isFinite(resumeSeq)) {
    const existing = state.sessions.get(resumeSessionId);
    const oldestBuffered = existing.buffer.length > 0 ? existing.buffer[0].seq : existing.seq;
    if (resumeSeq + 1 < oldestBuffered) {
      checkpointEvicted = true;
    }
  }
  if (
    typeof lastEventId === "string" &&
    lastEventId.length > 0 &&
    (!hasResume || checkpointEvicted)
  ) {
    // Unknown or evicted checkpoint: never silently partial-replay.
    const ephemeral = {
      sessionId: resumeSessionId ?? "unknown",
      seq: 0,
      counter: 0,
      buffer: [],
      connections: new Set(),
    };
    const frame = makeFrame(ephemeral, "resync.required", {
      reason: !hasResume ? "checkpoint-unknown" : "checkpoint-evicted",
      lastSeq: Number.isFinite(resumeSeq) ? resumeSeq : 0,
    });
    res.write(`id: ${ephemeral.sessionId}:0\n`);
    res.write(`data: ${JSON.stringify(frame)}\n\n`);
    res.end();
    return;
  }

  const session = state.acquireSession(hasResume ? resumeSessionId : undefined);
  const checkpointSeq = hasResume && Number.isFinite(resumeSeq) ? resumeSeq : session.seq;
  // High-water mark of replayable content: frames buffered before this
  // connection's own session.opened/subscription.established identities.
  const replayHighWater = session.seq;
  // Snapshot the missed frames before the new connection frames enter the
  // buffer, so replay carries exactly the pre-reconnect backlog.
  const replayBacklog = hasResume ? session.buffer.filter(({ seq }) => seq > checkpointSeq) : [];

  const connection = {
    res,
    session,
    deltasDelivered: 0,
    dropAfter: options.ticksBeforeDrop > 0 ? options.ticksBeforeDrop : Number.POSITIVE_INFINITY,
  };
  session.connections.add(connection);

  writeFrameToConnection(
    connection,
    makeFrame(session, "session.opened", {
      sessionId: session.sessionId,
      resumable: true,
      heartbeatIntervalMs: options.heartbeatIntervalMs,
    }),
  );
  writeFrameToConnection(
    connection,
    makeFrame(session, "subscription.established", {
      subscriptionId: session.sessionId,
      checkpoint: `${session.sessionId}:${checkpointSeq}`,
      lastSeq: replayHighWater,
    }),
  );

  if (hasResume && Number.isFinite(resumeSeq)) {
    for (const { frame } of replayBacklog) {
      writeFrameToConnection(connection, frame, { liveDelta: false });
    }
  }

  ensureSessionTimers(state, session, options);
  req.on("close", () => dropConnection(connection));
}

function fixtureLoginPage() {
  return `<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Host login (fixture)</title></head>
<body data-fixture="host-login">
  <main>
    <h1>Host login flow (simulated)</h1>
    <p>This page simulates the host-enforced operator login flow. The studio
    session bootstrap redirected here because the fixture answered
    <code>session.unauthenticated</code>. No credentials exist on this
    surface.</p>
  </main>
</body>
</html>
`;
}

// The DB-04 backbone changes stream: the fixture's mirror of the real C#
// API host's GET /v1/streams SSE surface (host/TerraBase.Api
// TypedApi.StreamSseAsync over BackboneHub) — same named events (opened,
// batch, trigger, closed, drop), same SSE id semantics (the last served
// sequence; a drop marker's id is resumeFrom - 1), same resume rules
// (from query inclusive, Last-Event-ID exclusive), same backend-context
// gate, same content-free events. The fixture adds the controlled
// behaviors the tests drive deterministically: emitting every selector
// kind, holding delivery, shedding a held queue oldest-first with an
// in-band drop marker, re-delivering served identities (at-least-once),
// an unknown event name, and connection drops.
function handleChangesStream(req, res, url, state) {
  // The local gate, mirroring the real host exactly: no backend-supplied
  // context, no engine frame (PD-2026-08-26-11 D1).
  const principal = req.headers["x-tb-principal"];
  const tenant = req.headers["x-tb-tenant"];
  if (
    typeof principal !== "string" ||
    principal.length === 0 ||
    typeof tenant !== "string" ||
    tenant.length === 0
  ) {
    sendJson(res, 401, {
      code: "unauthorized",
      detail: "backend-supplied context (X-TB-Principal, X-TB-Tenant) is required",
    });
    return;
  }

  const collectionsParam = url.searchParams.get("collections");
  const collections =
    collectionsParam !== null && collectionsParam.length > 0
      ? collectionsParam
          .split(",")
          .map((name) => name.trim())
          .filter((name) => name.length > 0)
      : null;
  // Resume: the from query is an inclusive replay start; Last-Event-ID
  // names the last event seen, so the resume point is strictly after it.
  const fromQuery = url.searchParams.get("from");
  const lastEventId = req.headers["last-event-id"];
  let from = null;
  if (fromQuery !== null && fromQuery.length > 0 && Number.isFinite(Number(fromQuery))) {
    from = Math.max(1, Math.trunc(Number(fromQuery)));
  } else if (typeof lastEventId === "string" && lastEventId.length > 0) {
    const parsed = Number.parseInt(lastEventId, 10);
    if (Number.isFinite(parsed)) {
      from = parsed + 1;
    }
  }

  res.writeHead(200, {
    "content-type": "text/event-stream",
    "cache-control": "no-cache, no-transform",
    connection: "keep-alive",
    "x-accel-buffering": "no",
    ...corsHeaders(),
  });
  res.write("retry: 1000\n\n");

  const subscriptionId = ++state.stream.nextSubscriptionId;
  const matches = (event) => {
    if (collections === null) {
      return true;
    }
    // SE-03's recorded limit, mirrored: conflict events carry no
    // collection identity, so a collection-refined filter does not
    // observe them.
    if (event.kind === "conflict") {
      return false;
    }
    return event.collection !== null && collections.includes(event.collection);
  };

  const subscriber = {
    res,
    subscriptionId,
    queued: [],
    deliveredBatches: [],
    dropped: 0,
    write(frame) {
      if (!res.destroyed) {
        res.write(frame);
      }
    },
    batch(events, { replay = false } = {}) {
      if (events.length === 0) {
        return;
      }
      const payload = {
        subscriptionId,
        fromSequence: events[0].sequence,
        events,
        nextSequence: events[events.length - 1].sequence + 1,
      };
      const frame = `id: ${payload.nextSequence - 1}\nevent: batch\ndata: ${JSON.stringify(payload)}\n\n`;
      if (!replay) {
        this.deliveredBatches.push(frame);
        if (this.deliveredBatches.length > 16) {
          this.deliveredBatches.shift();
        }
      }
      if (state.stream.hold) {
        this.queued.push({ frame, events });
        return;
      }
      this.write(frame);
      if (replay) {
        return;
      }
      // The content-free trigger rides after the live batch (the push-hint
      // contract, PD-2026-08-26-13 D3).
      this.write(
        `id: ${payload.nextSequence - 1}\nevent: trigger\ndata: ${JSON.stringify({
          subscriptionId,
          collection: events[0].collection ?? "",
          sequence: events[events.length - 1].sequence,
          operationId: events[events.length - 1].operationId,
        })}\n\n`,
      );
    },
    deliver(event) {
      if (!matches(event)) {
        return;
      }
      this.batch([event]);
    },
    close() {
      this.res.destroy();
    },
  };
  state.stream.subscribers.add(subscriber);

  subscriber.write(
    `id: ${Math.max(0, (from ?? state.stream.nextSequence) - 1)}\nevent: opened\ndata: ${JSON.stringify(
      {
        subscriptionId,
        startSequence: from ?? state.stream.nextSequence,
        collections,
      },
    )}\n\n`,
  );

  // The replayed backlog: ONE batch from the journal, exactly the host's
  // single replay batch at subscribe (the client classifies a resumed
  // connection's first batch as the backlog).
  if (from !== null) {
    const replay = state.stream.journal.filter((event) => event.sequence >= from && matches(event));
    subscriber.batch(replay, { replay: true });
  }

  req.on("close", () => {
    state.stream.subscribers.delete(subscriber);
  });
}

export function createFixtureServer(options = {}) {
  const resolved = { ...DEFAULTS, ...options };
  const state = createFixtureState(resolved);

  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
    const path = url.pathname;

    try {
      if (req.method === "OPTIONS") {
        res.writeHead(204, corsHeaders());
        res.end();
        return;
      }

      if (req.method === "GET" && path === "/fixture/login") {
        res.writeHead(200, {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "no-store",
        });
        res.end(fixtureLoginPage());
        return;
      }

      if (req.method === "GET" && path === "/api/v2/_fixture/health") {
        sendJson(res, 200, { ok: true, contract: CONTRACT_NAME, version: CONTRACT_VERSION });
        return;
      }

      if (req.method === "GET" && path === "/api/v2/subscriptions/ticks") {
        handleTicksSubscription(req, res, url, state, resolved);
        return;
      }

      // The real-host backbone stream surface (DB-04): same route, same
      // vocabulary, same gate as the C# host's /v1/streams.
      if (req.method === "GET" && path === "/v1/streams") {
        handleChangesStream(req, res, url, state);
        return;
      }

      // Topology reads (DB-07, TerraBase/TerraBaseV2#19): the `topology.*`
      // route namespace is owned by the topology ticket and delegated to its
      // synthetic-lane demo module; every other route below is unchanged.
      if (
        await handleTopologyOperation(
          { method: req.method, pathname: path },
          {
            req,
            res,
            url,
            state,
            options: resolved,
            helpers: { sendJson, errorEnvelope, readBody },
          },
        )
      ) {
        return;
      }

      if (path.startsWith("/api/v2/operations/")) {
        const op = path.slice("/api/v2/operations/".length);
        if (req.method !== "POST") {
          sendJson(res, 405, {
            v: CONTRACT_VERSION,
            op,
            correlationId: null,
            error: {
              code: "method.not-allowed",
              message: "Operations accept POST only.",
              retryable: false,
            },
          });
          return;
        }

        let body;
        try {
          body = JSON.parse((await readBody(req)) || "null");
        } catch {
          const failure = errorEnvelope(
            op,
            null,
            "protocol.envelope-invalid",
            "Request body is not JSON.",
            false,
            400,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }

        const correlationId = body?.correlationId;
        if (body?.v !== CONTRACT_VERSION) {
          const failure = errorEnvelope(
            op,
            typeof correlationId === "string" ? correlationId : null,
            "protocol.version-unsupported",
            `Envelope version ${JSON.stringify(body?.v)} is not supported; this host speaks v${CONTRACT_VERSION}.`,
            false,
            400,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }
        if (typeof correlationId !== "string" || correlationId.length === 0 || body.op !== op) {
          const failure = errorEnvelope(
            op,
            typeof correlationId === "string" ? correlationId : null,
            "protocol.envelope-invalid",
            "Envelope requires v, op matching the route, and a non-empty correlationId.",
            false,
            400,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }

        if (!OPERATIONS.has(op)) {
          const failure = errorEnvelope(
            op,
            correlationId,
            "operation.unknown",
            `Operation '${op}' is not part of ${CONTRACT_NAME}.`,
            false,
            404,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }

        // Host-enforced operator session (simulated): the studio's session
        // bootstrap sees this typed error and honors the loginUrl redirect.
        if (state.sessionGate) {
          const failure = errorEnvelope(
            op,
            correlationId,
            "session.unauthenticated",
            "The host requires an operator session (fixture behavior: session-gate).",
            false,
            401,
            { loginUrl: `/fixture/login` },
          );
          sendJson(res, failure.status, failure.body);
          return;
        }

        if (op === "engine.status") {
          if (state.statusBehavior === "status-fail") {
            const failure = errorEnvelope(
              op,
              correlationId,
              "engine.unavailable",
              "The engine is not reachable (fixture behavior: status-fail).",
              true,
              503,
            );
            sendJson(res, failure.status, failure.body);
            return;
          }
          if (state.statusBehavior === "status-slow") {
            await new Promise((resolve) => setTimeout(resolve, resolved.statusDelayMs));
          }
          state.counters.reads += 1;
          sendJson(res, 200, {
            v: CONTRACT_VERSION,
            op,
            correlationId,
            result: state.engineStatus(),
          });
          return;
        }

        const request = body.request ?? {};

        if (op === "document.read") {
          if (
            typeof request.collection !== "string" ||
            typeof request.documentId !== "string" ||
            request.collection.length === 0 ||
            request.documentId.length === 0
          ) {
            const failure = errorEnvelope(
              op,
              correlationId,
              "request.invalid",
              "document.read requires collection and documentId strings.",
              false,
              400,
            );
            sendJson(res, failure.status, failure.body);
            return;
          }
          state.counters.reads += 1;
          const document = state.findDocument(request.collection, request.documentId);
          sendJson(res, 200, { v: CONTRACT_VERSION, op, correlationId, result: { document } });
          return;
        }

        if (op === "document.write") {
          state.counters.writes += 1;
          const outcome = state.writeDocument(request);
          if (outcome.conflict === true) {
            const failure = errorEnvelope(
              op,
              correlationId,
              "write.conflict",
              `Expected version ${outcome.expected} but found ${outcome.actual}.`,
              false,
              409,
            );
            sendJson(res, failure.status, failure.body);
            return;
          }
          sendJson(res, 200, { v: CONTRACT_VERSION, op, correlationId, result: outcome.receipt });
          return;
        }

        if (op === "query.execute") {
          state.counters.queries += 1;
          const text = request?.input?.kind === "v1" ? String(request.input.text ?? "") : "";
          if (/collection/i.test(text)) {
            const summaries = state.collectionSummaries();
            sendJson(res, 200, {
              v: CONTRACT_VERSION,
              op,
              correlationId,
              result: {
                rows: summaries.map((summary) => ({
                  document: {
                    id: `catalog:${summary.name}`,
                    collection: "__catalog__",
                    version: 1,
                    value: summary,
                  },
                  projected: [{ name: "name", value: summary.name }],
                })),
                matched: summaries.length,
                staleness: [],
              },
            });
            return;
          }
          sendJson(res, 200, {
            v: CONTRACT_VERSION,
            op,
            correlationId,
            result: { rows: [], matched: 0, staleness: [] },
          });
          return;
        }

        if (op === "family.read" || op === "family.write") {
          if (typeof request.kind !== "string" || !FAMILY_KINDS.has(request.kind)) {
            const failure = errorEnvelope(
              op,
              correlationId,
              "family.kind-unknown",
              `Family kind ${JSON.stringify(request.kind)} is not part of the closed FamilyKind set.`,
              false,
              400,
            );
            sendJson(res, failure.status, failure.body);
            return;
          }
          state.counters.familyOperations += 1;
          if (op === "family.read") {
            sendJson(res, 200, {
              v: CONTRACT_VERSION,
              op,
              correlationId,
              result: state.readFamily(request),
            });
            return;
          }
          const outcome = state.writeFamily(request);
          sendJson(res, 200, { v: CONTRACT_VERSION, op, correlationId, result: outcome.receipt });
          return;
        }
      }

      if (req.method === "POST" && path === "/api/v2/_fixture/control") {
        const body = JSON.parse((await readBody(req)) || "{}");
        if (["status-ok", "status-slow", "status-fail"].includes(body.behavior)) {
          state.statusBehavior = body.behavior;
        }
        if (body.behavior === "session-gate") {
          state.sessionGate = true;
        }
        if (body.behavior === "session-open" || body.behavior === "session-ungate") {
          state.sessionGate = false;
        }
        if (body.framePoison === true) {
          state.framePoison = true;
        }
        // The DB-04 changes-stream controls (deterministic test drivers
        // for the live surface's behaviors; see handleChangesStream).
        if (Array.isArray(body.streamEmit)) {
          for (const partial of body.streamEmit) {
            state.emitStreamEvent(partial);
          }
        }
        if (body.streamHold === true) {
          state.stream.hold = true;
        }
        if (body.streamShed === true) {
          state.stream.hold = false;
          for (const subscriber of state.stream.subscribers) {
            if (subscriber.queued.length === 0) {
              continue;
            }
            const shed = subscriber.queued;
            subscriber.queued = [];
            subscriber.dropped += shed.length;
            const resumeFrom = shed[0].events[0].sequence;
            // The in-band drop marker, exactly the host's policy: the
            // marker's id is the boundary minus one, so a consumer that
            // rewinds there re-delivers the missed range.
            subscriber.write(
              `id: ${resumeFrom - 1}\nevent: drop\ndata: ${JSON.stringify({
                subscriptionId: subscriber.subscriptionId,
                resumeFrom,
                dropped: subscriber.dropped,
              })}\n\n`,
            );
          }
        }
        if (body.streamRedeliver === true) {
          // At-least-once in action: the served identities return with
          // their original sequences.
          for (const subscriber of state.stream.subscribers) {
            const last = subscriber.deliveredBatches.at(-1);
            if (last !== undefined) {
              subscriber.write(last);
            }
          }
        }
        if (body.streamDropConnections === true) {
          for (const subscriber of state.stream.subscribers) {
            subscriber.close();
          }
          state.stream.subscribers.clear();
        }
        if (body.streamPoisonEvent === true) {
          for (const subscriber of state.stream.subscribers) {
            subscriber.write(`event: surprise\ndata: {}\n\n`);
          }
        }
        if (body.reset === true) {
          state.reset();
        }
        sendJson(res, 200, {
          ok: true,
          statusBehavior: state.statusBehavior,
          sessionGate: state.sessionGate,
          framePoison: state.framePoison,
          streamSubscribers: state.stream.subscribers.size,
          streamSequence: state.stream.nextSequence - 1,
        });
        return;
      }

      const failure = errorEnvelope(
        null,
        null,
        "route.unknown",
        `No route for ${req.method} ${path}.`,
        false,
        404,
      );
      sendJson(res, failure.status, failure.body);
    } catch (error) {
      const failure = errorEnvelope(
        null,
        null,
        "fixture.internal",
        String(error?.message ?? error),
        false,
        500,
      );
      if (!res.headersSent) {
        sendJson(res, failure.status, failure.body);
      } else {
        res.end();
      }
    }
  });

  return { server, state, options: resolved };
}

const isDirectRun =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isDirectRun) {
  const { server, state, options } = createFixtureServer();
  server.listen(options.port, options.host, () => {
    console.log(`[studio-fixture] ${CONTRACT_NAME} on http://${options.host}:${options.port}`);
    console.log(
      `[studio-fixture] ticks every ${options.tickIntervalMs}ms, drop after ${options.ticksBeforeDrop} deltas (0 = never), buffer ${options.sessionBufferFrames} frames`,
    );
  });
  const shutdown = () => {
    state.dispose();
    server.closeAllConnections();
    server.close(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
