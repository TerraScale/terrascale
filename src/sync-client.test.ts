import assert from "node:assert/strict";
import { Buffer } from "node:buffer";
import { describe, test } from "node:test";

import {
  syncWebSocketProtocol,
  TerraScaleSyncSession,
  type SyncWebSocket,
  type SyncWebSocketFactory,
} from "./sync/client.js";
import type { CompleteTransition, SyncStateVersion } from "./sync/protocol.js";
import type { JsonValue } from "./models.js";

const databaseId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const environmentId = "01BX5ZZKBKACTAV9WEVGEMMVRZ";
const sessionId = "01HZY3NDEKTSV4RRFFQ69G5FAV";
const mutationId = "01J00000000000000000000000";
const secondMutationId = "01K00000000000000000000000";
const transitionId = "01M00000000000000000000000";
const shaA = "a".repeat(64);
const shaB = "b".repeat(64);

describe("TerraScaleSyncSession transport", () => {
  test("authenticates browser tickets and server bearer sessions without exposing credentials", async () => {
    const browser = harness({
      TicketProvider: async () => "opaque_browser_capability",
    });
    const browserConnect = browser.session.connect();
    await waitFor(() => browser.sockets.length === 1);
    assert.deepEqual(browser.sockets[0]?.protocols, [
      syncWebSocketProtocol,
      "ticket.opaque_browser_capability",
    ]);
    browser.sockets[0]!.open();
    await browserConnect;
    assert.deepEqual(browser.sockets[0]!.sent.map(message => message.MessageType), ["Connect"]);
    assert.equal(JSON.stringify(browser.sockets[0]!.sent).includes("opaque_browser_capability"), false);
    browser.session.dispose();

    const server = harness({
      BearerTokenProvider: async () => "server-bearer-secret",
    });
    const serverConnect = server.session.connect();
    assert.deepEqual(server.sockets[0]?.protocols, [syncWebSocketProtocol]);
    server.sockets[0]!.open();
    await serverConnect;
    assert.deepEqual(
      server.sockets[0]!.sent.map(message => message.MessageType),
      ["Authenticate", "Connect"],
    );
    assert.equal(
      (server.sockets[0]!.sent[0]!.Payload as { BearerToken: string }).BearerToken,
      "server-bearer-secret",
    );
    server.session.dispose();
  });

  test("cancels opening sockets and closes failed native transports before reconnecting", async () => {
    const cancelled = harness();
    const controller = new AbortController();
    const opening = cancelled.session.connect(controller.signal);
    controller.abort(new Error("cancelled"));
    await assert.rejects(opening, /cancelled/);
    assert.equal(cancelled.sockets[0]!.lastCloseCode, 1000);
    await drain();
    assert.equal(cancelled.sockets.length, 1);
    cancelled.session.dispose();

    const failed = harness();
    const connecting = failed.session.connect();
    failed.sockets[0]!.failBeforeOpen();
    await assert.rejects(connecting, /failed|closed/i);
    assert.equal(failed.sockets[0]!.lastCloseCode, 1011);
    await waitFor(() => failed.sockets.length === 2);
    failed.session.dispose();
  });

  test("reconnects with stable session state, active queries, and unresolved mutations", async () => {
    const fixture = harness();
    const query = fixture.session.subscribeQuery(liveQuery({ state: "open" }));
    const mutation = fixture.session.executeMutation({
      ClientRequestId: mutationId,
      IdempotencyKey: "create-task-once",
      Mutation: "insert into Tasks",
      Arguments: { title: "one" },
    });
    const connecting = fixture.session.connect();
    fixture.sockets[0]!.open();
    await connecting;
    const firstConnect = sentPayload<ConnectPayload>(fixture.sockets[0]!, "Connect");
    assert.equal(firstConnect.SessionId, sessionId);
    assert.equal(firstConnect.ActiveQueries.length, 1);
    assert.deepEqual(firstConnect.PendingMutationIds, [mutationId]);
    assert.equal(countSent(fixture.sockets[0]!, "ExecuteMutation"), 1);

    fixture.sockets[0]!.server("Transition", transition(
      version("0", "0"),
      version("1", "1"),
      [update(query.QueryId, { count: 0 }, version("1", "1"))],
    ));
    await waitFor(() =>
      fixture.session.getConnectionSnapshot().LastAppliedVersion.DatabaseRevision.CommitSequence
        === "1");
    fixture.sockets[0]!.serverClose(1012, "draining");
    await waitFor(() => fixture.sockets.length === 2);
    fixture.sockets[1]!.open();
    await waitFor(() => countSent(fixture.sockets[1]!, "Connect") === 1);

    const resumed = sentPayload<ConnectPayload>(fixture.sockets[1]!, "Connect");
    assert.equal(resumed.SessionId, sessionId);
    assert.equal(fixture.sockets[1]!.sent[0]!.ConnectionSequence, "2");
    assert.equal(resumed.LastAppliedVersion?.DatabaseRevision.CommitSequence, "1");
    assert.equal(resumed.LastAcknowledgedVersion?.DatabaseRevision.CommitSequence, "1");
    assert.equal(resumed.MaximumObservedRevision.CommitSequence, "1");
    assert.equal(resumed.ActiveQueries[0]?.QueryId, query.QueryId);
    assert.deepEqual(resumed.PendingMutationIds, [mutationId]);
    assert.equal(countSent(fixture.sockets[1]!, "ExecuteMutation"), 1);

    fixture.sockets[1]!.server("MutationResult", {
      ClientRequestId: mutationId,
      Succeeded: true,
      Result: { created: true },
      CommittedRevision: revision("1"),
    });
    assert.equal((await mutation as { created: boolean }).created, true);
    fixture.session.dispose();
  });

  test("retries a mutation without a sequence gap when native send fails", async () => {
    const fixture = harness();
    await connect(fixture);
    fixture.sockets[0]!.throwOnNextSend = true;
    const mutation = fixture.session.executeMutation({
      ClientRequestId: mutationId,
      IdempotencyKey: "native-send-retry",
      Mutation: "insert into Tasks",
      Arguments: { title: "retry" },
    });
    assert.equal(fixture.sockets[0]!.lastCloseCode, 1012);
    await waitFor(() => fixture.sockets.length === 2);
    fixture.sockets[1]!.open();
    await waitFor(() => countSent(fixture.sockets[1]!, "ExecuteMutation") === 1);
    assert.deepEqual(
      fixture.sockets[1]!.sent.map(message => message.MessageSequence),
      ["1", "2", "3"],
    );
    fixture.sockets[1]!.server("MutationResult", {
      ClientRequestId: mutationId,
      Succeeded: true,
      CommittedRevision: revision("1"),
    });
    await mutation;
    fixture.session.dispose();
  });

  test("shares identical queries, modifies the wire set only on edge references, and disposes once", async () => {
    const fixture = harness();
    await connect(fixture);
    const first = fixture.session.subscribeQuery(liveQuery({
      state: "open",
      order: ["name", "asc"],
    }));
    const second = fixture.session.subscribeQuery(liveQuery({
      order: ["name", "asc"],
      state: "open",
    }));
    assert.equal(first.QueryId, second.QueryId);
    assert.equal(countSent(fixture.sockets[0]!, "ModifyQuerySet"), 1);

    fixture.sockets[0]!.server("Transition", transition(
      version("0", "0"),
      version("1", "0"),
      [update(first.QueryId, [], version("1", "0"))],
    ));
    await waitFor(() => fixture.session.store.getSnapshot().Version.QuerySetVersion === "1");
    first.release();
    first.release();
    assert.equal(countSent(fixture.sockets[0]!, "ModifyQuerySet"), 1);
    second.release();
    assert.equal(countSent(fixture.sockets[0]!, "ModifyQuerySet"), 2);
    assert.deepEqual(
      sentPayload<{ RemoveQuery: Array<{ QueryId: string }> }>(
        fixture.sockets[0]!,
        "ModifyQuerySet",
        1,
      ).RemoveQuery,
      [{ QueryId: first.QueryId }],
    );

    let disposalNotifications = 0;
    fixture.session.subscribeConnection(() => disposalNotifications++);
    fixture.session.dispose();
    fixture.session.dispose();
    assert.equal(fixture.session.getConnectionSnapshot().Status, "disposed");
    assert.equal(disposalNotifications, 1);
    assert.equal(fixture.sockets[0]!.closeCalls, 1);
  });

  test("removes old identity results atomically and clears protected state on auth failure", async () => {
    const fixture = harness();
    const first = fixture.session.subscribeQuery(liveQuery({ state: "first" }));
    const second = fixture.session.subscribeQuery(liveQuery({ state: "second" }));
    await connect(fixture);
    fixture.sockets[0]!.server("Transition", transition(
      version("0", "0"),
      version("1", "1"),
      [
        update(first.QueryId, { visible: "first" }, version("1", "1")),
        update(second.QueryId, { visible: "second" }, version("1", "1")),
      ],
    ));
    await waitFor(() => fixture.session.store.getSnapshot().Queries.size === 2);
    let secondNotifications = 0;
    second.subscribe(() => secondNotifications++);

    fixture.sockets[0]!.server("Transition", transition(
      version("1", "1"),
      version("1", "1", "1"),
      [update(first.QueryId, { visible: "new-identity" }, version("1", "1", "1"))],
    ));
    await waitFor(() =>
      fixture.session.store.getSnapshot().Version.IdentityVersion === "1");
    assert.deepEqual(first.getSnapshot()?.Value, { visible: "new-identity" });
    assert.equal(second.getSnapshot(), undefined);
    assert.equal(secondNotifications, 1);

    fixture.sockets[0]!.server("AuthenticationError", {
      Error: { Code: "Auth.Expired", Message: "Authentication expired.", Retryable: true },
    });
    await waitFor(() =>
      fixture.session.getConnectionSnapshot().Status === "authentication-error"
      || fixture.session.getConnectionSnapshot().Status === "reconnecting");
    assert.equal(fixture.session.store.getSnapshot().Queries.size, 0);
    fixture.session.dispose();
  });

  test("keeps old state until an explicit resync installs the complete replacement atomically", async () => {
    const fixture = harness();
    const first = fixture.session.subscribeQuery(liveQuery({ state: "first" }));
    const second = fixture.session.subscribeQuery(liveQuery({ state: "second" }));
    await connect(fixture);
    fixture.sockets[0]!.server("Transition", transition(
      version("0", "0"),
      version("1", "1"),
      [
        update(first.QueryId, { value: 1 }, version("1", "1")),
        update(second.QueryId, { value: 2 }, version("1", "1")),
      ],
    ));
    await waitFor(() => fixture.session.store.getSnapshot().Queries.size === 2);

    fixture.sockets[0]!.server("ResyncRequired", {
      Reason: "The retained cursor expired.",
      EarliestRetainedRevision: revision("5"),
      LastAcknowledgedVersion: version("1", "1"),
    });
    await waitFor(() => fixture.sockets.length === 2);
    assert.deepEqual(first.getSnapshot()?.Value, { value: 1 });
    assert.deepEqual(second.getSnapshot()?.Value, { value: 2 });
    fixture.sockets[1]!.open();
    await waitFor(() => countSent(fixture.sockets[1]!, "Connect") === 1);
    assert.equal(fixture.session.getConnectionSnapshot().Status, "resync-required");
    const reconnect = sentPayload<ConnectPayload>(fixture.sockets[1]!, "Connect");
    assert.equal(reconnect.LastAppliedVersion, undefined);
    assert.equal(reconnect.LastAcknowledgedVersion, undefined);

    fixture.sockets[1]!.server("Transition", transition(
      version("1", "5"),
      version("2", "6"),
      [
        update(first.QueryId, { value: 10 }, version("2", "6")),
        update(second.QueryId, { value: 20 }, version("2", "6")),
      ],
    ));
    await waitFor(() =>
      fixture.session.store.getSnapshot().Version.DatabaseRevision.CommitSequence === "6");
    assert.deepEqual(first.getSnapshot()?.Value, { value: 10 });
    assert.deepEqual(second.getSnapshot()?.Value, { value: 20 });
    assert.equal(fixture.session.getConnectionSnapshot().Status, "connected");
    fixture.session.dispose();
  });

  test("validates every chunk before applying a large transition", async () => {
    const fixture = harness();
    const query = fixture.session.subscribeQuery(liveQuery({ large: true }));
    await connect(fixture);
    const payload = transition(
      version("0", "0"),
      version("1", "1"),
      [update(query.QueryId, "x".repeat(300_000), version("1", "1"))],
    );
    const bytes = Buffer.from(JSON.stringify(payload));
    const digest = Buffer.from(await crypto.subtle.digest("SHA-256", bytes)).toString("hex");
    const chunks = [
      bytes.subarray(0, 256 * 1024),
      bytes.subarray(256 * 1024),
    ];
    for (let index = 0; index < chunks.length; index++) {
      fixture.sockets[0]!.server("Transition", {
        FrameType: "Chunk",
        TransitionId: payload.TransitionId,
        ChunkIndex: index,
        ChunkCount: chunks.length,
        UncompressedLength: bytes.length,
        Sha256: digest,
        Data: chunks[index]!.toString("base64"),
      });
      if (index === 0) {
        await drain();
        assert.equal(query.getSnapshot(), undefined);
      }
    }
    await waitFor(() => typeof query.getSnapshot()?.Value === "string");
    assert.equal((query.getSnapshot()!.Value as string).length, 300_000);
    await drain();
    assert.equal(
      countSent(fixture.sockets[0]!, "AcknowledgeTransition"),
      1,
      JSON.stringify({
        sent: fixture.sockets[0]!.sent.map(message => message.MessageType),
        connection: fixture.session.getConnectionSnapshot(),
        close: fixture.sockets[0]!.lastCloseCode,
      }),
    );
    fixture.session.dispose();
  });

  test("rejects slow-client buffer overflow, stale route generations, and revision regression", async () => {
    const slow = harness();
    await connect(slow);
    const oversizedQueueItem = " ".repeat(7 * 1024 * 1024);
    slow.sockets[0]!.messageRaw(oversizedQueueItem);
    slow.sockets[0]!.messageRaw(oversizedQueueItem);
    assert.equal(slow.sockets[0]!.lastCloseCode, 1012);
    slow.session.dispose();

    const route = harness();
    await connect(route);
    route.sockets[0]!.server("RegionRouteChanged", {
      IngressRegion: "gru",
      ServingRegion: "sfo",
      DatabaseRegion: "sfo",
      RouteGeneration: "2",
      ReconnectRequired: false,
    });
    route.sockets[0]!.server("RegionRouteChanged", {
      IngressRegion: "gru",
      ServingRegion: "gru",
      DatabaseRegion: "gru",
      RouteGeneration: "1",
      ReconnectRequired: false,
    });
    await waitFor(() => route.sockets[0]!.lastCloseCode === 1002);
    assert.match(route.session.getConnectionSnapshot().LastError ?? "", /generation regressed/);
    route.session.dispose();

    const revisionFence = harness();
    await connect(revisionFence);
    revisionFence.sockets[0]!.server("ResyncRequired", {
      Reason: "History expired.",
      EarliestRetainedRevision: revision("5"),
    });
    await waitFor(() => revisionFence.sockets.length === 2);
    revisionFence.sockets[1]!.open();
    await waitFor(() => countSent(revisionFence.sockets[1]!, "Connect") === 1);
    revisionFence.sockets[1]!.server("Transition", transition(
      version("0", "0"),
      version("0", "4"),
      [],
    ));
    await waitFor(() => revisionFence.sockets[1]!.lastCloseCode === 1002);
    assert.match(
      revisionFence.session.getConnectionSnapshot().LastError ?? "",
      /regressed the observed database revision/,
    );
    revisionFence.session.dispose();
  });

  test("replays optimistic layers across transitions and removes them only on catch-up or failure", async () => {
    const fixture = harness();
    const query = fixture.session.subscribeQuery(liveQuery({ aggregate: true }));
    await connect(fixture);
    fixture.sockets[0]!.server("Transition", transition(
      version("0", "0"),
      version("1", "1"),
      [update(query.QueryId, { count: 0 }, version("1", "1"))],
    ));
    await waitFor(() => query.getSnapshot()?.Value !== undefined);

    const committed = fixture.session.executeMutation({
      ClientRequestId: mutationId,
      IdempotencyKey: "increment-once",
      Mutation: "update Counters",
      Arguments: { delta: 1 },
      OptimisticPatches: [{
        QueryId: query.QueryId,
        Apply: current => ({ count: count(current) + 1 }),
      }],
    });
    assert.deepEqual(query.getSnapshot()?.Value, { count: 1 });
    assert.equal(query.getSnapshot()?.Optimistic, true);
    fixture.sockets[0]!.server("MutationResult", {
      ClientRequestId: mutationId,
      Succeeded: true,
      Result: { accepted: true },
      CommittedRevision: revision("2"),
    });
    assert.equal((await committed as { accepted: boolean }).accepted, true);
    assert.equal(
      fixture.session.getConnectionSnapshot().MaximumObservedRevision.CommitSequence,
      "2",
    );
    assert.equal(query.getSnapshot()?.Optimistic, true);

    fixture.sockets[0]!.server("Transition", transition(
      version("1", "1"),
      version("1", "2"),
      [update(query.QueryId, { count: 1 }, version("1", "2"))],
    ));
    await waitFor(() => query.getSnapshot()?.Optimistic === false);
    assert.deepEqual(query.getSnapshot()?.Value, { count: 1 });

    const failed = fixture.session.executeMutation({
      ClientRequestId: secondMutationId,
      IdempotencyKey: "increment-fails",
      Mutation: "update Counters",
      Arguments: { delta: 1 },
      OptimisticPatches: [{
        QueryId: query.QueryId,
        Apply: current => ({ count: count(current) + 1 }),
      }],
    });
    assert.deepEqual(query.getSnapshot()?.Value, { count: 2 });
    fixture.sockets[0]!.server("MutationResult", {
      ClientRequestId: secondMutationId,
      Succeeded: false,
      Error: { Code: "Mutation.Rejected", Message: "Rejected.", Retryable: false },
    });
    await assert.rejects(failed, /Rejected/);
    assert.deepEqual(query.getSnapshot()?.Value, { count: 1 });
    assert.equal(query.getSnapshot()?.Optimistic, false);
    fixture.session.dispose();
  });
});

