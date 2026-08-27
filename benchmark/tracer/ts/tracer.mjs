// The SE-11 TypeScript SDK tracer: one node process embedding the real
// `@terrascale/sdk` sync-v2 session (the sibling TerraScale.TypeScript
// checkout's built dist, read-only) against the TerraBase C# API host,
// driven by a scripted plan, emitting a uniform JSONL observation log
// the orchestrator asserts against the SE-04 golden vectors and diffs
// against the .NET tracer's log.
//
// No secrets, no live systems: loopback hosts only, backend-context
// fixtures only. The payload stays opaque (the wrapped-codec decision);
// the tracer records payload byte identities (SHA-256 digests), never
// payload contents.

import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, watchFile, writeFileSync, existsSync, appendFileSync } from "node:fs";
import path from "node:path";

const specPath = process.argv[2];
if (specPath === undefined) {
  console.error("usage: node tracer.mjs <spec.json>");
  process.exit(2);
}
const spec = JSON.parse(readFileSync(specPath, "utf8"));

const sdkRoot = spec.sdk;
const { TerraScaleSyncV2Session } = await import(path.join(sdkRoot, "dist", "index.js"));

const outPath = spec.out;
mkdirSync(path.dirname(outPath), { recursive: true });

function emit(record) {
  // Synchronous appends: the orchestrator polls this file while the plan
  // runs, and no buffered write may be lost to a fast exit.
  appendFileSync(outPath, JSON.stringify(record) + "\n", "utf8");
}

function checkpointOf(checkpoint) {
  return {
    storageSequence: Number(checkpoint.storage_sequence),
    ends: checkpoint.ends.map((pair) => [Number(pair[0]), pair[1]]),
  };
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

/** The durable checkpoint file of this tracer role (resume across processes). */
const checkpointFile = spec.checkpointFile;

const state = {
  session: null,
  // The materializer hook stash: one payload identity per applied batch.
  pendingPayload: null,
  lastStatus: "idle",
  checkpoint: null,
  // Resolved when THIS connection's push-channel subscription (the
  // /v1/streams GET) is observed on the client's own wire — the
  // deterministic gate a measured write is held behind.
  streamSubscribed: null,
};

function recordStatus(status, lastError) {
  if (status !== state.lastStatus) {
    emit({ t: "status", status, lastError: lastError ?? null });
    state.lastStatus = status;
  }
}

/**
 * The observing fetch: delegates one-to-one to the global fetch (the SDK's
 * own default) and records the one wire fact the plan needs as a
 * deterministic gate — that the session's push-channel subscription (the
 * streams GET) was accepted by the host. The host registers the subscriber
 * and the engine's backbone subscription before it writes the stream's
 * first event, so the subscription being observable on the client means
 * every later trigger fans out to it. No request or response byte is
 * touched; the returned promise is the fetch's own.
 */
function observingFetch(onStreamSubscribed) {
  return (url, init) => {
    const pending = fetch(url, init);
    const method = String(init?.method ?? "GET").toUpperCase();
    if (method === "GET" && String(url).includes("/v1/streams")) {
      pending.then((response) => {
        if (response.ok) {
          onStreamSubscribed(String(url));
        }
      }).catch(() => {
        // A refused or dropped open is the SDK's own typed surface; the
        // gate simply stays unset until a subscription is observed.
      });
    }
    return pending;
  };
}

/** Builds one session wired to the observation log. */
function openSession(url, resumeBase64) {
  const materializer = (payload, codec) => {
    state.pendingPayload = { codec, sha256: sha256(payload), bytes: payload.length };
    return undefined; // memory-only store; the checkpoint is the state
  };
  const store = {
    save(checkpointBase64) {
      writeFileSync(checkpointFile, checkpointBase64, "utf8");
    },
  };
  let resolveSubscribed;
  state.streamSubscribed = new Promise((resolve) => {
    resolveSubscribed = resolve;
  });
  const options = {
    Url: url,
    Collection: spec.collection,
    Principal: spec.principal,
    Tenant: spec.tenant,
    Materializer: materializer,
    CheckpointStore: store,
    Fetch: observingFetch((streamUrl) => {
      emit({ t: "stream-opened", host: streamUrl.split("?")[0] });
      resolveSubscribed();
    }),
    ReconnectDelayMilliseconds: spec.reconnect?.delayMs ?? 100,
    MaxReconnectAttempts: spec.reconnect?.maxAttempts ?? 3,
  };
  if (resumeBase64 !== undefined && resumeBase64 !== null && resumeBase64 !== "") {
    options.ResumeCheckpoint = resumeBase64;
  }
  const session = new TerraScaleSyncV2Session(options);
  const observed = session.getStore();
  let lastSnapshot = observed.getSnapshot();
  observed.subscribe(() => {
    const next = observed.getSnapshot();
    if (next.Checkpoint !== lastSnapshot.Checkpoint) {
      const payload = state.pendingPayload ?? { codec: null, sha256: null, bytes: 0 };
      const adopted = lastSnapshot.AdoptedSnapshots < next.AdoptedSnapshots;
      emit({
        t: adopted ? "snapshot-adopted" : "delta-applied",
        checkpoint: checkpointOf(next.Checkpoint),
        codec: payload.codec,
        payloadSha256: payload.sha256,
        payloadBytes: payload.bytes,
      });
      state.checkpoint = next.Checkpoint;
      state.pendingPayload = null;
    }
    if (next.Status !== lastSnapshot.Status || next.LastError !== lastSnapshot.LastError) {
      recordStatus(next.Status, next.LastError);
    }
    lastSnapshot = next;
  });
  return session;
}

function durableCheckpoint() {
  return existsSync(checkpointFile) ? readFileSync(checkpointFile, "utf8") : "";
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function waitForFile(file, timeoutMs) {
  return new Promise((resolve, reject) => {
    if (existsSync(file)) {
      resolve();
      return;
    }
    const started = Date.now();
    const timer = setInterval(() => {
      if (existsSync(file)) {
        clearInterval(timer);
        resolve();
      } else if (Date.now() - started > timeoutMs) {
        clearInterval(timer);
        reject(new Error(`gate timeout waiting for ${path.basename(file)}`));
      }
    }, 20);
  });
}

async function waitCheckpoint(sequence, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const snapshot = state.session?.getStoreSnapshot();
    if (snapshot !== undefined && Number(snapshot.Checkpoint.storage_sequence) >= sequence) {
      return;
    }
    if (Date.now() > deadline) {
      throw new Error(`checkpoint timeout waiting for storage sequence ${sequence}`);
    }
    await sleep(25);
  }
}

async function waitFailed(timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (state.session !== null && state.session.getStatus() === "failed") {
      return;
    }
    if (Date.now() > deadline) {
      throw new Error("timeout waiting for the session's terminal failed status");
    }
    await sleep(25);
  }
}

