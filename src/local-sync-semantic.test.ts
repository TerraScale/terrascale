import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, describe, test } from "node:test";

import {
  createTerraScaleSqliteRuntime,
  type LocalServerAddress,
} from "./local/index.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.test.js";
import type { SyncV2Value } from "./sync/v2/frame.js";

installLocalSqliteTestEnvironment();

type Runtime = ReturnType<typeof createTerraScaleSqliteRuntime>;

const runtimeDirectories = new Set<string>();

after(() => {
  for (const directory of runtimeDirectories) {
    rmSync(directory, { recursive: true, force: true });
  }
});

function createLocalTestRuntime(): Runtime {
  const directory = mkdtempSync(join(tmpdir(), "terrascale-local-sync-semantic-"));
  runtimeDirectories.add(directory);
  return createTerraScaleSqliteRuntime({
    sqlite: { enabled: true, databasePath: join(directory, "sync.sqlite") },
  });
}

async function started(runtime: Runtime): Promise<LocalServerAddress> {
  return runtime.start({ host: "127.0.0.1", port: 0 });
}

async function eventually(
  predicate: () => boolean,
  timeoutMilliseconds = 1_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (!predicate()) {
    if (Date.now() >= deadline) {
      throw new Error("Timed out waiting for local Sync state.");
    }
    await new Promise<void>(resolve => setTimeout(resolve, 2));
  }
}

function jsonHeaders(principal: string, tenant: string): Record<string, string> {
  return {
    "content-type": "application/json",
    "x-tb-principal": principal,
    "x-tb-tenant": tenant,
  };
}

async function post(
  address: LocalServerAddress,
  path: string,
  principal: string,
  tenant: string,
  body: unknown,
  idempotencyKey?: string,
): Promise<Response> {
  return fetch(`${address.origin}${path}`, {
    method: "POST",
    headers: {
      ...jsonHeaders(principal, tenant),
      ...(idempotencyKey === undefined ? {} : { "idempotency-key": idempotencyKey }),
    },
    body: JSON.stringify(body),
  });
}

async function setup(
  address: LocalServerAddress,
  principal: string,
  tenant: string,
  collection = "tasks",
  resume?: string,
): Promise<{ readonly sessionId: number; readonly checkpoint: string }> {
  const response = await post(
    address,
    "/v1/sync/sessions",
    principal,
    tenant,
    resume === undefined ? { collection } : { collection, resume },
  );
  assert.equal(response.status, 200);
  return await response.json() as { readonly sessionId: number; readonly checkpoint: string };
}

function decodePayload(payload: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(payload, "base64").toString("utf8")) as Record<string, unknown>;
}

function materializer(
  payload: Uint8Array,
  codec: "terrabase-native/1" | "terrabase-snapshot/1",
): ReadonlyMap<string, Readonly<Record<string, SyncV2Value>>> {
  const body = JSON.parse(new TextDecoder().decode(payload)) as {
    documents?: readonly { document: string; put?: Readonly<Record<string, SyncV2Value>>; delete?: boolean }[];
    changes?: readonly { document: string; put?: Readonly<Record<string, SyncV2Value>>; delete?: boolean }[];
  };
  const entries = codec === "terrabase-snapshot/1" ? body.documents ?? [] : body.changes ?? [];
  const result = new Map<string, Readonly<Record<string, SyncV2Value>>>();
  for (const entry of entries) {
    if (entry.delete === true) {
      result.delete(entry.document);
    } else if (entry.put !== undefined) {
      result.set(entry.document, entry.put);
    }
  }
  return result;
}

async function readSseUntil(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  pattern: RegExp,
): Promise<string> {
  let text = "";
  for (let attempt = 0; attempt < 8; attempt++) {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const result = await Promise.race([
        reader.read(),
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => reject(new Error("Timed out waiting for SSE event.")), 1_000);
        }),
      ]);
      text += new TextDecoder().decode(result.value);
      if (pattern.test(text) || result.done) {
        return text;
      }
    } finally {
      if (timer !== undefined) clearTimeout(timer);
    }
  }
  throw new Error("SSE event was not delivered.");
}