type ConnectPayload = {
  SessionId: string;
  LastAppliedVersion?: SyncStateVersion;
  LastAcknowledgedVersion?: SyncStateVersion;
  MaximumObservedRevision: ReturnType<typeof revision>;
  ActiveQueries: Array<{ QueryId: string }>;
  PendingMutationIds: string[];
};

type WireEnvelope = {
  Protocol: string;
  MessageType: string;
  ConnectionSequence: string;
  MessageSequence: string;
  Payload: unknown;
};

class FakeWebSocket implements SyncWebSocket {
  readyState = 0;
  bufferedAmount = 0;
  protocol = syncWebSocketProtocol;
  binaryType: BinaryType = "blob";
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent<unknown>) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  readonly sent: WireEnvelope[] = [];
  closeCalls = 0;
  lastCloseCode: number | undefined;
  throwOnNextSend = false;
  #serverSequence = 0n;

  public constructor(readonly protocols: readonly string[]) {}

  public open(): void {
    this.readyState = 1;
    this.onopen?.(new Event("open"));
  }

  public send(data: string): void {
    assert.equal(this.readyState, 1);
    if (this.throwOnNextSend) {
      this.throwOnNextSend = false;
      throw new Error("native send failed");
    }
    this.sent.push(JSON.parse(data) as WireEnvelope);
  }

  public close(code = 1000, reason = ""): void {
    if (this.readyState === 3) {
      return;
    }
    this.closeCalls++;
    this.lastCloseCode = code;
    this.readyState = 3;
    this.onclose?.({ code, reason } as CloseEvent);
  }

  public server(messageType: string, payload: unknown): void {
    const connectionSequence = this.sent[0]?.ConnectionSequence ?? "1";
    this.messageRaw(JSON.stringify({
      Protocol: "terrascale.sync.v1",
      MessageType: messageType,
      ConnectionSequence: connectionSequence,
      MessageSequence: (++this.#serverSequence).toString(),
      Payload: payload,
    }));
  }

  public messageRaw(data: string): void {
    this.onmessage?.({ data } as MessageEvent<string>);
  }

  public serverClose(code: number, reason: string): void {
    this.close(code, reason);
  }

  public failBeforeOpen(): void {
    this.onerror?.(new Event("error"));
  }
}

