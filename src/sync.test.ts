import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  canonicalQueryKey,
  parseCompleteTransitionEnvelope,
  parseSyncUInt64,
  SyncVersionMismatchError,
  TerraScaleSyncStore,
  type CompleteTransition,
  type DatabaseRevision,
  type SyncClientSnapshot,
  type SyncQueryIdentity,
  type SyncQuerySnapshot,
  type SyncStateVersion,
} from "./index.js";

const fixture = "{\"Protocol\":\"terrascale.sync.v1\",\"MessageType\":\"Transition\",\"ConnectionSequence\":\"4\",\"MessageSequence\":\"8\",\"Payload\":{\"FrameType\":\"Complete\",\"TransitionId\":\"01HZY3NDEKTSV4RRFFQ69G5FAV\",\"StartVersion\":{\"QuerySetVersion\":\"4\",\"DatabaseRevision\":{\"RevisionEpoch\":\"3\",\"CommitSequence\":\"8\"},\"IdentityVersion\":\"2\"},\"EndVersion\":{\"QuerySetVersion\":\"5\",\"DatabaseRevision\":{\"RevisionEpoch\":\"3\",\"CommitSequence\":\"9\"},\"IdentityVersion\":\"2\"},\"QueryUpdated\":[{\"QueryId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAV\",\"QueryFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Result\":{\"count\":2},\"ResultHash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"ExecutionRevision\":{\"RevisionEpoch\":\"3\",\"CommitSequence\":\"8\"},\"ValidatedThroughRevision\":{\"RevisionEpoch\":\"3\",\"CommitSequence\":\"9\"}}],\"QueryFailed\":[{\"QueryId\":\"01BX5ZZKBKACTAV9WEVGEMMVRZ\",\"Error\":{\"Code\":\"Query.Unsupported\",\"Message\":\"Unsupported query.\",\"Retryable\":false},\"ValidatedThroughRevision\":{\"RevisionEpoch\":\"3\",\"CommitSequence\":\"9\"}}],\"QueryRemoved\":[{\"QueryId\":\"01J00000000000000000000000\"}],\"ServerTiming\":{\"QueryMilliseconds\":2,\"ValidationMilliseconds\":1,\"SerializationMilliseconds\":1},\"IngressRegion\":\"gru\",\"ServingRegion\":\"sfo\",\"DatabaseRegion\":\"sfo\",\"RouteGeneration\":\"12\"}}";

const updatedQueryId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const unrelatedQueryId = "01K00000000000000000000000";

describe("TerraScale Sync v1 protocol", () => {
  test("parses the shared complete-transition fixture without losing UInt64 precision", () => {
    const envelope = parseCompleteTransitionEnvelope(JSON.parse(fixture));

    assert.equal(envelope.Protocol, "terrascale.sync.v1");
    assert.equal(envelope.Payload.FrameType, "Complete");
    assert.equal(envelope.Payload.QueryUpdated[0]?.QueryId, updatedQueryId);
    assert.equal(parseSyncUInt64("18446744073709551615"), 18_446_744_073_709_551_615n);
    assert.throws(() => parseSyncUInt64("18446744073709551616"), RangeError);
    assert.throws(() => parseSyncUInt64("01"), TypeError);
  });

  test("rejects unknown members, numeric versions, and inconsistent query revisions", () => {
    const unknownMember = JSON.parse(fixture) as Record<string, unknown>;
    unknownMember.Extra = true;
    assert.throws(() => parseCompleteTransitionEnvelope(unknownMember), TypeError);

    const numericVersion = JSON.parse(fixture) as {
      ConnectionSequence: unknown;
    };
    numericVersion.ConnectionSequence = 4;
    assert.throws(() => parseCompleteTransitionEnvelope(numericVersion), TypeError);

    const zeroSequence = JSON.parse(fixture) as {
      MessageSequence: unknown;
    };
    zeroSequence.MessageSequence = "0";
    assert.throws(() => parseCompleteTransitionEnvelope(zeroSequence), TypeError);

    const wrongRevision = JSON.parse(fixture) as {
      Payload: {
        QueryUpdated: Array<{
          ValidatedThroughRevision: { CommitSequence: string };
        }>;
      };
    };
    wrongRevision.Payload.QueryUpdated[0]!.ValidatedThroughRevision.CommitSequence = "8";
    assert.throws(() => parseCompleteTransitionEnvelope(wrongRevision), TypeError);

    const stationary = JSON.parse(fixture) as {
      Payload: { EndVersion: SyncStateVersion; StartVersion: SyncStateVersion };
    };
    stationary.Payload.EndVersion = stationary.Payload.StartVersion;
    assert.throws(() => parseCompleteTransitionEnvelope(stationary), TypeError);
  });
});

