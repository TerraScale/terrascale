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