describe("local Sync v2 semantic runtime", () => {
  test("runs setup, delta, snapshot, acknowledgement, mutation replay/conflict, and close over real HTTP", async () => {
    const runtime = createLocalTestRuntime();
    try {
      const address = await started(runtime);
      const session = runtime.createSyncV2Session({
        Collection: "tasks",
        Principal: "principal-v2",
        Tenant: "tenant-v2",
        Materializer: materializer,
      });
      await session.connect();
      assert.equal(session.getStatus(), "live");
      assert.equal(session.getStoreSnapshot().AppliedDeltaBatches, 0);

      const applied = await session.executeMutation({ Document: "task-1", Put: { state: "open" } }, "v2-mutation-once");
      const replay = await session.executeMutation({ Document: "task-1", Put: { state: "open" } }, "v2-mutation-once");
      assert.deepEqual(replay, applied);
      assert.equal(applied.Outcome, "applied");
      await assert.rejects(
        session.executeMutation({ Document: "task-1", Put: { state: "closed" } }, "v2-mutation-once"),
        /idempotency_key_reused/u,
      );
      await eventually(() => session.getStoreSnapshot().AppliedDeltaBatches === 1);
      assert.deepEqual(session.getStore().getDocuments().get("task-1"), { state: "open" });

      await session.resync();
      assert.equal(session.getStoreSnapshot().AdoptedSnapshots, 1);
      assert.equal(session.getStoreSnapshot().AppliedPayloadCodec, "terrabase-snapshot/1");
      assert.deepEqual(session.getStore().getDocuments().get("task-1"), { state: "open" });
      const checkpoint = session.getCheckpointBase64();
      const acknowledged = await post(address, "/v1/sync/sessions/1/ack", "principal-v2", "tenant-v2", { checkpoint });
      assert.equal(acknowledged.status, 200);

      const directSnapshot = await post(address, "/v1/sync/sessions/1/snapshot", "principal-v2", "tenant-v2", {});
      assert.equal(directSnapshot.status, 200);
      const snapshotBody = await directSnapshot.json() as { payload: string; payloadCodec: string };
      assert.equal(snapshotBody.payloadCodec, "terrabase-snapshot/1");
      assert.equal(JSON.stringify(decodePayload(snapshotBody.payload)).includes("task-1"), true);

      await session.disconnect();
      assert.equal(session.getStatus(), "closed");
      const closed = await post(address, "/v1/sync/sessions/1/delta", "principal-v2", "tenant-v2", { from: checkpoint });
      assert.equal(closed.status, 410);
      assert.equal((await closed.json() as { code: string }).code, "session_closed");
    } finally {
      await runtime.close();
    }
  });

  test("resumes from a durable checkpoint without replaying already applied deltas", async () => {
    const runtime = createLocalTestRuntime();
    try {
      const first = runtime.createSyncV2Session({
        Collection: "resume",
        Principal: "principal-resume",
        Tenant: "tenant-resume",
        Materializer: materializer,
      });
      await first.connect();
      await first.executeMutation({ Document: "r-1", Put: { n: 1 } }, "resume-key");
      await eventually(() => first.getStoreSnapshot().AppliedDeltaBatches === 1);
      const checkpoint = first.getCheckpointBase64();
      await first.disconnect();

      const resumed = runtime.createSyncV2Session({
        Collection: "resume",
        Principal: "principal-resume",
        Tenant: "tenant-resume",
        ResumeCheckpoint: checkpoint,
        Materializer: materializer,
      });
      await resumed.connect();
      assert.equal(resumed.getCheckpointBase64(), checkpoint);
      assert.equal(resumed.getStoreSnapshot().AppliedDeltaBatches, 0);
      await resumed.disconnect();
    } finally {
      await runtime.close();
    }
  });

  test("delivers SSE triggers, and replays changes after Last-Event-ID", async () => {
    const runtime = createLocalTestRuntime();
    try {
      const address = await started(runtime);
      const stream = await fetch(`${address.origin}/v1/streams?collections=${encodeURIComponent("events")}`, {
        headers: { ...jsonHeaders("principal-sse", "tenant-sse"), accept: "text/event-stream", "last-event-id": "0" },
      });
      assert.equal(stream.status, 200);
      const reader = stream.body!.getReader();
      const opened = await readSseUntil(reader, /event: opened/u);
      assert.match(opened, /event: opened/u);

      const created = await setup(address, "principal-sse", "tenant-sse", "events");
      const mutation = await post(address, `/v1/sync/sessions/${created.sessionId}/mutations`, "principal-sse", "tenant-sse", {
        document: "event-1",
        change: { put: [{ name: "kind", value: "created" }] },
      }, "sse-key");
      assert.equal(mutation.status, 200);
      const trigger = await readSseUntil(reader, /event: trigger/u);
      assert.match(trigger, /event: trigger/u);
      assert.match(trigger, /"sequence":1/u);
      await reader.cancel();

      const replay = await fetch(`${address.origin}/v1/streams?collections=events`, {
        headers: { ...jsonHeaders("principal-sse", "tenant-sse"), accept: "text/event-stream", "last-event-id": "0" },
      });
      assert.equal(replay.status, 200);
      const replayReader = replay.body!.getReader();
      try {
        const replayChunk = await readSseUntil(replayReader, /event: trigger/u);
        assert.match(replayChunk, /event: trigger/u);
        assert.match(replayChunk, /"sequence":1/u);
      } finally {
        await replayReader.cancel();
      }

      const noReplay = await fetch(`${address.origin}/v1/streams?collections=events`, {
        headers: { ...jsonHeaders("principal-sse", "tenant-sse"), accept: "text/event-stream", "last-event-id": "1" },
      });
      assert.equal(noReplay.status, 200);
      const noReplayReader = noReplay.body!.getReader();
      try {
        const openingOnly = await readSseUntil(noReplayReader, /event: opened/u);
        assert.doesNotMatch(openingOnly, /event: trigger/u);
      } finally {
        await noReplayReader.cancel();
      }
    } finally {
      await runtime.close();
    }
  });

  test("enforces principal/tenant isolation and setup, stream, mutation, and checkpoint bounds", async () => {
    const runtime = createLocalTestRuntime();
    try {
      const address = await started(runtime);
      const missingIdentity = await fetch(`${address.origin}/v1/streams?collections=tasks`, { headers: { accept: "text/event-stream", "last-event-id": "0" } });
      assert.equal(missingIdentity.status, 401);
      const oversizedSetup = await post(address, "/v1/sync/sessions", "principal-bounds", "tenant-bounds", { collection: "x".repeat(129) });
      assert.equal(oversizedSetup.status, 400);
      const malformedResume = await post(address, "/v1/sync/sessions", "principal-bounds", "tenant-bounds", { collection: "tasks", resume: "%%%" });
      assert.equal(malformedResume.status, 400);
      const malformedEventId = await fetch(`${address.origin}/v1/streams?collections=tasks`, {
        headers: { ...jsonHeaders("principal-bounds", "tenant-bounds"), accept: "text/event-stream", "last-event-id": "not-a-number" },
      });
      assert.equal(malformedEventId.status, 400);

      const owner = await setup(address, "principal-owner", "tenant-owner");
      const other = await setup(address, "principal-other", "tenant-other");
      const applied = await post(address, `/v1/sync/sessions/${owner.sessionId}/mutations`, "principal-owner", "tenant-owner", {
        document: "private",
        change: { put: [{ name: "owner", value: "owner" }] },
      }, "private-key");
      assert.equal(applied.status, 200);
      const malformedMutation = await post(address, `/v1/sync/sessions/${owner.sessionId}/mutations`, "principal-owner", "tenant-owner", {
        document: "invalid",
        change: { put: [], delete: true },
      }, "invalid-key");
      assert.equal(malformedMutation.status, 200);
      assert.deepEqual((await malformedMutation.json() as { outcome: { outcome: string; reason?: string } }).outcome, { outcome: "rejected", reason: "invalid_body" });
      const crossSession = await post(address, `/v1/sync/sessions/${owner.sessionId}/delta`, "principal-other", "tenant-other", { from: other.checkpoint });
      assert.equal(crossSession.status, 403);
      const isolatedDelta = await post(address, `/v1/sync/sessions/${other.sessionId}/delta`, "principal-other", "tenant-other", { from: other.checkpoint });
      assert.equal(isolatedDelta.status, 200);
      assert.equal((await isolatedDelta.json() as { commits: number }).commits, 0);

      const oversizedMutation = await post(address, `/v1/sync/sessions/${owner.sessionId}/mutations`, "principal-owner", "tenant-owner", {
        document: "x".repeat(257),
        change: { put: [{ name: "value", value: "bounded" }] },
      }, "large-key");
      assert.equal(oversizedMutation.status, 200);
      assert.deepEqual((await oversizedMutation.json() as { outcome: { outcome: string; reason?: string } }).outcome, { outcome: "rejected", reason: "invalid_body" });
    } finally {
      await runtime.close();
    }
  });

  test("rejects invalid typed v2 inputs and closes the lifecycle cleanly", async () => {
    const runtime = createLocalTestRuntime();
    try {
      assert.throws(() => runtime.createSyncV2Session({ Collection: "", Principal: "p", Tenant: "t" }), /Collection/iu);
      assert.throws(() => runtime.createSyncV2Session({ Collection: "tasks", Principal: "", Tenant: "t" }), /Principal/iu);
      assert.throws(() => runtime.createSyncV2Session({ Collection: "tasks", Principal: "p", Tenant: "" }), /Tenant/iu);
      const first = runtime.createSyncV2Session({ Collection: "lifecycle", Principal: "p", Tenant: "t" });
      const second = runtime.createSyncV2Session({ Collection: "lifecycle", Principal: "p2", Tenant: "t2" });
      await first.connect();
      await second.connect();
      await runtime.reset();
      assert.equal(first.getStatus(), "closed");
      assert.equal(second.getStatus(), "closed");
      await runtime.close();
      assert.throws(() => runtime.createSyncV2Session({ Collection: "tasks", Principal: "p", Tenant: "t" }), /runtime is closed/iu);
    } finally {
      await runtime.close();
    }
  });
});