function harness(
  authentication: {
    TicketProvider?: () => Promise<string>;
    BearerTokenProvider?: () => Promise<string>;
  } = { BearerTokenProvider: async () => "server-token" },
): {
  session: TerraScaleSyncSession;
  sockets: FakeWebSocket[];
} {
  const sockets: FakeWebSocket[] = [];
  const factory: SyncWebSocketFactory = (_url, protocols) => {
    const socket = new FakeWebSocket(protocols);
    sockets.push(socket);
    return socket;
  };
  return {
    session: new TerraScaleSyncSession({
      Url: "wss://sync.example.test/api/v1/sync",
      DatabaseId: databaseId,
      EnvironmentId: environmentId,
      Tenant: "tenant",
      AuthorizationScope: "authorization-v1",
      SessionId: sessionId,
      WebSocketFactory: factory,
      ReconnectDelayMilliseconds: () => 0,
      HeartbeatIntervalMilliseconds: 0,
      HeartbeatTimeoutMilliseconds: 1,
      ...authentication,
    }),
    sockets,
  };
}

async function connect(fixture: ReturnType<typeof harness>): Promise<void> {
  const connecting = fixture.session.connect();
  fixture.sockets[0]!.open();
  await connecting;
}

function liveQuery(argumentsValue: JsonValue) {
  return {
    Query: "from Tasks",
    Arguments: argumentsValue,
    SchemaVersion: "1" as const,
    ConsistencyMode: "AuthoritativeSnapshot" as const,
  };
}

