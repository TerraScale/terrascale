import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  syncV2CanonicalCheckpoint,
  syncV2CheckpointFromBytes,
  syncV2CheckpointToBytes,
  TerraScaleSyncV2Session,
  type SyncV2Fetch,
  type SyncV2Value,
} from "./index.js";

// ---------------------------------------------------------------------------
// A fixture API host implementing the SE-08 sync-session HTTP/SSE surface
// (TypedApi.cs) so the session tests exercise the real endpoint contract:
// POST /v1/sync/sessions{,/delta,/snapshot,/ack,/mutations,/close} with
// Web-camelCase DTOs, base64 checkpoints, the Idempotency-Key header, and
// the /v1/streams SSE channel with Last-Event-ID resume.
// ---------------------------------------------------------------------------

type HostRequest = {
  readonly method: string;
  readonly path: string;
  readonly body?: unknown | undefined;
  readonly idempotencyKey?: string | undefined;
  readonly lastEventId?: string | undefined;
};

type DeltaScript =
  | { readonly kind: "serve" }
  | { readonly kind: "refuse"; readonly detail: string };

class FixtureApiHost {
  public readonly requests: HostRequest[] = [];
  public appliedKeys = new Map<string, { sequence: number; version: number }>();
  public acknowledged: string[] = [];
  public journalSize = 0;

  readonly #collections = new Map<string, { sequence: number; end: number }>();
  readonly #sessions = new Map<number, string>();
  readonly #streams: Array<(frame: string) => void> = [];
  #deltaScript: DeltaScript = { kind: "serve" };
  #refusalsRemaining = 0;
  #nextSessionId = 1;
  #lastStreamId = 0;

  public scriptDeltaRefusal(detail: string): void {
    this.#deltaScript = { kind: "refuse", detail };
    this.#refusalsRemaining = 1;
  }

  public scriptDeltaServe(): void {
    this.#deltaScript = { kind: "serve" };
    this.#refusalsRemaining = 0;
  }

  public commit(collection: string): { sequence: number } {
    const scope = this.#scope(collection);
    scope.sequence += 1;
    scope.end += 1;
    this.journalSize += 1;
    return { sequence: scope.sequence };
  }

  public pushTrigger(collection: string, sequence: number): void {
    this.#lastStreamId = sequence;
    const frame =
      `id: ${sequence}\n` +
      `event: trigger\n` +
      `data: ${JSON.stringify({ subscriptionId: 1, collection, sequence, operationId: `op-${sequence}` })}\n\n`;
    for (const write of this.#streams) {
      write(frame);
    }
  }

  public closeStreams(): void {
    this.#streams.splice(0);
    for (const close of this.#streamClosers.splice(0)) {
      close();
    }
  }

  readonly #streamClosers: Array<() => void> = [];

