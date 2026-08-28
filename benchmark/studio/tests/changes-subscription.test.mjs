// Unit tests for the DB-04 changes subscription: the generated client's
// fetch-stream SSE consumer against the fixture server's /v1/streams —
// the real C# API host's backbone subscription surface
// (host/TerraBase.Api TypedApi.StreamSseAsync) — covering every selector
// kind, the server-side collections filter, checkpoint-resumed reconnect
// with backlog and no duplicates beyond at-least-once identities, the
// in-band drop marker with boundary rewind, fail-closed unknown events,
// and the local backend-context gate.
//
// Run with: node --test tests/changes-subscription.test.mjs (also wired
// into `vp run unit:test`).

import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";

import { TerrabaseStudioClient } from "../src/api/generated/client.ts";
import { createFixtureServer } from "../scripts/fixture-server.mjs";

const CONTEXT = { principal: "backend-operator", tenant: "tenant-a" };

async function withFixture(run) {
  const { server, state } = createFixtureServer({ host: "127.0.0.1" });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await run(baseUrl, state);
  } finally {
    state.dispose();
    server.closeAllConnections();
    server.close();
  }
}

function subscribe(baseUrl, handlers, options = {}) {
  const client = new TerrabaseStudioClient({
    baseUrl,
    backendContext: CONTEXT,
  });
  return client.subscribeChanges(handlers, options);
}

async function control(baseUrl, body) {
  const response = await fetch(`${baseUrl}/api/v2/_fixture/control`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  assert.equal(response.status, 200);
  return response.json();
}

function waitFor(predicate, timeoutMs, what) {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const tick = () => {
      if (predicate()) {
        resolve();
        return;
      }
      if (Date.now() - started > timeoutMs) {
        reject(new Error(`timed out waiting for ${what}`));
        return;
      }
      setTimeout(tick, 25);
    };
    tick();
  });
}

test("the changes stream opens live and delivers every selector kind the backbone exposes", async () => {
  await withFixture(async (baseUrl) => {
    const seen = [];
    const subscription = subscribe(baseUrl, {
      onEvent: (event, meta) => seen.push({ ...event, replayed: meta.replayed }),
    });
    try {
      await waitFor(
        () => subscription.snapshot().state === "live",
        5_000,
        "the stream to open live",
      );
      await control(baseUrl, {
        streamEmit: [
          { kind: "document", collection: "orders", document: "d-01" },
          { kind: "index", collection: null, document: null },
          { kind: "counter", collection: "orders", document: "d-01" },
          { kind: "time-series", collection: "orders", document: "d-01" },
          { kind: "operation", collection: null, document: null },
          { kind: "database", collection: null, document: null },
          {
            kind: "conflict",
            collection: null,
            document: null,
            conflict: {
              tenant: "tenant-a",
              operations: ["op-1", "op-2"],
              members: ["member-a", "member-b"],
              clocks: ["clock-1", "clock-2"],
              outcome: "winner:member-a",
            },
          },
        ],
      });
      await waitFor(() => seen.length >= 7, 5_000, "all seven emitted events");
      const kinds = seen.map((event) => event.kind);
      assert.deepEqual(kinds, [
        "document",
        "index",
        "counter",
        "time-series",
        "operation",
        "database",
        "conflict",
      ]);
      const conflict = seen.find((event) => event.kind === "conflict");
      assert.equal(conflict.collection, null);
      assert.equal(conflict.document, null);
      assert.equal(conflict.conflict.tenant, "tenant-a");
      // Content-free: no event carries a document body anywhere.
      for (const event of seen) {
        assert.equal(JSON.stringify(event).includes('"body"'), false);
      }
      const snapshot = subscription.snapshot();
      assert.equal(snapshot.eventCount, 7);
      assert.equal(snapshot.duplicateCount, 0);
      assert.ok(snapshot.lastSeq !== null && snapshot.lastSeq >= 7);
    } finally {
      subscription.close();
    }
  });
});