describe("TerraScaleSyncStore", () => {
  test("applies one transition atomically and notifies only changed query listeners", () => {
    const transition = parseCompleteTransitionEnvelope(JSON.parse(fixture)).Payload;
    const initial = snapshot(transition.StartVersion, [
      query(updatedQueryId, { count: 1 }, "c".repeat(64), transition.StartVersion.DatabaseRevision),
      query(unrelatedQueryId, { stable: true }, "d".repeat(64), transition.StartVersion.DatabaseRevision),
    ]);
    const store = new TerraScaleSyncStore(initial);
    const changed = store.acquire(updatedQueryId, identity({ filter: "updated" }));
    const unrelated = store.acquire(unrelatedQueryId, identity({ filter: "unrelated" }));
    let changedNotifications = 0;
    let unrelatedNotifications = 0;
    changed.subscribe(() => changedNotifications++);
    unrelated.subscribe(() => unrelatedNotifications++);

    store.applyTransition(transition);

    assert.deepEqual(changed.getSnapshot()?.Value, { count: 2 });
    assert.equal(changedNotifications, 1);
    assert.equal(unrelatedNotifications, 0);
    assert.equal(
      unrelated.getSnapshot()?.ValidatedThroughRevision.CommitSequence,
      transition.EndVersion.DatabaseRevision.CommitSequence,
    );
    assert.equal(Object.isFrozen(changed.getSnapshot()?.Value), true);
    const afterFirst = store.getSnapshot();
    assert.throws(() => store.applyTransition(transition), SyncVersionMismatchError);
    assert.equal(store.getSnapshot(), afterFirst);

    const nextVersion = {
      ...transition.EndVersion,
      DatabaseRevision: {
        ...transition.EndVersion.DatabaseRevision,
        CommitSequence: "10" as const,
      },
    };
    store.applyTransition(completeTransition(
      transition.EndVersion,
      nextVersion,
      { count: 2 },
      "b".repeat(64),
    ));
    assert.equal(changedNotifications, 1);
    assert.equal(
      changed.getSnapshot()?.ValidatedThroughRevision.CommitSequence,
      "10",
    );
  });

  test("reference-counts identical queries and releases only the final handle", () => {
    const store = new TerraScaleSyncStore();
    const queryIdentity = identity({ state: "open", order: ["name", "asc"] });
    const first = store.acquire(updatedQueryId, queryIdentity);
    const second = store.acquire("01BX5ZZKBKACTAV9WEVGEMMVRZ", {
      ...queryIdentity,
      Arguments: { order: ["name", "asc"], state: "open" },
    });

    assert.equal(first.QueryId, second.QueryId);
    assert.equal(store.getReferenceCount(queryIdentity), 2);
    first.release();
    first.release();
    assert.equal(store.getReferenceCount(queryIdentity), 1);
    second.release();
    assert.equal(store.getReferenceCount(queryIdentity), 0);
  });

  test("owns an immutable copy of the initial snapshot", () => {
    const start = version("1", "1");
    const value = { count: 1 };
    const initialQueries = new Map([
      [updatedQueryId, query(updatedQueryId, value, "a".repeat(64), start.DatabaseRevision)],
    ]);
    const store = new TerraScaleSyncStore({ Version: start, Queries: initialQueries });

    value.count = 2;
    initialQueries.clear();

    assert.deepEqual(store.getSnapshot().Queries.get(updatedQueryId)?.Value, { count: 1 });
    assert.equal(Object.isFrozen(store.getSnapshot().Queries.get(updatedQueryId)?.Value), true);
  });

  test("replays ordered optimistic layers and removes them only after catch-up", () => {
    const start = version("1", "1");
    const store = new TerraScaleSyncStore(snapshot(start, [
      query(updatedQueryId, [{ id: "a" }], "a".repeat(64), start.DatabaseRevision),
    ]));
    store.beginOptimisticMutation("mutation-1", [{
      QueryId: updatedQueryId,
      Apply: current => [...asArray(current), { id: "b" }],
    }]);
    store.beginOptimisticMutation("mutation-2", [{
      QueryId: updatedQueryId,
      Apply: current => [...asArray(current), { id: "c" }],
    }]);
    store.markMutationCommitted("mutation-1", revision("2"));

    store.applyTransition(completeTransition(
      start,
      version("2", "2"),
      [{ id: "a" }, { id: "b" }],
      "b".repeat(64),
    ));
    assert.deepEqual(store.getSnapshot().Queries.get(updatedQueryId)?.Value, [
      { id: "a" },
      { id: "b" },
      { id: "c" },
    ]);
    assert.equal(store.getSnapshot().Queries.get(updatedQueryId)?.Optimistic, true);

    store.failMutation("mutation-2");
    assert.deepEqual(store.getSnapshot().Queries.get(updatedQueryId)?.Value, [
      { id: "a" },
      { id: "b" },
    ]);
    assert.equal(store.getSnapshot().Queries.get(updatedQueryId)?.Optimistic, false);
  });

  test("does not retain an optimistic layer whose patch throws", () => {
    const start = version("1", "1");
    const store = new TerraScaleSyncStore(snapshot(start, [
      query(updatedQueryId, { count: 1 }, "a".repeat(64), start.DatabaseRevision),
    ]));
    const before = store.getSnapshot();

    assert.throws(() => store.beginOptimisticMutation("invalid", [{
      QueryId: updatedQueryId,
      Apply: () => {
        throw new Error("invalid patch");
      },
    }]), /invalid patch/);
    assert.equal(store.getSnapshot(), before);
    assert.doesNotThrow(() => store.beginOptimisticMutation("invalid", [{
      QueryId: updatedQueryId,
      Apply: () => ({ count: 2 }),
    }]));
  });
});