  public readonly fetch: SyncV2Fetch = async (input, init) => {
    const url = new URL(input.toString());
    const method = (init?.method ?? "GET").toUpperCase();
    const headers = new Headers(init?.headers ?? []);
    const rawBody = typeof init?.body === "string" ? (JSON.parse(init.body) as unknown) : undefined;
    this.requests.push({
      method,
      path: url.pathname,
      body: rawBody,
      idempotencyKey: headers.get("idempotency-key") ?? undefined,
      lastEventId: headers.get("last-event-id") ?? undefined,
    });

    if (method === "POST" && url.pathname === "/v1/sync/sessions") {
      const request = rawBody as { collection: string; resume?: string };
      const sessionId = this.#nextSessionId++;
      this.#sessions.set(sessionId, request.collection);
      const scope = this.#scope(request.collection);
      return Response.json({
        sessionId,
        checkpoint: base64(syncV2CheckpointToBytes(hostCheckpoint(scope))),
        retainedFloor: base64(syncV2CheckpointToBytes(hostCheckpoint(scope))),
      });
    }

    if (method === "POST" && url.pathname.endsWith("/delta")) {
      const sessionId = Number(url.pathname.split("/").at(-2));
      const request = rawBody as { from: string };
      const scope = this.#scope(this.#sessions.get(sessionId) ?? "orders");
      const from = syncV2CheckpointFromBytes(unbase64(request.from));
      if (this.#deltaScript.kind === "refuse" && this.#refusalsRemaining > 0) {
        this.#refusalsRemaining -= 1;
        return Response.json(
          { code: "checkpoint-refused", detail: this.#deltaScript.detail, resyncRequired: true },
          { status: 409 },
        );
      }
      if (from.storage_sequence >= BigInt(scope.sequence)) {
        return Response.json({
          sessionId,
          payloadCodec: "loro-delta/1",
          payload: "",
          next: base64(syncV2CheckpointToBytes(hostCheckpoint(scope))),
          commits: 0,
          singleCommitOversize: false,
        });
      }
      const payload = new TextEncoder().encode(
        `delta:${from.storage_sequence}->${scope.sequence}`,
      );
      return Response.json({
        sessionId,
        payloadCodec: "loro-delta/1",
        payload: base64(payload),
        next: base64(syncV2CheckpointToBytes(hostCheckpoint(scope))),
        commits: scope.sequence - Number(from.storage_sequence),
        singleCommitOversize: false,
      });
    }

    if (method === "POST" && url.pathname.endsWith("/snapshot")) {
      const sessionId = Number(url.pathname.split("/").at(-2));
      const scope = this.#scope(this.#sessions.get(sessionId) ?? "orders");
      return Response.json({
        sessionId,
        payloadCodec: "loro-snapshot/1",
        payload: base64(new TextEncoder().encode(`snapshot:@${scope.sequence}`)),
        checkpoint: base64(syncV2CheckpointToBytes(hostCheckpoint(scope))),
      });
    }

    if (method === "POST" && url.pathname.endsWith("/ack")) {
      const sessionId = Number(url.pathname.split("/").at(-2));
      const request = rawBody as { checkpoint: string };
      syncV2CheckpointFromBytes(unbase64(request.checkpoint));
      this.acknowledged.push(request.checkpoint);
      return Response.json({ sessionId });
    }

    if (method === "POST" && url.pathname.endsWith("/mutations")) {
      const sessionId = Number(url.pathname.split("/").at(-2));
      const request = rawBody as {
        document: string;
        change: { put?: Array<{ name: string; value: unknown }>; delete?: boolean };
      };
      const key = headers.get("idempotency-key") ?? "";
      const replayed = this.appliedKeys.get(key);
      if (replayed) {
        return Response.json({
          sessionId,
          outcome: {
            outcome: "applied",
            receipt: { ...replayed, operationId: key },
          },
        });
      }
      const scalarDiscriminators = ["null", "boolean", "integer", "number", "string"];
      const hasComposite = (request.change.put ?? []).some((field) => {
        const value = field.value as Record<string, unknown> | null;
        return value === null
          || typeof value !== "object"
          || !scalarDiscriminators.some((key) => key in value);
      });
      if (hasComposite || key === "") {
        return Response.json({
          sessionId,
          outcome: { outcome: "rejected", reason: "invalid_body" },
        });
      }
      const scope = this.#scope(this.#sessions.get(sessionId) ?? "orders");
      const receipt = { sequence: scope.sequence + 1, version: scope.sequence + 1 };
      this.appliedKeys.set(key, receipt);
      this.commit(this.#sessions.get(sessionId) ?? "orders");
      return Response.json({
        sessionId,
        outcome: { outcome: "applied", receipt: { ...receipt, operationId: key } },
      });
    }

    if (method === "POST" && url.pathname.endsWith("/close")) {
      const sessionId = Number(url.pathname.split("/").at(-2));
      return Response.json({ sessionId });
    }

    if (method === "GET" && url.pathname === "/v1/streams") {
      const host = this;
      const encoder = new TextEncoder();
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          const write = (frame: string) => controller.enqueue(encoder.encode(frame));
          host.#streams.push(write);
          host.#streamClosers.push(() => {
            try {
              controller.close();
            } catch {
              // already closed
            }
          });
          write(
            `id: ${host.#lastStreamId}\nevent: opened\n` +
              `data: ${JSON.stringify({ subscriptionId: 1, startSequence: host.#lastStreamId, collections: [url.searchParams.get("collections")] })}\n\n`,
          );
        },
      });
      return new Response(stream, {
        status: 200,
        headers: { "content-type": "text/event-stream", "cache-control": "no-cache" },
      });
    }

    return Response.json({ code: "not-found", detail: url.pathname }, { status: 404 });
  };

  #scope(collection: string): { sequence: number; end: number } {
    let scope = this.#collections.get(collection);
    if (scope === undefined) {
      scope = { sequence: 0, end: 0 };
      this.#collections.set(collection, scope);
    }
    return scope;
  }
}

function hostCheckpoint(scope: { sequence: number; end: number }) {
  return syncV2CanonicalCheckpoint(scope.sequence === 0 ? [] : [[1n, scope.end]], BigInt(scope.sequence));
}

function base64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

function unbase64(value: string): Uint8Array {
  return new Uint8Array(Buffer.from(value, "base64"));
}

