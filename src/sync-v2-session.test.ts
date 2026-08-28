import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  syncV2CanonicalCheckpoint,
  syncV2CheckpointFromBytes,
  syncV2CheckpointToBytes,
  SyncV2SessionError,
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
  #trafficRefusal: { readonly status: number; readonly code: string; readonly detail: string } | null = null;

  /**
   * Refuses every session-scoped request (deltas, snapshots, acks, streams)
   * with a typed error body, the shape a host emits after session expiry
   * (session_closed) or under sustained overload (overloaded).
   */
  public refuseSessionTraffic(status: number, code: string, detail: string): void {
    this.#trafficRefusal = { status, code, detail };
    this.closeStreams();
  }

  public restoreSessionTraffic(): void {
    this.#trafficRefusal = null;
  }

  /** Restores served traffic after a short delay (a blip, not an outage). */
  public restoreSessionTrafficSoon(delayMs = 20): void {
    setTimeout(() => {
      this.#trafficRefusal = null;
    }, delayMs);
  }

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

    if (
      this.#trafficRefusal !== null
      && (url.pathname.startsWith("/v1/sync/sessions/") || (method === "GET" && url.pathname === "/v1/streams"))
    ) {
      return Response.json(
        { code: this.#trafficRefusal.code, detail: this.#trafficRefusal.detail },
        { status: this.#trafficRefusal.status },
      );
    }

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

function createSession(
  host: FixtureApiHost,
  options?: { resume?: string; reconnectDelayMs?: number; maxReconnectAttempts?: number },
): TerraScaleSyncV2Session {
  return new TerraScaleSyncV2Session({
    Url: "https://api.terrascale.test",
    Collection: "orders",
    Principal: "principal-1",
    Tenant: "tenant-a",
    Fetch: host.fetch,
    ResumeCheckpoint: options?.resume,
    ReconnectDelayMilliseconds: options?.reconnectDelayMs,
    MaxReconnectAttempts: options?.maxReconnectAttempts,
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

  test("a bigint outside the safely representable integer range is refused before any request", async () => {
    const host = new FixtureApiHost();
    const session = createSession(host);
    await session.connect();
    const mutationsBefore = host.requests.filter((request) => request.path.endsWith("/mutations")).length;

    // 2^63 + 1: the JSON DTO would flatten it through Number and silently
    // corrupt the durable write (9223372036854775809 -> 9223372036854776000).
    const beyondSafe = 9_223_372_036_854_775_809n;
    await assert.rejects(
      session.executeMutation({ Document: "d-01", Put: { seq: beyondSafe } }, "idem-big"),
      (error: unknown) => {
        assert.ok(error instanceof SyncV2SessionError, "the refusal is typed");
        assert.equal(error.code, "invalid_mutation_value");
        return true;
      },
    );
    await assert.rejects(
      session.executeMutation({ Document: "d-01", Put: { seq: -beyondSafe } }, "idem-big"),
      (error: unknown) => error instanceof SyncV2SessionError,
    );
    assert.equal(
      host.requests.filter((request) => request.path.endsWith("/mutations")).length,
      mutationsBefore,
      "the out-of-range value never reaches the host",
    );

    // The safe boundary itself still serializes: MAX_SAFE_INTEGER rides the
    // integer discriminator exactly.
    const applied = await session.executeMutation(
      { Document: "d-02", Put: { seq: 9_007_199_254_740_991n } },
      "idem-safe",
    );
    assert.equal(applied.Outcome, "applied");
    const safeRequest = host.requests
      .filter((request) => request.path.endsWith("/mutations"))
      .at(-1);
    const put = (safeRequest?.body as { change: { put: Array<{ name: string; value: unknown }> } }).change.put;
    assert.deepEqual(put, [{ name: "seq", value: { integer: 9_007_199_254_740_991 } }]);
    await session.disconnect();
  });

  test("a non-JSON host answer is a typed malformed_frame rejection, not a raw SyntaxError", async () => {
    const refuseJson: SyncV2Fetch = async () =>
      new Response("gateway noise", { status: 200, headers: { "content-type": "application/json" } });
    const session = new TerraScaleSyncV2Session({
      Url: "https://api.terrascale.test",
      Collection: "orders",
      Principal: "principal-1",
      Tenant: "tenant-a",
      Fetch: refuseJson,
    });
    await assert.rejects(
      session.connect(),
      (error: unknown) => {
        assert.ok(error instanceof SyncV2SessionError, "the rejection is typed");
        assert.equal(error.code, "malformed_frame");
        return true;
      },
    );
    assert.equal(session.getStatus(), "failed");
  });

  test("a resume checkpoint that is not valid base64 is a typed rejection at construction", () => {
    assert.throws(
      () =>
        new TerraScaleSyncV2Session({
          Url: "https://api.terrascale.test",
          Collection: "orders",
          Principal: "principal-1",
          Tenant: "tenant-a",
          Fetch: async () => Response.json({}),
          ResumeCheckpoint: "!!!not-base64!!!",
        }),
      (error: unknown) => {
        assert.ok(error instanceof SyncV2SessionError, "the rejection is typed, not a raw DOMException");
        assert.equal(error.code, "malformed_frame");
        assert.match(error.detail ?? "", /resume checkpoint/u);
        return true;
      },
    );
  });

  test("host session expiry is a terminal failure with a recorded LastError and bounded retries", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const session = createSession(host);
    await session.connect();
    assert.equal(session.getStatus(), "live");

    host.refuseSessionTraffic(410, "session_closed", "the host session expired");
    await waitFor(() => session.getStatus() === "failed");

    assert.equal(session.getStoreSnapshot().Status, "failed");
    const lastError = session.getStoreSnapshot().LastError;
    assert.ok(lastError !== undefined, "LastError records the terminal failure");
    assert.match(lastError, /session_closed/u);

    // The retry ladder is bounded: after the terminal transition no further
    // session-scoped requests leave the client.
    const deltas = () => host.requests.filter((request) => request.path.endsWith("/delta")).length;
    const streams = () => host.requests.filter((request) => request.method === "GET" && request.path === "/v1/streams").length;
    const deltasAtFailure = deltas();
    const streamsAtFailure = streams();
    assert.ok(streamsAtFailure <= 4, `the retry count is bounded (stream attempts: ${streamsAtFailure})`);
    await new Promise((resolve) => setTimeout(resolve, 300));
    assert.equal(deltas(), deltasAtFailure, "no delta POSTs after the terminal failure");
    assert.equal(streams(), streamsAtFailure, "no stream reconnects after the terminal failure");

    // The terminal session no longer speaks for a host session; the
    // documented re-setup is a fresh connect (or a new session resumed from
    // the durable checkpoint, which survives the failure).
    await assert.rejects(
      session.executeMutation({ Document: "d-01", Put: { status: "x" } }, "idem-after"),
      (error: unknown) => error instanceof SyncV2SessionError && error.code === "session_closed",
    );
    await session.disconnect();
  });

  test("sustained transient failures exhaust the bounded backoff ladder and fail terminally", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const session = createSession(host, { reconnectDelayMs: 1 });
    await session.connect();
    assert.equal(session.getStatus(), "live");

    host.refuseSessionTraffic(503, "overloaded", "injected sustained overload");
    await waitFor(() => session.getStatus() === "failed");

    const snapshot = session.getStoreSnapshot();
    assert.equal(snapshot.Status, "failed");
    assert.ok(snapshot.LastError !== undefined, "LastError records the exhaustion");
    const streams = () => host.requests.filter((request) => request.method === "GET" && request.path === "/v1/streams").length;
    const streamsAtFailure = streams();
    // The default ladder is 8 attempts over the initial healthy stream —
    // bounded, not an unbounded fixed-interval storm.
    assert.ok(streamsAtFailure <= 12, `the retry ladder is bounded (stream attempts: ${streamsAtFailure})`);
    await new Promise((resolve) => setTimeout(resolve, 200));
    assert.equal(streams(), streamsAtFailure, "the ladder stops after exhaustion");
    await session.disconnect();
  });

  test("a transient failure inside the ladder recovers to live and resets the ladder", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    const session = createSession(host, { reconnectDelayMs: 1 });
    await session.connect();

    // One overloaded round trip, then the host recovers: the session must
    // ride the ladder back to live (not fail) and be able to do it again.
    host.refuseSessionTraffic(503, "overloaded", "a short overload");
    host.restoreSessionTrafficSoon();
    await waitFor(() =>
      host.requests.filter((request) => request.method === "GET" && request.path === "/v1/streams").length >= 2);
    await waitFor(() => session.getStoreSnapshot().Status === "live");

    host.commit("orders");
    host.pushTrigger("orders", 2);
    await waitFor(() => session.getCheckpoint().storage_sequence === 2n);
    assert.equal(session.getStatus(), "live");
    await session.disconnect();
  });

  test("a pathological event-stream line aborts the subscription with a typed stream error", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    let streamCancels = 0;
    let streamGets = 0;
    let abortSignals = 0;
    const pathologicalStream: SyncV2Fetch = async (input, init) => {
      const url = new URL(input.toString());
      if ((init?.method ?? "GET").toUpperCase() === "GET" && url.pathname === "/v1/streams") {
        streamGets += 1;
        init?.signal?.addEventListener("abort", () => {
          abortSignals += 1;
        });
        const stream = new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(new TextEncoder().encode("id: 1\nevent: trigger\ndata: "));
            // A pathological undelimited line: no newline ever arrives.
            controller.enqueue(new TextEncoder().encode("x".repeat(200_000)));
          },
          cancel() {
            streamCancels += 1;
          },
        });
        return new Response(stream, {
          status: 200,
          headers: { "content-type": "text/event-stream" },
        });
      }
      return host.fetch(input, init);
    };
    const session = new TerraScaleSyncV2Session({
      Url: "https://api.terrascale.test",
      Collection: "orders",
      Principal: "principal-1",
      Tenant: "tenant-a",
      Fetch: pathologicalStream,
      ReconnectDelayMilliseconds: 1,
      MaxReconnectAttempts: 2,
    });
    const observedErrors: string[] = [];
    session.getStore().subscribe(() => {
      const lastError = session.getStore().getSnapshot().LastError;
      if (lastError !== undefined) {
        observedErrors.push(lastError);
      }
    });

    await session.connect();
    await waitFor(() => session.getStatus() === "failed");

    assert.ok(
      observedErrors.some((error) => /stream_limit/u.test(error)),
      `the typed stream-limit error surfaced through the failure surface: ${JSON.stringify(observedErrors)}`,
    );
    assert.ok(
      abortSignals >= 1 || streamCancels >= 1,
      `the pathological subscription is aborted (fetch aborts: ${abortSignals}, cancels: ${streamCancels})`,
    );
    assert.ok(streamGets >= 2, "the torn-down stream was abandoned for the bounded reconnect ladder");
    await session.dispose();
  });

  test("a well-behaved multi-event chunk does not trip the line bound", async () => {
    const host = new FixtureApiHost();
    host.commit("orders");
    host.commit("orders");
    // One reader chunk carrying many complete newline-terminated trigger
    // events: every single line sits far below the 4096-unit line bound,
    // while the chunk as a whole is several times over it. A bound that
    // measures the chunk instead of the line false-positives exactly here.
    const events: string[] = [];
    for (let sequence = 1; sequence <= 80; sequence += 1) {
      events.push(
        `id: ${sequence}\nevent: trigger\n` +
        `data: ${JSON.stringify({ subscriptionId: 1, collection: "orders", sequence, operationId: `op-${sequence}` })}\n\n`,
      );
    }
    const chunk = events.join("");
    const longestLine = Math.max(...chunk.split("\n").map((line) => line.length));
    assert.ok(
      chunk.length > 4096 && longestLine <= 4096,
      `the fixture discriminates chunk length (${chunk.length}) from line length (${longestLine})`,
    );

    const chunkedStream: SyncV2Fetch = async (input, init) => {
      const url = new URL(input.toString());
      if ((init?.method ?? "GET").toUpperCase() === "GET" && url.pathname === "/v1/streams") {
        const stream = new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(new TextEncoder().encode(chunk));
          },
        });
        return new Response(stream, {
          status: 200,
          headers: { "content-type": "text/event-stream", "cache-control": "no-cache" },
        });
      }
      return host.fetch(input, init);
    };
    const session = new TerraScaleSyncV2Session({
      Url: "https://api.terrascale.test",
      Collection: "orders",
      Principal: "principal-1",
      Tenant: "tenant-a",
      Fetch: chunkedStream,
      ReconnectDelayMilliseconds: 1,
      MaxReconnectAttempts: 2,
    });
    const observedErrors: string[] = [];
    session.getStore().subscribe(() => {
      const lastError = session.getStore().getSnapshot().LastError;
      if (lastError !== undefined) {
        observedErrors.push(lastError);
      }
    });

    await session.connect();
    await waitFor(() =>
      host.requests.filter((request) => request.path.endsWith("/delta")).length >= 1);
    assert.equal(
      session.getCheckpoint().storage_sequence,
      2n,
      "the drained events drive the checkpoint-bounded pulls",
    );
    assert.equal(
      session.getStatus(),
      "live",
      "a fully well-behaved host keeps the session live",
    );
    assert.ok(
      !observedErrors.some((error) => /stream_limit/u.test(error)),
      `no false-positive stream limit: ${JSON.stringify(observedErrors)}`,
    );
    await session.dispose();
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