test("the collections filter scopes the stream server-side", async () => {
  await withFixture(async (baseUrl) => {
    const seen = [];
    const subscription = subscribe(
      baseUrl,
      { onEvent: (event) => seen.push(event) },
      { collections: ["orders"] },
    );
    try {
      await waitFor(() => subscription.snapshot().state === "live", 5_000, "the stream to open");
      await control(baseUrl, {
        streamEmit: [
          { kind: "document", collection: "orders", document: "d-01" },
          { kind: "document", collection: "users", document: "u-01" },
          { kind: "counter", collection: "users", document: "u-01" },
        ],
      });
      await waitFor(() => seen.length >= 1, 5_000, "the filtered event");
      await new Promise((resolve) => setTimeout(resolve, 250));
      assert.deepEqual(
        seen.map((event) => event.collection),
        ["orders"],
      );
    } finally {
      subscription.close();
    }
  });
});

test("a dropped connection reconnects from the checkpoint and shows the backlog without duplicates", async () => {
  await withFixture(async (baseUrl) => {
    const seen = [];
    let replayed = 0;
    const subscription = subscribe(baseUrl, {
      onEvent: (event, meta) => {
        seen.push(event.sequence);
        if (meta.replayed) {
          replayed += 1;
        }
      },
    });
    try {
      await waitFor(() => subscription.snapshot().state === "live", 5_000, "the stream to open");
      await control(baseUrl, {
        streamEmit: [{ kind: "document", collection: "orders", document: "d-01" }],
      });
      await waitFor(() => seen.length >= 1, 5_000, "the first event");
      const firstSequence = seen[0];

      // The host drops every open changes connection; the client must
      // reconnect on its own from the checkpoint (the last accepted id).
      await control(baseUrl, { streamDropConnections: true });
      await control(baseUrl, {
        streamEmit: [
          { kind: "document", collection: "orders", document: "d-02" },
          { kind: "document", collection: "orders", document: "d-03" },
        ],
      });
      await waitFor(() => seen.length >= 3, 8_000, "the reconnected backlog");
      await waitFor(
        () => subscription.snapshot().state === "live",
        8_000,
        "the reconnected stream to return live",
      );
      assert.equal(seen.length, 3);
      assert.deepEqual(seen, [firstSequence, firstSequence + 1, firstSequence + 2]);
      assert.equal(replayed, 2);
      assert.equal(subscription.snapshot().duplicateCount, 0);
      assert.ok(subscription.snapshot().reconnectCount >= 1);
    } finally {
      subscription.close();
    }
  });
});

test("re-delivered at-least-once identities are counted as duplicates, never re-surfaced", async () => {
  await withFixture(async (baseUrl) => {
    const seen = [];
    const subscription = subscribe(baseUrl, { onEvent: (event) => seen.push(event.sequence) });
    try {
      await waitFor(() => subscription.snapshot().state === "live", 5_000, "the stream to open");
      await control(baseUrl, {
        streamEmit: [{ kind: "document", collection: "orders", document: "d-01" }],
      });
      await waitFor(() => seen.length >= 1, 5_000, "the first event");
      // The fixture re-delivers the served range with the original
      // identities (an at-least-once redelivery): the client counts a
      // duplicate and does not surface it again.
      await control(baseUrl, { streamRedeliver: true });
      await waitFor(
        () => subscription.snapshot().duplicateCount >= 1,
        5_000,
        "the duplicate to be counted",
      );
      assert.equal(seen.length, 1);
    } finally {
      subscription.close();
    }
  });
});

test("a host drop marker surfaces in-band and rewinds the checkpoint to the boundary", async () => {
  await withFixture(async (baseUrl) => {
    const seen = [];
    const drops = [];
    let replayed = 0;
    const subscription = subscribe(baseUrl, {
      onEvent: (event, meta) => {
        seen.push(event.sequence);
        if (meta.replayed) {
          replayed += 1;
        }
      },
      onDrop: (drop) => drops.push({ resumeFrom: drop.resumeFrom, dropped: drop.dropped }),
    });
    try {
      await waitFor(() => subscription.snapshot().state === "live", 5_000, "the stream to open");
      await control(baseUrl, {
        streamEmit: [{ kind: "document", collection: "orders", document: "d-01" }],
      });
      await waitFor(() => seen.length >= 1, 5_000, "the first event");
      const before = seen[0];

      // The slow-consumer shed, deterministically: the fixture holds
      // delivery (the subscriber stops draining), a change queues behind
      // the hold, and the shed counts it and surfaces the drop in-band
      // with the safe rewind boundary — the host's bounded-queue policy.
      await control(baseUrl, { streamHold: true });
      await control(baseUrl, {
        streamEmit: [{ kind: "document", collection: "orders", document: "d-02" }],
      });
      await control(baseUrl, { streamShed: true });
      await waitFor(() => drops.length >= 1, 5_000, "the in-band drop marker");
      assert.equal(drops[0].dropped >= 1, true);
      assert.equal(drops[0].resumeFrom > 0, true);
      assert.equal(drops[0].resumeFrom, before + 1);
      assert.equal(subscription.snapshot().dropCount, 1);

      // Reconnect from the rewound checkpoint re-delivers the missed
      // range: the shed sequences return as the replayed backlog.
      await control(baseUrl, { streamDropConnections: true });
      await waitFor(() => replayed >= 1, 8_000, "the missed range to be re-delivered");
      assert.ok(seen.length > 1);
      assert.ok(Math.min(...seen) <= before + 1);
    } finally {
      subscription.close();
    }
  });
});