/**
 * Waits for positive evidence that this session's push channel is
 * subscribed (the streams response observed on the client's own wire):
 * the deterministic gate a measured write is held behind. The TS core's
 * connect() opens the stream without awaiting it, so a commit landing
 * before the subscription is undeliverable — the pull is authoritative
 * but needs a hint.
 */
async function waitStream(timeoutMs) {
  const subscribed = state.streamSubscribed;
  if (subscribed === null) {
    throw new Error("the session is not open");
  }
  let timer;
  try {
    await Promise.race([
      subscribed,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => reject(new Error(`the delivery stream was not subscribed within ${timeoutMs} ms`)),
          timeoutMs,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function run() {
  emit({ t: "tracer", role: spec.role, sdk: "TerraScale.TypeScript dist", node: process.version });

  for (const step of spec.plan) {
    try {
      await runStep(step);
    } catch (error) {
      // A failed step is an observation, not a crash: the plan owns the
      // recovery choreography, and the orchestrator asserts on the log.
      const code = error?.code ?? error?.name ?? "step-failed";
      emit({
        t: "error",
        op: step.op,
        code,
        resyncRequired: error?.resyncRequired === true,
        message: String(error?.message ?? error),
      });
    }
  }
}

async function runStep(step) {
  switch (step.op) {
      case "connect": {
        let resume = "";
        if (step.resume === true) {
          resume = durableCheckpoint();
        } else if (typeof step.resumeFrom === "string") {
          resume = step.resumeFrom;
        }
        state.session = openSession(step.host ?? spec.host, resume);
        await state.session.connect();
        emit({ t: "opened", sessionId: null, resumed: resume !== "" });
        break;
      }
      case "mutate": {
        const request = step.delete === true
          ? { Document: step.document, Delete: true }
          : { Document: step.document, Put: step.put };
        const answer = await state.session.executeMutation(request, step.key);
        if (answer.Outcome === "applied") {
          emit({
            t: "mutation",
            key: step.key,
            document: step.document,
            outcome: "applied",
            sequence: Number(answer.Receipt.Sequence),
            operationId: answer.Receipt.OperationId,
          });
        } else {
          emit({ t: "mutation", key: step.key, document: step.document, outcome: "rejected", reason: answer.Reason });
        }
        break;
      }
      case "pull": {
        await state.session.pullNow();
        break;
      }
      case "resync": {
        await state.session.resync();
        break;
      }
      case "waitCheckpoint": {
        await waitCheckpoint(step.sequence, step.timeoutMs ?? 20000);
        break;
      }
      case "waitStream": {
        await waitStream(step.timeoutMs ?? 20000);
        break;
      }
      case "waitFailed": {
        await waitFailed(step.timeoutMs ?? 30000);
        break;
      }
      case "gate": {
        await waitForFile(path.join(spec.gates, step.name), step.timeoutMs ?? 60000);
        break;
      }
      case "signal": {
        writeFileSync(path.join(spec.gates, step.name), String(Date.now()), "utf8");
        break;
      }
      case "watch": {
        await sleep(step.ms);
        break;
      }
      case "dispose": {
        if (state.session !== null) {
          try {
            await state.session.dispose();
          } catch (error) {
            emit({ t: "dispose-error", message: String(error?.message ?? error) });
          }
          state.session = null;
        }
        state.streamSubscribed = null;
        break;
      }
      case "final": {
        const snapshot = state.session?.getStoreSnapshot();
        emit({
          t: "final",
          status: snapshot === undefined ? "disposed" : snapshot.Status,
          checkpoint: snapshot === undefined ? state.checkpoint && checkpointOf(state.checkpoint) : checkpointOf(snapshot.Checkpoint),
          appliedDeltaBatches: snapshot?.AppliedDeltaBatches ?? null,
          adoptedSnapshots: snapshot?.AdoptedSnapshots ?? null,
        });
        break;
      }
      default:
        throw new Error(`unknown plan op ${step.op}`);
  }
}

try {
  await run();
  process.exit(0);
} catch (error) {
  emit({ t: "tracer-error", message: String(error?.message ?? error) });
  process.exit(1);
}
