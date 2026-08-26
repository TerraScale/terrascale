// Runs the stub-first conformance vectors from
// contract/conformance-vectors.json against the in-repo fixture server
// (scripts/fixture-server.mjs), in-process, on ephemeral loopback ports.
//
// The digest discipline below is an independent re-implementation (not shared
// with the fixture) so the vectors cross-check the envelope contract itself.
//
// Run with: vp run conformance:vectors

import { createHash, randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { createFixtureServer } from "./fixture-server.mjs";

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const vectors = JSON.parse(
  await readFile(resolve(studioRoot, "contract", "conformance-vectors.json"), "utf8"),
);

class AssertionError extends Error {}

function assert(condition, message) {
  if (!condition) {
    throw new AssertionError(message);
  }
}

function canonicalJson(value) {
  if (
    value === null ||
    typeof value === "number" ||
    typeof value === "boolean" ||
    typeof value === "string"
  ) {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => canonicalJson(item)).join(",")}]`;
  }
  return `{${Object.keys(value)
    .sort()
    .map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`)
    .join(",")}}`;
}

function digestOf(payload) {
  return `sha256:${createHash("sha256").update(canonicalJson(payload)).digest("hex")}`;
}

async function startFixture(options) {
  const { server, state } = createFixtureServer({ host: "127.0.0.1", ...options });
  await new Promise((resolveListen) => server.listen(0, "127.0.0.1", resolveListen));
  const address = server.address();
  const baseUrl = `http://127.0.0.1:${address.port}`;
  return { server, state, baseUrl };
}

async function callOperation(
  baseUrl,
  op,
  request,
  { version = 2, opOverride = null, correlationId = randomUUID() } = {},
) {
  const response = await fetch(`${baseUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: version,
      op: opOverride ?? op,
      correlationId,
      causationId: null,
      idempotencyKey: null,
      request,
    }),
  });
  const body = await response.json();
  return { status: response.status, body, correlationId };
}

function parseSseBlock(block) {
  let id = null;
  const dataLines = [];
  for (const line of block.split("\n")) {
    if (line.startsWith("id:")) {
      id = line.slice(3).trim();
    } else if (line.startsWith("data:")) {
      dataLines.push(line.slice(5).trimStart());
    }
  }
  if (dataLines.length === 0) {
    return null;
  }
  return { id, frame: JSON.parse(dataLines.join("\n")) };
}

function createSseReader(response) {
  const messages = [];
  let waiters = [];
  let closed = false;

  void (async () => {
    const decoder = new TextDecoder();
    let buffer = "";
    try {
      for await (const chunk of response.body) {
        buffer += decoder.decode(chunk, { stream: true });
        let separator = buffer.indexOf("\n\n");
        while (separator >= 0) {
          const block = buffer.slice(0, separator);
          buffer = buffer.slice(separator + 2);
          const parsed = parseSseBlock(block);
          if (parsed !== null) {
            messages.push(parsed);
            const current = waiters;
            waiters = [];
            for (const waiter of current) {
              waiter();
            }
          }
          separator = buffer.indexOf("\n\n");
        }
      }
    } finally {
      closed = true;
      const current = waiters;
      waiters = [];
      for (const waiter of current) {
        waiter();
      }
    }
  })();

  return {
    async next(timeoutMs = 5000) {
      const deadline = Date.now() + timeoutMs;
      while (messages.length === 0 && !closed && Date.now() < deadline) {
        await new Promise((resolveWait) => {
          waiters.push(resolveWait);
          setTimeout(resolveWait, 50);
        });
      }
      const message = messages.shift();
      assert(message !== undefined, "timed out waiting for an SSE frame");
      return message;
    },
    async waitForClose(timeoutMs = 5000) {
      const deadline = Date.now() + timeoutMs;
      while (!closed && Date.now() < deadline) {
        await new Promise((resolveWait) => setTimeout(resolveWait, 25));
      }
      assert(closed, "connection did not close within the window");
    },
    get isClosed() {
      return closed;
    },
  };
}

async function openTicks(baseUrl, lastEventId = null) {
  const response = await fetch(`${baseUrl}/api/v2/subscriptions/ticks`, {
    headers: lastEventId === null ? {} : { "last-event-id": lastEventId },
  });
  assert(response.ok, `ticks subscription failed: HTTP ${response.status}`);
  assert(
    (response.headers.get("content-type") ?? "").startsWith("text/event-stream"),
    "ticks subscription is not text/event-stream",
  );
  return createSseReader(response);
}

function assertValidFrame(frame) {
  assert(frame.v === 2, `frame envelope version is ${JSON.stringify(frame.v)}`);
  assert(typeof frame.type === "string" && frame.type.length > 0, "frame type missing");
  assert(typeof frame.seq === "number" && Number.isFinite(frame.seq), "frame seq invalid");
  assert(typeof frame.digest === "string", "frame digest missing");
  assert(
    digestOf(frame.payload ?? null) === frame.digest,
    `digest mismatch on ${frame.type} frame`,
  );
}

const vectorRunners = {
  "status-read-ok": async (baseUrl) => {
    const { status, body, correlationId } = await callOperation(baseUrl, "engine.status", {});
    assert(status === 200, `expected 200, got ${status}`);
    assert(body.v === 2, "response envelope version is not 2");
    assert(body.op === "engine.status", "response op mismatch");
    assert(body.correlationId === correlationId, "correlationId not echoed");
    const result = body.result ?? {};
    for (const field of ["engineId", "engineVersion", "role", "startedAt", "uptimeMs"]) {
      assert(result[field] !== undefined, `EngineStatus.${field} missing`);
    }
    assert(
      ["single", "leader", "follower"].includes(result.role),
      `role ${result.role} not in enum`,
    );
    assert(!Number.isNaN(Date.parse(result.startedAt)), "startedAt is not ISO-8601");
    for (const group of ["storage", "counters"]) {
      assert(
        typeof result[group] === "object" && result[group] !== null,
        `EngineStatus.${group} missing`,
      );
    }
  },

  "correlation-echo": async (baseUrl) => {
    const { status, body, correlationId } = await callOperation(baseUrl, "document.read", {
      collection: "studio-seed",
      documentId: "seed-001",
    });
    assert(status === 200, `expected 200, got ${status}`);
    assert(body.correlationId === correlationId, "correlationId not echoed exactly");
    assert(body.result?.document?.id === "seed-001", "seed document not returned");
  },

  "unknown-operation-rejected": async (baseUrl) => {
    const { status, body } = await callOperation(baseUrl, "not.an.operation", {});
    assert(status === 404, `expected 404, got ${status}`);
    assert(body.error?.code === "operation.unknown", `error code is ${body.error?.code}`);
    assert(body.error?.retryable === false, "operation.unknown must not be retryable");
  },

  "malformed-envelope-rejected": async (baseUrl) => {
    const { status, body } = await callOperation(baseUrl, "engine.status", {}, { version: 1 });
    assert(status === 400, `expected 400, got ${status}`);
    assert(
      body.error?.code === "protocol.version-unsupported",
      `error code is ${body.error?.code}`,
    );
  },

  "correlation-mismatch-rejected": async (baseUrl) => {
    const { status, body } = await callOperation(
      baseUrl,
      "engine.status",
      {},
      { opOverride: "query.execute" },
    );
    assert(status === 400, `expected 400, got ${status}`);
    assert(body.error?.code === "protocol.envelope-invalid", `error code is ${body.error?.code}`);
  },

  "query-v1-ok": async (baseUrl) => {
    const { status, body } = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: "FROM collections" },
    });
    assert(status === 200, `expected 200, got ${status}`);
    const result = body.result ?? {};
    assert(result.matched > 0, "matched must be > 0");
    assert(Array.isArray(result.rows), "rows must be an array");
    const first = result.rows[0];
    assert(typeof first?.document?.id === "string", "row document.id must be a string");
    assert(
      typeof first?.document?.collection === "string",
      "row document.collection must be a string",
    );
    assert(typeof first?.document?.version === "number", "row document.version must be a number");
    assert(Array.isArray(result.staleness), "staleness must be an array");
  },

  "tick-session-open": async (baseUrl) => {
    const reader = await openTicks(baseUrl);
    const opened = await reader.next();
    const established = await reader.next();
    assertValidFrame(opened.frame);
    assertValidFrame(established.frame);
    assert(opened.frame.type === "session.opened", `first frame is ${opened.frame.type}`);
    assert(
      established.frame.type === "subscription.established",
      `second frame is ${established.frame.type}`,
    );
    assert(opened.frame.seq < established.frame.seq, "session frames are not seq-ordered");
    assert(typeof established.frame.payload.checkpoint === "string", "checkpoint missing");
    assert(typeof established.frame.payload.lastSeq === "number", "lastSeq missing");
  },

  "tick-delta-digest-and-seq": async (baseUrl) => {
    const reader = await openTicks(baseUrl);
    await reader.next(); // session.opened
    await reader.next(); // subscription.established
    let previousSeq = 0;
    for (let index = 0; index < 5; index += 1) {
      const message = await reader.next();
      assertValidFrame(message.frame);
      if (message.frame.type === "delta") {
        assert(message.frame.seq > previousSeq, "delta seq is not strictly increasing");
        assert(message.frame.payload.seq === message.frame.seq, "delta payload seq mismatch");
        previousSeq = message.frame.seq;
      }
      const serialized = JSON.stringify(message.frame).toLowerCase();
      for (const banned of ["password", "credential", "secret", "token", "apikey"]) {
        assert(!serialized.includes(banned), `frame carries a banned field: ${banned}`);
      }
    }
  },

  "tick-heartbeat-present": async (baseUrl) => {
    const reader = await openTicks(baseUrl);
    const deadline = Date.now() + 4000;
    while (Date.now() < deadline) {
      const message = await reader.next(1000);
      if (message.frame.type === "heartbeat") {
        assertValidFrame(message.frame);
        return;
      }
    }
    throw new AssertionError("no heartbeat frame within the window");
  },

  "tick-reconnect-resume": async (baseUrl) => {
    const reader = await openTicks(baseUrl);
    await reader.next(); // session.opened
    await reader.next(); // subscription.established
    const seenIds = [];
    for (let index = 0; index < 4; index += 1) {
      const message = await reader.next();
      if (message.frame.type === "delta") {
        seenIds.push(message.id);
      }
    }
    const checkpoint = seenIds[seenIds.length - 1];
    await reader.waitForClose();
    // The session keeps ticking server-side while disconnected; let real
    // missed deltas accumulate so the resume has something to replay.
    await sleep(200);

    const resumed = await openTicks(baseUrl, checkpoint);
    const reopened = await resumed.next();
    const reestablished = await resumed.next();
    assert(reopened.frame.type === "session.opened", `resume begins with ${reopened.frame.type}`);
    assert(
      reestablished.frame.type === "subscription.established",
      `resume establishes with ${reestablished.frame.type}`,
    );
    const checkpointSeq = Number.parseInt(checkpoint.split(":")[1], 10);
    const replayed = await resumed.next();
    assert(replayed.frame.type === "delta", `expected replayed delta, got ${replayed.frame.type}`);
    assert(
      replayed.frame.seq === checkpointSeq + 1,
      `first replayed delta seq ${replayed.frame.seq} does not continue checkpoint ${checkpointSeq}`,
    );
    const replaySeqs = [replayed.frame.seq];
    for (let index = 0; index < 2; index += 1) {
      const message = await resumed.next();
      if (message.frame.type === "delta") {
        replaySeqs.push(message.frame.seq);
      }
    }
    assert(
      new Set(replaySeqs).size === replaySeqs.length,
      "replay produced duplicate seq identities",
    );
    assert(
      replaySeqs.every((seq) => seq > checkpointSeq),
      "replay produced frames at or before the checkpoint",
    );
    // Live deltas after the replayed backlog carry seq past the established
    // high-water mark (SubscriptionEstablishedPayload.lastSeq).
    assert(
      reestablished.frame.payload.lastSeq >= replayed.frame.seq,
      "established lastSeq does not cover the replayed backlog",
    );
  },

  "tick-resync-on-evicted-checkpoint": async (baseUrl) => {
    const reader = await openTicks(baseUrl);
    await reader.next(); // session.opened
    await reader.next(); // subscription.established
    const stale = await reader.next();
    for (let index = 0; index < 4; index += 1) {
      await reader.next();
    }
    const resumed = await openTicks(baseUrl, stale.id);
    const frame = (await resumed.next()).frame;
    assert(frame.type === "resync.required", `expected resync.required, got ${frame.type}`);
    assertValidFrame(frame);
    assert(
      frame.payload.reason === "checkpoint-evicted" ||
        frame.payload.reason === "checkpoint-unknown",
      `unexpected resync reason ${frame.payload.reason}`,
    );
    assert(typeof frame.payload.lastSeq === "number", "resync.lastSeq missing");
  },

  "tick-poison-frame-emitted": async (baseUrl) => {
    const control = await fetch(`${baseUrl}/api/v2/_fixture/control`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ framePoison: true }),
    });
    assert(control.ok, "fixture control call failed");
    const reader = await openTicks(baseUrl);
    await reader.next(); // session.opened
    await reader.next(); // subscription.established
    const deadline = Date.now() + 4000;
    while (Date.now() < deadline) {
      const message = await reader.next(1000);
      if (message.frame.type === "bogus.frame") {
        assertValidFrame(message.frame);
        return;
      }
    }
    throw new AssertionError("fixture did not emit the poison frame");
  },

  "family-kind-unknown-rejected": async (baseUrl) => {
    const { status, body } = await callOperation(baseUrl, "family.read", {
      kind: "not-a-family",
      scope: "studio",
    });
    assert(status === 400, `expected 400, got ${status}`);
    assert(body.error?.code === "family.kind-unknown", `error code is ${body.error?.code}`);
    assert(body.error?.retryable === false, "family.kind-unknown must not be retryable");
  },

  "family-read-ok": async (baseUrl) => {
    const { status, body } = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: "studio",
    });
    assert(status === 200, `expected 200, got ${status}`);
    assert(body.result?.kind === "counters", "FamilyReadResult.kind not echoed");
    assert(body.result?.scope === "studio", "FamilyReadResult.scope not echoed");
    assert(
      typeof body.result?.state === "object" && body.result.state !== null,
      "FamilyReadResult.state missing",
    );
  },

  "session-unauthenticated-redirect-simulated": async (baseUrl) => {
    const gate = await fetch(`${baseUrl}/api/v2/_fixture/control`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ behavior: "session-gate" }),
    });
    assert(gate.ok, "fixture control (session-gate) failed");
    const gated = await callOperation(baseUrl, "engine.status", {});
    assert(gated.status === 401, `expected 401, got ${gated.status}`);
    assert(
      gated.body.error?.code === "session.unauthenticated",
      `error code is ${gated.body.error?.code}`,
    );
    assert(gated.body.error?.retryable === false, "session.unauthenticated must not be retryable");
    const loginUrl = gated.body.error?.loginUrl;
    assert(typeof loginUrl === "string" && loginUrl.length > 0, "error.loginUrl missing");
    const login = await fetch(new URL(loginUrl, baseUrl).toString());
    assert(login.ok, `simulated host login flow answered HTTP ${login.status}`);
    const loginBody = await login.text();
    assert(loginBody.includes('data-fixture="host-login"'), "simulated login flow marker missing");

    const ungate = await fetch(`${baseUrl}/api/v2/_fixture/control`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ behavior: "session-open" }),
    });
    assert(ungate.ok, "fixture control (session-open) failed");
    const reopened = await callOperation(baseUrl, "engine.status", {});
    assert(reopened.status === 200, `expected 200 after ungate, got ${reopened.status}`);
  },
};

// Vectors needing a fixture configuration other than the default.
const fixtureOverrides = {
  "tick-heartbeat-present": { tickIntervalMs: 50, heartbeatIntervalMs: 300, ticksBeforeDrop: 0 },
  "tick-reconnect-resume": { tickIntervalMs: 50, heartbeatIntervalMs: 60_000, ticksBeforeDrop: 4 },
  "tick-resync-on-evicted-checkpoint": {
    tickIntervalMs: 40,
    heartbeatIntervalMs: 60_000,
    ticksBeforeDrop: 0,
    sessionBufferFrames: 2,
  },
  "tick-poison-frame-emitted": {
    tickIntervalMs: 50,
    heartbeatIntervalMs: 60_000,
    ticksBeforeDrop: 0,
  },
  "session-unauthenticated-redirect-simulated": { tickIntervalMs: 250, ticksBeforeDrop: 0 },
};

const defaultFixture = await startFixture({ tickIntervalMs: 250, ticksBeforeDrop: 0 });
const overrideFixtures = new Map();

let failures = 0;
for (const vector of vectors.vectors) {
  const runner = vectorRunners[vector.id];
  if (runner === undefined) {
    console.log(`SKIP  ${vector.id} (no runner)`);
    continue;
  }
  let fixture = defaultFixture;
  const overrides = fixtureOverrides[vector.id];
  if (overrides !== undefined && !overrideFixtures.has(vector.id)) {
    fixture = await startFixture(overrides);
    overrideFixtures.set(vector.id, fixture);
  } else if (overrides !== undefined) {
    fixture = overrideFixtures.get(vector.id);
  }
  try {
    await runner(fixture.baseUrl);
    console.log(`PASS  ${vector.id}`);
  } catch (error) {
    failures += 1;
    console.log(`FAIL  ${vector.id}: ${error instanceof Error ? error.message : String(error)}`);
  }
}

for (const fixture of [defaultFixture, ...overrideFixtures.values()]) {
  fixture.state.dispose();
  fixture.server.closeAllConnections();
  fixture.server.close();
}

console.log(
  `\n[conformance] ${vectors.vectors.length - failures}/${vectors.vectors.length} vectors passed against the fixture`,
);
if (failures > 0) {
  process.exit(1);
}
// Safety: if anything still holds the event loop, do not hang the harness.
setTimeout(() => process.exit(failures > 0 ? 1 : 0), 2_000).unref();