test("an unknown SSE event name fails closed, is counted, and the stream stays live", async () => {
  await withFixture(async (baseUrl) => {
    const subscription = subscribe(baseUrl, { onEvent: () => {} });
    try {
      await waitFor(() => subscription.snapshot().state === "live", 5_000, "the stream to open");
      await control(baseUrl, { streamPoisonEvent: true });
      await waitFor(
        () => subscription.snapshot().rejectedCount >= 1,
        5_000,
        "the unknown event to be rejected",
      );
      assert.match(subscription.snapshot().lastError ?? "", /unknown event/);
      await control(baseUrl, {
        streamEmit: [{ kind: "document", collection: "orders", document: "d-01" }],
      });
      await waitFor(
        () => subscription.snapshot().eventCount >= 1,
        5_000,
        "delivery to continue after the rejection",
      );
      assert.equal(subscription.snapshot().state, "live");
    } finally {
      subscription.close();
    }
  });
});

test("the fixture mirrors the real host's backend-context gate on the stream route", async () => {
  await withFixture(async (baseUrl) => {
    const response = await fetch(`${baseUrl}/v1/streams`);
    assert.equal(response.status, 401);
    const body = await response.json();
    // The real host's ErrorDto shape (System.Text.Json Web defaults).
    assert.equal(body.code, "unauthorized");
    assert.match(body.detail, /X-TB-Principal/);
  });
});

// ---------------------------------------------------------------------------
// Hostile-SSE regression rows (repair loop 1): the real host's bounded queue
// sheds OLDEST-FIRST with per-commit batch-then-trigger ordering
// (host/TerraBase.Api/BackboneHub.cs Offer/Shed;
// crates/terrabase-tbep/src/host.rs), so a surviving newer batch or trigger
// with a shed older batch is the normal multi-batch shed shape — shapes the
// fixture's never-partially-draining consumers cannot produce. Each row
// drives the REAL generated client against a scripted SSE server that
// reproduces one shed shape exactly.

function changeEvent(sequence) {
  return {
    sequence,
    operationId: `op-${sequence}`,
    kind: "put",
    collection: "orders",
    document: `d-${sequence}`,
  };
}

function batchPayload(events, nextSequence) {
  return {
    subscriptionId: 1,
    fromSequence: events.length === 0 ? 0 : events[0].sequence,
    events,
    nextSequence:
      nextSequence ?? (events.length === 0 ? 1 : events[events.length - 1].sequence + 1),
  };
}

function sseFrame(name, payload, eol = "\n") {
  return `event: ${name}${eol}data: ${JSON.stringify(payload)}${eol}${eol}`;
}

// A scripted /v1/streams server: no gate, whatever frames the scenario
// writes. Records each connection's `from` resume parameter so the tests
// can assert the exact reconnect boundary.
function startHostileStream(handler) {
  const connections = [];
  const server = createServer((req, res) => {
    const url = new URL(req.url, "http://127.0.0.1");
    connections.push(url.searchParams.get("from"));
    handler(connections.length, url, req, res);
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      resolve({
        connections,
        baseUrl: `http://127.0.0.1:${server.address().port}`,
        close: () => {
          server.closeAllConnections();
          server.close();
        },
      });
    });
  });
}