function revision(sequence: string) {
  return {
    RevisionEpoch: "1" as const,
    CommitSequence: sequence as `${bigint}`,
  };
}

function version(
  querySet: string,
  sequence: string,
  identity = "0",
): SyncStateVersion {
  return {
    QuerySetVersion: querySet as `${bigint}`,
    DatabaseRevision: sequence === "0"
      ? { RevisionEpoch: "0", CommitSequence: "0" }
      : revision(sequence),
    IdentityVersion: identity as `${bigint}`,
  };
}

function update(
  queryId: string,
  result: JsonValue,
  validated: SyncStateVersion,
) {
  return {
    QueryId: queryId,
    QueryFingerprint: shaA,
    Result: result,
    ResultHash: shaB,
    ExecutionRevision: validated.DatabaseRevision,
    ValidatedThroughRevision: validated.DatabaseRevision,
  };
}

function transition(
  start: SyncStateVersion,
  end: SyncStateVersion,
  updated: ReturnType<typeof update>[],
): CompleteTransition {
  return {
    FrameType: "Complete",
    TransitionId: transitionId,
    StartVersion: start,
    EndVersion: end,
    QueryUpdated: updated,
    QueryFailed: [],
    QueryRemoved: [],
    IngressRegion: "gru",
    ServingRegion: "gru",
    DatabaseRegion: "gru",
    RouteGeneration: "1",
  };
}

function sentPayload<T>(
  socket: FakeWebSocket,
  messageType: string,
  index = 0,
): T {
  return socket.sent.filter(message => message.MessageType === messageType)[index]!.Payload as T;
}

function countSent(socket: FakeWebSocket, messageType: string): number {
  return socket.sent.filter(message => message.MessageType === messageType).length;
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (predicate()) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 1));
  }
  assert.fail("Timed out waiting for Sync state.");
}

async function drain(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
}

function count(value: JsonValue | undefined): number {
  assert.ok(value !== null && typeof value === "object" && !Array.isArray(value));
  const result = value as { count?: unknown };
  assert.equal(typeof result.count, "number");
  return result.count as number;
}