async function waitFor(condition: () => boolean, timeoutMs = 2_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error("condition not reached within the timeout");
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

function createSession(host: FixtureApiHost, options?: { resume?: string }): TerraScaleSyncV2Session {
  return new TerraScaleSyncV2Session({
    Url: "https://api.terrascale.test",
    Collection: "orders",
    Principal: "principal-1",
    Tenant: "tenant-a",
    Fetch: host.fetch,
    ResumeCheckpoint: options?.resume,
  });
}

describe("TerraScaleSyncV2Session over the API host HTTP/SSE surface", () => {
  test("a fresh session adopts a snapshot, then triggers drive delta pulls without polling", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    host.commit("orders");
    const session = createSession(host);

    await session.connect();

    const setup = host.requests.find((request) => request.path === "/v1/sync/sessions");
    assert.deepEqual((setup?.body as { collection?: string } | undefined)?.collection, "orders");
    assert.deepEqual((setup?.body as { resume?: string } | undefined)?.resume ?? null, null);
    assert.equal(
      host.requests.some((request) => request.path.endsWith("/snapshot")),
      true,
      "a fresh session adopts the full snapshot",
    );
    assert.equal(session.getStatus(), "live");
    assert.equal(
      (session.getCheckpoint().storage_sequence as bigint).toString(10),
      "2",
      "the snapshot checkpoint is adopted",
    );
    assert.ok(host.acknowledged.length >= 1, "the adopted checkpoint is acknowledged");

    const deltasBefore = host.requests.filter((request) => request.path.endsWith("/delta")).length;
    await new Promise((resolve) => setTimeout(resolve, 40));
    assert.equal(
      host.requests.filter((request) => request.path.endsWith("/delta")).length,
      deltasBefore,
      "no timer-driven polling: deltas are pulled only on the trigger hint",
    );

    host.commit("orders");
    host.pushTrigger("orders", 3);
    await waitFor(() => session.getCheckpoint().storage_sequence === 3n);
    assert.equal(session.getStatus(), "live");

    // One triggered catch-up: the delta batch plus the terminal currency
    // check (the empty answer that ends the drain) — never a timer poll.
    const deltaRequests = host.requests.filter((request) => request.path.endsWith("/delta"));
    assert.equal(deltaRequests.length, deltasBefore + 2);
    const batchPull = deltaRequests.at(-2)?.body as { from: string };
    assert.equal(
      syncV2CheckpointFromBytes(unbase64(batchPull.from)).storage_sequence,
      2n,
      "the pull serves from the durable checkpoint",
    );
    const terminalPull = deltaRequests.at(-1)?.body as { from: string };
    assert.equal(
      syncV2CheckpointFromBytes(unbase64(terminalPull.from)).storage_sequence,
      3n,
      "the terminal currency check serves from the advanced checkpoint",
    );
    assert.equal(
      host.acknowledged.at(-1) !== undefined
        && syncV2CheckpointFromBytes(unbase64(host.acknowledged.at(-1)!)).storage_sequence === 3n,
      true,
      "the advanced checkpoint is acknowledged",
    );

    await session.disconnect();
  });

  test("a resuming session presents its durable checkpoint and re-served batches apply idempotently", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const first = createSession(host);
    await first.connect();
    const resume = first.getCheckpointBase64();
    await first.disconnect();

    host.commit("orders");
    host.commit("orders");

    const second = createSession(host, { resume });
    await second.connect();

    const setup = host.requests
      .filter((request) => request.method === "POST" && request.path === "/v1/sync/sessions")
      .at(-1);
    assert.deepEqual(
      (setup?.body as { resume?: string } | undefined)?.resume,
      resume,
      "session setup carries the durable resume checkpoint",
    );
    assert.equal(second.getCheckpoint().storage_sequence, 3n);

    // At-least-once: the identical batch re-served after a lost ack is a
    // no-op at the consumer (the reconnect-replay vector contract).
    const appliedBefore = second.getStoreSnapshot().AppliedDeltaBatches;
    await second.pullNow();
    assert.equal(
      second.getStoreSnapshot().AppliedDeltaBatches,
      appliedBefore,
      "a re-served identical batch from the same checkpoint applies nothing new",
    );
    assert.equal(second.getCheckpoint().storage_sequence, 3n);
    await second.disconnect();
  });

  test("a checkpoint behind the retained floor triggers snapshot resync and recovery to live", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const session = createSession(host);
    await session.connect();

    host.commit("orders");
    host.scriptDeltaRefusal("behind-retained-floor");
    host.pushTrigger("orders", 2);

    await waitFor(() => session.getCheckpoint().storage_sequence === 2n);
    assert.equal(session.getStatus(), "live");
    assert.equal(
      host.requests.some((request) => request.method === "POST" && request.path.endsWith("/snapshot")),
      true,
      "resync adopts a fresh snapshot",
    );
    host.scriptDeltaServe();
    await session.disconnect();
  });

  test("mutations are server-ack-only: applied receipts, typed rejections, and idempotent replay", async () => {
    const host = new FixtureApiHost();
    const session = createSession(host);
    await session.connect();
    const before = host.journalSize;

    const put: Record<string, SyncV2Value> = { status: "approved", count: 3 };
    const applied = await session.executeMutation({ Document: "d-01", Put: put }, "idem-1");
    assert.equal(applied.Outcome, "applied");
    if (applied.Outcome === "applied") {
      assert.equal(applied.Receipt.OperationId, "idem-1");
      assert.equal(typeof applied.Receipt.Sequence, "number");
    }
    const mutationRequest = host.requests.find((request) => request.path.endsWith("/mutations"));
    assert.equal(mutationRequest?.idempotencyKey, "idem-1");
    assert.deepEqual((mutationRequest?.body as { document: string }).document, "d-01");
    const change = (mutationRequest?.body as { change: { put: Array<{ name: string; value: unknown }> } }).change;
    assert.equal(change.put.length, 2);
    assert.deepEqual(
      change.put.map((field) => [field.name, field.value]).sort(([a], [b]) => String(a).localeCompare(String(b))),
      [["count", { integer: 3 }], ["status", { string: "approved" }]],
    );
    assert.equal(host.journalSize, before + 1, "the server applied the mutation exactly once");

    // A retry with the same key replays the recorded receipt.
    const replay = await session.executeMutation({ Document: "d-01", Put: put }, "idem-1");
    assert.equal(replay.Outcome, "applied");
    if (replay.Outcome === "applied" && applied.Outcome === "applied") {
      assert.equal(replay.Receipt.Sequence, applied.Receipt.Sequence);
    }
    assert.equal(host.journalSize, before + 1, "the retried key does not double-apply");

    const deleted = await session.executeMutation({ Document: "d-02", Delete: true }, "idem-2");
    assert.equal(deleted.Outcome, "applied");
    const deleteRequest = host.requests.filter((request) => request.path.endsWith("/mutations")).at(-1);
    assert.deepEqual(
      (deleteRequest?.body as { change: { delete?: boolean } }).change.delete,
      true,
    );

    const rejected = await session.executeMutation(
      { Document: "d-03", Put: { bad: { nested: true } as unknown as SyncV2Value } },
      "idem-3",
    );
    assert.deepEqual(rejected, { Outcome: "rejected", Reason: "invalid_body" });
    await session.disconnect();
  });

  test("the SSE stream resumes with Last-Event-ID after a connection loss", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const session = createSession(host);
    await session.connect();

    host.commit("orders");
    host.pushTrigger("orders", 2);
    await waitFor(() => session.getCheckpoint().storage_sequence === 2n);

    host.closeStreams();
    const streamRequests = () =>
      host.requests.filter((request) => request.method === "GET" && request.path === "/v1/streams");
    await waitFor(() => streamRequests().length >= 2);
    assert.ok(streamRequests().length >= 2, "the stream reconnects");
    assert.equal(streamRequests().at(-1)?.lastEventId, "2");

    host.commit("orders");
    host.pushTrigger("orders", 3);
    await waitFor(() => session.getCheckpoint().storage_sequence === 3n);
    await session.disconnect();
  });

  test("the store snapshot honors the useSyncExternalStore contract and close is explicit", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const session = createSession(host);
    let notifications = 0;
    const unsubscribe = session.getStore().subscribe(() => {
      notifications += 1;
    });

    await session.connect();
    const snapshot = session.getStore().getSnapshot();
    assert.equal(snapshot.Status, "live");
    assert.equal(snapshot.Checkpoint.storage_sequence, 1n);
    assert.equal(snapshot.AppliedPayloadCodec, "loro-snapshot/1");
    assert.ok(notifications >= 1);

    host.commit("orders");
    host.pushTrigger("orders", 2);
    await waitFor(() => session.getStore().getSnapshot().Checkpoint.storage_sequence === 2n);
    assert.equal(session.getStore().getSnapshot().AppliedPayloadCodec, "loro-delta/1");

    unsubscribe();
    await session.disconnect();
    assert.equal(session.getStatus(), "closed");
    assert.equal(
      host.requests.some((request) => request.method === "POST" && request.path.endsWith("/close")),
      true,
      "disconnect closes the host session",
    );
  });

  test("construction fails closed without the backend-supplied tenant context", () => {
    const host = new FixtureApiHost();
    assert.throws(
      () =>
        new TerraScaleSyncV2Session({
          Url: "https://api.terrascale.test",
          Collection: "orders",
          Principal: "",
          Tenant: "tenant-a",
          Fetch: host.fetch,
        }),
      TypeError,
    );
  });
});