test("a checkpoint-ahead-of-gap shed re-delivers the missed range: the drop marker rewinds the dedup ledger, not just the cursor", async () => {
  // BackboneHub.Shed drops the oldest frames: batch(1,2) and batch(5,6)
  // were delivered while batch(3,4) was shed; the marker names boundary 3.
  // The watermark already stands at 6 — without a re-anchored identity
  // ledger the re-delivered gap 3,4 would be swallowed as "duplicates".
  const stream = await startHostileStream((connection, url, req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    if (connection === 1) {
      res.write(sseFrame("opened", { subscriptionId: 1, startSequence: 1 }));
      res.write(sseFrame("batch", batchPayload([changeEvent(1), changeEvent(2)])));
      res.write(sseFrame("batch", batchPayload([changeEvent(5), changeEvent(6)])));
      res.write(sseFrame("drop", { subscriptionId: 1, resumeFrom: 3, dropped: 2 }));
      setTimeout(() => res.end(), 100);
      return;
    }
    const from = Number(url.searchParams.get("from") ?? 1);
    const events = [];
    for (let sequence = from; sequence <= 6; sequence += 1) {
      events.push(changeEvent(sequence));
    }
    res.write(sseFrame("opened", { subscriptionId: 2, startSequence: from }));
    res.write(sseFrame("batch", batchPayload(events)));
    setTimeout(() => res.end(), 150);
  });
  const surfaced = [];
  let replayed = 0;
  const subscription = subscribe(
    stream.baseUrl,
    {
      onEvent: (event, meta) => {
        surfaced.push(event.sequence);
        if (meta.replayed) {
          replayed += 1;
        }
      },
    },
    { reconnectDelayMs: 100 },
  );
  try {
    await waitFor(() => surfaced.length >= 4, 5_000, "the pre-drop batches");
    await waitFor(() => stream.connections.length >= 2, 8_000, "the resumed connection");
    // The resume boundary is the marker's, even though the checkpoint ran
    // ahead of the gap before the shed.
    assert.equal(stream.connections[1], "3");
    await waitFor(() => surfaced.length >= 6, 8_000, "the missed range 3,4 to surface");
    // 1,2,5,6 surfaced when they were delivered; 3,4 surface only on the
    // resumed connection (as the replayed backlog) — the watermark must
    // not swallow them even though it had already reached 6.
    assert.deepEqual(surfaced, [1, 2, 5, 6, 3, 4]);
    assert.equal(replayed, 2);
    const snapshot = subscription.snapshot();
    assert.equal(snapshot.eventCount, 6);
    // 5,6 were genuinely accepted before the marker: their re-delivery is
    // exactly the at-least-once duplicate count.
    assert.equal(snapshot.duplicateCount, 2);
    assert.ok(snapshot.checkpoint !== null && snapshot.checkpoint >= 6);
  } finally {
    subscription.close();
    stream.close();
  }
});

test("a surviving trigger never advances the dedup watermark past the last accepted batch event", async () => {
  // batch(3,4) was shed; its trigger (sequence 4) survived the bounded
  // queue. The trigger is a push hint, not an accepted identity — the
  // re-delivered gap must still surface.
  const stream = await startHostileStream((connection, url, req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    if (connection === 1) {
      res.write(sseFrame("opened", { subscriptionId: 1, startSequence: 1 }));
      res.write(sseFrame("batch", batchPayload([changeEvent(1), changeEvent(2)])));
      res.write(
        sseFrame("trigger", {
          subscriptionId: 1,
          collection: "orders",
          sequence: 4,
          operationId: "op-4",
        }),
      );
      res.write(sseFrame("drop", { subscriptionId: 1, resumeFrom: 3, dropped: 2 }));
      setTimeout(() => res.end(), 100);
      return;
    }
    const from = Number(url.searchParams.get("from") ?? 1);
    res.write(sseFrame("opened", { subscriptionId: 2, startSequence: from }));
    res.write(sseFrame("batch", batchPayload([changeEvent(3), changeEvent(4)])));
    res.write(sseFrame("batch", batchPayload([changeEvent(5)])));
    setTimeout(() => res.end(), 150);
  });
  const surfaced = [];
  const subscription = subscribe(
    stream.baseUrl,
    { onEvent: (event) => surfaced.push(event.sequence) },
    { reconnectDelayMs: 100 },
  );
  try {
    await waitFor(() => surfaced.length >= 5, 8_000, "the gap 3,4 and the live 5 to surface");
    assert.deepEqual(surfaced, [1, 2, 3, 4, 5]);
    const snapshot = subscription.snapshot();
    assert.equal(snapshot.duplicateCount, 0);
    assert.equal(snapshot.lastSeq, 5);
  } finally {
    subscription.close();
    stream.close();
  }
});