describe("canonicalQueryKey", () => {
  test("is stable across object insertion order and includes authorization scope", () => {
    const left = identity({ filter: { state: "open", priority: 2 } });
    const right = identity({ filter: { priority: 2, state: "open" } });

    assert.equal(canonicalQueryKey(left), canonicalQueryKey(right));
    assert.notEqual(
      canonicalQueryKey(left),
      canonicalQueryKey({ ...right, AuthorizationScope: "different" }),
    );
    assert.throws(
      () => canonicalQueryKey({ ...left, Arguments: { invalid: Number.NaN } }),
      TypeError,
    );
  });
});

function identity(argumentsValue: SyncQueryIdentity["Arguments"]): SyncQueryIdentity {
  return {
    Database: "database",
    Tenant: "tenant",
    AuthorizationScope: "authorization-v1",
    Query: "from Tasks",
    Arguments: argumentsValue,
    SchemaVersion: "1",
    ConsistencyMode: "AuthoritativeSnapshot",
  };
}

function revision(sequence: string): DatabaseRevision {
  return { RevisionEpoch: "1", CommitSequence: sequence as `${bigint}` };
}

function version(querySet: string, sequence: string): SyncStateVersion {
  return {
    QuerySetVersion: querySet as `${bigint}`,
    DatabaseRevision: revision(sequence),
    IdentityVersion: "1",
  };
}

function query(
  queryId: string,
  value: SyncQuerySnapshot["Value"],
  resultHash: string,
  validatedThroughRevision: DatabaseRevision,
): SyncQuerySnapshot {
  return {
    QueryId: queryId,
    Value: value,
    ResultHash: resultHash,
    ValidatedThroughRevision: validatedThroughRevision,
    Optimistic: false,
  };
}

function snapshot(
  stateVersion: SyncStateVersion,
  queries: readonly SyncQuerySnapshot[],
): SyncClientSnapshot {
  return {
    Version: stateVersion,
    Queries: new Map(queries.map(item => [item.QueryId, item])),
  };
}

function completeTransition(
  start: SyncStateVersion,
  end: SyncStateVersion,
  result: SyncQuerySnapshot["Value"],
  resultHash: string,
): CompleteTransition {
  return {
    FrameType: "Complete",
    TransitionId: "01HZY3NDEKTSV4RRFFQ69G5FAV",
    StartVersion: start,
    EndVersion: end,
    QueryUpdated: [{
      QueryId: updatedQueryId,
      QueryFingerprint: "a".repeat(64),
      Result: result!,
      ResultHash: resultHash,
      ExecutionRevision: end.DatabaseRevision,
      ValidatedThroughRevision: end.DatabaseRevision,
    }],
    QueryFailed: [],
    QueryRemoved: [],
    IngressRegion: "gru",
    ServingRegion: "gru",
    DatabaseRegion: "gru",
    RouteGeneration: "1",
  };
}

function asArray(value: SyncQuerySnapshot["Value"]): readonly SyncQueryIdentity["Arguments"][] {
  assert.ok(Array.isArray(value));
  return value;
}