test("a post-marker batch cursor never erases the rewind: the checkpoint stays at the boundary until the gap is covered", async () => {
  // The host's marker discipline (BackboneHub Dropped): a checkpoint at or
  // past the boundary cannot be trusted for the gap. Live traffic (7,8)
  // continues past the marker on the same connection — its cursor must not
  // move the rewound checkpoint, or the next reconnect would skip 3..6.
  const stream = await startHostileStream((connection, url, req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    if (connection === 1) {
      res.write(sseFrame("opened", { subscriptionId: 1, startSequence: 1 }));
      res.write(sseFrame("batch", batchPayload([changeEvent(1), changeEvent(2)])));
      res.write(sseFrame("drop", { subscriptionId: 1, resumeFrom: 3, dropped: 4 }));
      res.write(sseFrame("batch", batchPayload([changeEvent(7), changeEvent(8)])));
      setTimeout(() => res.end(), 100);
      return;
    }
    const from = Number(url.searchParams.get("from") ?? 1);
    const events = [];
    for (let sequence = from; sequence <= 8; sequence += 1) {
      events.push(changeEvent(sequence));
    }
    res.write(sseFrame("opened", { subscriptionId: 2, startSequence: from }));
    res.write(sseFrame("batch", batchPayload(events)));
    setTimeout(() => res.end(), 150);
  });
  const surfaced = [];
  const subscription = subscribe(
    stream.baseUrl,
    { onEvent: (event) => surfaced.push(event.sequence) },
    { reconnectDelayMs: 100 },
  );
  try {
    await waitFor(() => surfaced.length >= 4, 5_000, "the live frames past the marker");
    // The rewind is sticky against the post-marker cursor (8).
    assert.equal(subscription.snapshot().checkpoint, 2);
    await waitFor(() => stream.connections.length >= 2, 8_000, "the resumed connection");
    assert.equal(stream.connections[1], "3");
    await waitFor(() => surfaced.length >= 8, 8_000, "the whole shed range to re-deliver");
    assert.deepEqual(surfaced, [1, 2, 7, 8, 3, 4, 5, 6]);
    const snapshot = subscription.snapshot();
    assert.equal(snapshot.eventCount, 8);
    assert.equal(snapshot.duplicateCount, 2);
  } finally {
    subscription.close();
    stream.close();
  }
});

test("spec-legal CRLF SSE frames parse instead of stalling silently", async () => {
  const stream = await startHostileStream((connection, url, req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write(sseFrame("opened", { subscriptionId: 1, startSequence: 1 }, "\r\n"));
    res.write(sseFrame("batch", batchPayload([changeEvent(1), changeEvent(2)]), "\r\n"));
    // A frame torn across writes so the CR lands at a chunk boundary.
    setTimeout(() => {
      res.write("event: trigger\r");
      setTimeout(() => {
        res.write(
          `\ndata: ${JSON.stringify({
            subscriptionId: 1,
            collection: "orders",
            sequence: 2,
            operationId: "op-2",
          })}\r\n\r\n`,
        );
        res.end();
      }, 120);
    }, 120);
  });
  const surfaced = [];
  let triggers = 0;
  const subscription = subscribe(
    stream.baseUrl,
    {
      onEvent: (event) => surfaced.push(event.sequence),
      onTrigger: () => {
        triggers += 1;
      },
    },
    { reconnectDelayMs: 100 },
  );
  try {
    await waitFor(() => surfaced.length >= 2, 5_000, "the CRLF batch to surface");
    await waitFor(() => subscription.snapshot().state === "live", 5_000, "the CRLF opened frame");
    await waitFor(() => triggers >= 1, 5_000, "the chunk-torn trigger frame");
    const snapshot = subscription.snapshot();
    assert.equal(snapshot.rejectedCount, 0);
    assert.deepEqual(surfaced, [1, 2]);
    // The scripted server ends its response, so a reconnect notice in
    // lastError is the healthy path — what must NOT happen is a silent
    // stall (no events, no state change, no error).
  } finally {
    subscription.close();
    stream.close();
  }
});

test("a non-finite batch cursor (1e999) never poisons the checkpoint", async () => {
  const stream = await startHostileStream((connection, url, req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write(sseFrame("opened", { subscriptionId: 1, startSequence: 1 }));
    // Hand-written frame: JSON.stringify would turn Infinity into null;
    // 1e999 parses back as the number Infinity.
    res.write(
      `event: batch\ndata: {"subscriptionId":1,"fromSequence":1,"events":[${JSON.stringify(
        changeEvent(1),
      )}],"nextSequence":1e999}\n\n`,
    );
    setTimeout(() => res.end(), 100);
  });
  const surfaced = [];
  const subscription = subscribe(
    stream.baseUrl,
    { onEvent: (event) => surfaced.push(event.sequence) },
    { reconnectDelayMs: 100 },
  );
  try {
    await waitFor(() => surfaced.length >= 1, 5_000, "the event itself to surface");
    const checkpoint = subscription.snapshot().checkpoint;
    assert.ok(checkpoint === null || Number.isFinite(checkpoint), `checkpoint ${checkpoint}`);
    await waitFor(() => stream.connections.length >= 2, 8_000, "the reconnect");
    const from = stream.connections[1];
    assert.ok(
      from === null || (Number.isFinite(Number(from)) && Number(from) > 0),
      `reconnect from=${from}`,
    );
  } finally {
    subscription.close();
    stream.close();
  }
});

test("a hung connect is aborted and retried: the subscription never sits silent", async () => {
  const stream = await startHostileStream(() => {
    // Accept the request and never respond: the fetch hangs forever.
  });
  const subscription = subscribe(
    stream.baseUrl,
    { onEvent: () => {} },
    {
      connectTimeoutMs: 150,
      reconnectDelayMs: 100,
    },
  );
  try {
    await waitFor(
      () => subscription.snapshot().reconnectCount >= 1,
      5_000,
      "the timed-out connect to surface",
    );
    const snapshot = subscription.snapshot();
    assert.match(snapshot.lastError ?? "", /timed out/);
    assert.equal(snapshot.state, "reconnecting");
    await waitFor(() => stream.connections.length >= 2, 5_000, "the retried connect to arrive");
  } finally {
    subscription.close();
    stream.close();
  }
});

test("the client's own per-event batch validation fails closed on unknown kinds and non-finite sequences", async () => {
  // Defense-in-depth behind the model's fail-closed checks: the CLIENT
  // must reject invalid batch events itself (a hostile or broken host),
  // never surface them, and keep delivering the valid neighbors.
  const stream = await startHostileStream((connection, url, req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write(sseFrame("opened", { subscriptionId: 1, startSequence: 1 }));
    res.write(
      `event: batch\ndata: {"subscriptionId":1,"fromSequence":1,"events":[` +
        `{"sequence":1e999,"operationId":"op-inf","kind":"put","collection":"orders","document":"d-inf"},` +
        `{"sequence":2,"operationId":"op-kind","kind":"quantum-flux","collection":"orders","document":"d-kind"},` +
        `${JSON.stringify(changeEvent(3))}],"nextSequence":4}\n\n`,
    );
    setTimeout(() => res.end(), 150);
  });
  const surfaced = [];
  const subscription = subscribe(
    stream.baseUrl,
    { onEvent: (event) => surfaced.push(event.sequence) },
    { reconnectDelayMs: 100 },
  );
  try {
    await waitFor(() => surfaced.length >= 1, 5_000, "the valid neighbor to surface");
    assert.deepEqual(surfaced, [3]);
    const snapshot = subscription.snapshot();
    assert.equal(snapshot.eventCount, 1);
    assert.equal(snapshot.rejectedCount, 2);
    assert.match(snapshot.lastError ?? "", /invalid event/);
  } finally {
    subscription.close();
    stream.close();
  }
});

test("a paused (slow) consumer resumes and receives what followed the pause", async () => {
  await withFixture(async (baseUrl) => {
    const seen = [];
    const subscription = subscribe(baseUrl, { onEvent: (event) => seen.push(event.sequence) });
    try {
      await waitFor(() => subscription.snapshot().state === "live", 5_000, "the stream to open");
      subscription.pause();
      await control(baseUrl, {
        streamEmit: [{ kind: "document", collection: "orders", document: "d-01" }],
      });
      await new Promise((resolve) => setTimeout(resolve, 250));
      assert.equal(seen.length, 0);
      subscription.resume();
      await waitFor(() => seen.length >= 1, 5_000, "the paused event to arrive after resume");
      assert.equal(subscription.snapshot().state, "live");
    } finally {
      subscription.close();
    }
  });
});
