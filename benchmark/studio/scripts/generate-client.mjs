// Generates src/api/generated/client.ts from contract/terrabase-api.contract.json.
//
// The generated client is committed so builds never depend on running codegen;
// regenerate after contract edits with: vp run generate:client
// CI-style freshness check (fails when the committed client drifted from the
// contract): vp run generate:client -- --check
//
// Plain Node, no dependencies, no global tools.

import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contractPath = resolve(studioRoot, "contract", "terrabase-api.contract.json");
const outputPath = resolve(studioRoot, "src", "api", "generated", "client.ts");
const checkMode = process.argv.includes("--check");

const contract = JSON.parse(await readFile(contractPath, "utf8"));

function pascal(name) {
  return name
    .split(/[._-]/)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("");
}

function camel(name) {
  const pascalName = pascal(name);
  return pascalName.charAt(0).toLowerCase() + pascalName.slice(1);
}

function tsTypeOf(type) {
  switch (type) {
    case "string":
      return "string";
    case "integer":
      return "number";
    case "boolean":
      return "boolean";
    case "json":
      return "Json";
    case "none":
      return "null";
    default:
      return type;
  }
}

function docComment(entry, indent) {
  const doc = entry.doc;
  if (typeof doc !== "string" || doc.length === 0) {
    return "";
  }
  const pad = " ".repeat(indent);
  const lines = doc.match(/.{1,88}(\s|$)/g) ?? [];
  return `${pad}/**\n${lines.map((line) => `${pad} * ${line.trim()}`).join("\n")}\n${pad} */\n`;
}

function emitType(name, definition) {
  switch (definition.kind) {
    case "enum": {
      const doc = docComment(definition, 0);
      const values = definition.values.map((value) => JSON.stringify(value)).join(" | ");
      return `${doc}export type ${name} = ${values};\n`;
    }
    case "union": {
      const doc = docComment(definition, 0);
      const variants = definition.variants
        .map((variant) => {
          const fields = (variant.fields ?? [])
            .map((field) => {
              const optional = field.optional === true ? "?" : "";
              return `${field.name}${optional}: ${tsTypeOf(field.type)};`;
            })
            .join(" ");
          return `{ kind: ${JSON.stringify(variant.kind)}; ${fields} }`;
        })
        .join("\n  | ");
      return `${doc}export type ${name} =\n  | ${variants};\n`;
    }
    case "object": {
      const doc = docComment(definition, 0);
      const fields = (definition.fields ?? [])
        .map((field) => {
          const fieldDoc = docComment(field, 2);
          let type;
          if (field.type === "array") {
            type = `${tsTypeOf(field.items)}[]`;
          } else {
            type = tsTypeOf(field.type);
          }
          if (field.optional === true) {
            type = `${type} | null`;
          }
          return `${fieldDoc}  ${field.name}${field.optional === true ? "?" : ""}: ${type};`;
        })
        .join("\n");
      return `${doc}export interface ${name} {\n${fields}\n}\n`;
    }
    default:
      throw new Error(`Unknown type kind ${definition.kind} for ${name}`);
  }
}

const typeNames = Object.keys(contract.types);
const typesSection = typeNames.map((name) => emitType(name, contract.types[name])).join("\n");

const knownFrameTypes = contract.subscriptions.flatMap((subscription) => subscription.frames);
const uniqueFrameTypes = [...new Set(knownFrameTypes)];

const frameTypeSection = `const KNOWN_FRAME_TYPES = [
${uniqueFrameTypes.map((type) => `  ${JSON.stringify(type)},`).join("\n")}
] as const;

export type SyncFrameType = (typeof KNOWN_FRAME_TYPES)[number];

export interface SyncFrame {
  v: number;
  type: string;
  seq: number;
  correlationId: string | null;
  causationId: string | null;
  idempotencyKey: string | null;
  digest: string;
  payload: Json;
}
`;

const operations = contract.operations
  .map((operation) => {
    const methodName = camel(operation.name);
    const requestType = operation.request.type === "none" ? null : operation.request.type;
    const responseType = operation.response.type;
    const doc = docComment({ doc: operation.doc }, 2);
    if (requestType === null) {
      return `${doc}  async ${methodName}(options: OperationOptions = {}): Promise<${responseType}> {
    return callOperation(this.#options, ${JSON.stringify(operation.name)}, null, options) as Promise<${responseType}>;
  }`;
    }
    return `${doc}  async ${methodName}(request: ${requestType}, options: OperationOptions = {}): Promise<${responseType}> {
    return callOperation(this.#options, ${JSON.stringify(operation.name)}, request as unknown as Json, options) as Promise<${responseType}>;
  }`;
  })
  .join("\n\n");

const subscription = contract.subscriptions[0];
const subscribeMethod = camel(`subscribe-${subscription.name}`);

// The DB-04 changes stream: the real C# API host's backbone subscription
// surface (/v1/streams), mirrored by the fixture. Keyed off the contract's
// streams[] entry so the path stays contract-owned; the implementation is
// a fetch-stream SSE reader because the host's local gate requires
// backend-context headers EventSource cannot send.
const changesStream = contract.streams.find((entry) => entry.name === "changes");
if (changesStream === undefined) {
  throw new Error("contract.streams carries no 'changes' stream");
}
const changesPath = changesStream.path;
const changeKinds = contract.types.StreamChangeKind.values;
const changesEvents = changesStream.events;
const subscriptionSection = `export interface ${pascal(subscription.name)}SubscriptionHandlers {
  onState?: (state: ${pascal(subscription.name)}ConnectionState, snapshot: ${pascal(subscription.name)}SubscriptionSnapshot) => void;
  onTick: (tick: TickDeltaPayload) => void;
  onReplayed?: (replayedCount: number) => void;
  onRejected?: (rejectedCount: number, reason: string) => void;
  onResync?: (reason: string) => void;
  onFatal?: (error: TerrabaseApiError) => void;
}

export interface ${pascal(subscription.name)}SubscriptionOptions {
  /**
   * Force a reconnect when no frame (including heartbeats) arrives within
   * this window. Defaults to 10000ms.
   */
  staleMs?: number;
}

export type ${pascal(subscription.name)}ConnectionState = "connecting" | "live" | "reconnecting" | "failed";

export interface ${pascal(subscription.name)}SubscriptionSnapshot {
  state: ${pascal(subscription.name)}ConnectionState;
  tickCount: number;
  replayedCount: number;
  rejectedCount: number;
  reconnectCount: number;
  resyncCount: number;
  duplicateCount: number;
  lastSeq: number | null;
  lastError: string | null;
}

export interface ${pascal(subscription.name)}Subscription {
  snapshot(): ${pascal(subscription.name)}SubscriptionSnapshot;
  close(): void;
}
`;

const changesSection = `export interface BackendContext {
  principal: string;
  tenant: string;
}

const KNOWN_CHANGE_KINDS = [
${changeKinds.map((kind) => `  ${JSON.stringify(kind)},`).join("\n")}
] as const;

export type StreamChangeKind = (typeof KNOWN_CHANGE_KINDS)[number];

const KNOWN_STREAM_EVENTS = [
${changesEvents.map((name) => `  ${JSON.stringify(name)},`).join("\n")}
] as const;

export type StreamEventName = (typeof KNOWN_STREAM_EVENTS)[number];

export interface ChangesSubscriptionHandlers {
  onState?: (state: ChangesConnectionState, snapshot: ChangesSubscriptionSnapshot) => void;
  onOpened?: (opened: StreamOpenedPayload) => void;
  onEvent?: (event: StreamChangeEvent, meta: { readonly replayed: boolean }) => void;
  onTrigger?: (trigger: StreamTriggerPayload) => void;
  onDrop?: (drop: StreamDropPayload) => void;
  onClosed?: (closed: StreamClosedPayload) => void;
  onRejected?: (rejectedCount: number, reason: string) => void;
  onFatal?: (error: TerrabaseApiError) => void;
}

export interface ChangesSubscriptionOptions {
  /**
   * The collection filter (engine-side): absent subscribes to every collection.
   */
  collections?: readonly string[];
  /**
   * An explicit inclusive replay start (checkpoint resume). Absent opens live-only.
   */
  fromSequence?: number;
  /**
   * Backend-supplied context for the host's local gate. Falls back to the
   * client options; when neither is set the request carries no context and
   * the host's gate answers unauthorized (surfaced as a fatal error).
   */
  backendContext?: BackendContext;
  /**
   * Force a reconnect when no event arrives within this window. DISABLED by
   * default: the /v1/streams surface carries no heartbeat, so an idle stream
   * is indistinguishable from a quiet one in-band — only transport errors
   * (or this opt-in watchdog) drive reconnects.
   */
  staleMs?: number;
  /**
   * The delay before a reconnect attempt. Defaults to 500ms.
   */
  reconnectDelayMs?: number;
}

export type ChangesConnectionState = "connecting" | "live" | "reconnecting" | "failed";

export interface ChangesSubscriptionSnapshot {
  state: ChangesConnectionState;
  eventCount: number;
  replayedCount: number;
  duplicateCount: number;
  rejectedCount: number;
  reconnectCount: number;
  dropCount: number;
  droppedTotal: number | null;
  lastSeq: number | null;
  checkpoint: number | null;
  lastError: string | null;
}

export interface ChangesSubscription {
  snapshot(): ChangesSubscriptionSnapshot;
  /**
   * The deliberately-slow-consumer control (the DB-04 probe): stops reading
   * the response body — the connection stays open, the host's bounded queue
   * absorbs the backlog, and a full queue sheds counted frames.
   */
  pause(): void;
  resume(): void;
  close(): void;
}

class ChangesSubscriptionImpl implements ChangesSubscription {
  readonly #clientOptions: StudioClientOptions;
  readonly #handlers: ChangesSubscriptionHandlers;
  readonly #options: ChangesSubscriptionOptions;
  readonly #staleMs: number | null;
  readonly #reconnectDelayMs: number;

  #controller: AbortController | null = null;
  #reader: ReadableStreamDefaultReader<Uint8Array> | null = null;
  #decoder: TextDecoder | null = null;
  #watchdog: ReturnType<typeof setInterval> | null = null;
  #state: ChangesConnectionState = "connecting";
  #openCount = 0;
  #eventCount = 0;
  #replayedCount = 0;
  #duplicateCount = 0;
  #rejectedCount = 0;
  #dropMarkers = 0;
  #droppedTotal: number | null = null;
  #lastSeq: number | null = null;
  #lastError: string | null = null;
  #closed = false;
  #paused = false;
  #pending: Uint8Array | null = null;

  // The resume cursor: the last sequence this consumer accepted, minus the
  // rewind a drop marker imposes. Reconnects ask from = checkpoint + 1.
  #checkpoint: number | null = null;
  // The from value this connection opened with: null when live-only, and
  // the marker that classifies the first batch of a resumed connection as
  // the replayed backlog (the host replays from exactly this point).
  #openedFrom: number | null = null;
  #firstBatchOnConnection = false;
  #lastFrameAt = Date.now();
  #carry = "";

  constructor(
    clientOptions: StudioClientOptions,
    handlers: ChangesSubscriptionHandlers,
    options: ChangesSubscriptionOptions,
  ) {
    this.#clientOptions = clientOptions;
    this.#handlers = handlers;
    this.#options = options;
    this.#staleMs = options.staleMs ?? null;
    this.#reconnectDelayMs = options.reconnectDelayMs ?? 500;
    this.#open();
    if (this.#staleMs !== null) {
      this.#watchdog = setInterval(() => this.#checkStale(), 1_000);
    }
  }

  snapshot(): ChangesSubscriptionSnapshot {
    return {
      state: this.#state,
      eventCount: this.#eventCount,
      replayedCount: this.#replayedCount,
      duplicateCount: this.#duplicateCount,
      rejectedCount: this.#rejectedCount,
      reconnectCount: Math.max(0, this.#openCount - 1),
      dropCount: this.#dropMarkers,
      droppedTotal: this.#droppedTotal,
      lastSeq: this.#lastSeq,
      checkpoint: this.#checkpoint,
      lastError: this.#lastError,
    };
  }

  pause(): void {
    this.#paused = true;
  }

  resume(): void {
    if (!this.#paused) {
      return;
    }
    this.#paused = false;
    if (this.#pending !== null) {
      const value = this.#pending;
      this.#pending = null;
      this.#lastFrameAt = Date.now();
      this.#ingest((this.#decoder as TextDecoder).decode(value, { stream: true }));
    }
    this.#pump();
  }

  close(): void {
    this.#closed = true;
    if (this.#watchdog !== null) {
      clearInterval(this.#watchdog);
      this.#watchdog = null;
    }
    this.#detach();
  }

  #backendContext(): BackendContext | null {
    return this.#options.backendContext ?? this.#clientOptions.backendContext ?? null;
  }

  async #open(): Promise<void> {
    if (this.#closed) {
      return;
    }
    this.#openCount += 1;
    this.#firstBatchOnConnection = true;
    const from = this.#checkpoint === null ? this.#options.fromSequence ?? null : this.#checkpoint + 1;
    this.#openedFrom = from;
    const query = new URLSearchParams();
    if (this.#options.collections !== undefined && this.#options.collections.length > 0) {
      query.set("collections", this.#options.collections.join(","));
    }
    if (from !== null && from !== undefined) {
      query.set("from", String(from));
    }
    const base = this.#clientOptions.baseUrl.replace(/\\/$/, "");
    const qs = query.toString();
    const url = qs.length === 0 ? base + ${JSON.stringify(changesPath)} : base + ${JSON.stringify(changesPath)} + "?" + qs;
    const headers: Record<string, string> = { accept: "text/event-stream" };
    const context = this.#backendContext();
    if (context !== null) {
      headers["x-tb-principal"] = context.principal;
      headers["x-tb-tenant"] = context.tenant;
    }
    const controller = new AbortController();
    this.#controller = controller;
    try {
      const doFetch = this.#clientOptions.fetchImpl ?? fetch;
      const response = await doFetch(url, {
        method: "GET",
        headers,
        signal: controller.signal,
        credentials: "omit",
        cache: "no-store",
      });
      if (!response.ok || response.body === null) {
        const code = response.status;
        let message = "the stream was refused before it opened";
        try {
          const body = (await response.json()) as {
            detail?: unknown;
            error?: { message?: unknown };
          };
          if (typeof body.detail === "string") {
            // The real host's ErrorDto (System.Text.Json Web defaults).
            message = body.detail;
          } else if (typeof body.error?.message === "string") {
            message = body.error.message;
          }
        } catch {
          // A non-JSON refusal keeps the generic message.
        }
        if (code === 401 || code === 403) {
          // The gate's refusals are not retryable in-band: surface them.
          this.#lastError = message;
          this.close();
          this.#setState("failed");
          this.#handlers.onFatal?.(new TerrabaseApiError("stream.refused", message, { status: code }));
          return;
        }
        this.#scheduleReconnect("the stream was refused (HTTP " + code + "): " + message);
        return;
      }
      this.#read(response.body);
    } catch (cause) {
      if (this.#closed) {
        return;
      }
      this.#scheduleReconnect("the stream connection failed: " + String(cause));
    }
  }

  #read(body: ReadableStream<Uint8Array>): void {
    this.#reader = body.getReader();
    this.#decoder ??= new TextDecoder();
    this.#pump();
  }

  #pump(): void {
    const reader = this.#reader;
    if (reader === null || this.#closed) {
      return;
    }
    void reader
      .read()
      .then(({ done, value }) => {
        if (done) {
          if (!this.#closed) {
            this.#scheduleReconnect("the stream ended");
          }
          return;
        }
        if (this.#paused) {
          // A read that resolved while paused is stashed undelivered: the
          // deliberately-slow consumer processes nothing until resume().
          this.#pending = value;
          return;
        }
        this.#lastFrameAt = Date.now();
        this.#ingest((this.#decoder as TextDecoder).decode(value, { stream: true }));
        if (!this.#paused && !this.#closed) {
          this.#pump();
        }
      })
      .catch((cause: unknown) => {
        if (!this.#closed) {
          this.#scheduleReconnect("the stream read failed: " + String(cause));
        }
      });
  }

  #ingest(chunk: string): void {
    const text = this.#carry + chunk;
    const parts = text.split("\\n\\n");
    this.#carry = parts.pop() ?? "";
    for (const block of parts) {
      if (block.trim().length === 0) {
        continue;
      }
      let id: number | null = null;
      let name: string | null = null;
      let data = "";
      for (const line of block.split("\\n")) {
        if (line.startsWith("id:")) {
          const parsed = Number.parseInt(line.slice(3).trim(), 10);
          id = Number.isFinite(parsed) ? parsed : null;
        } else if (line.startsWith("event:")) {
          name = line.slice(6).trim();
        } else if (line.startsWith("data:")) {
          data += line.slice(5).trim();
        }
      }
      if (name === null) {
        continue;
      }
      this.#dispatch(name, data);
      void id;
    }
  }

  #dispatch(name: string, data: string): void {
    if (!(KNOWN_STREAM_EVENTS as readonly string[]).includes(name)) {
      this.#reject("unknown event " + JSON.stringify(name));
      return;
    }
    let payload: unknown;
    try {
      payload = JSON.parse(data);
    } catch {
      this.#reject("event " + name + " carries invalid JSON");
      return;
    }
    if (typeof payload !== "object" || payload === null) {
      this.#reject("event " + name + " is not an object");
      return;
    }
    switch (name as StreamEventName) {
      case "opened": {
        this.#setState("live");
        this.#handlers.onOpened?.(payload as unknown as StreamOpenedPayload);
        return;
      }
      case "batch": {
        this.#handleBatch(payload as unknown as StreamBatchPayload);
        return;
      }
      case "trigger": {
        const trigger = payload as unknown as StreamTriggerPayload;
        if (typeof trigger.sequence === "number" && trigger.sequence > (this.#lastSeq ?? 0)) {
          this.#lastSeq = trigger.sequence;
        }
        this.#handlers.onTrigger?.(trigger);
        return;
      }
      case "drop": {
        const drop = payload as unknown as StreamDropPayload;
        if (typeof drop.resumeFrom !== "number" || !Number.isFinite(drop.resumeFrom)) {
          this.#reject("drop event carries no finite resume boundary");
          return;
        }
        this.#dropMarkers += 1;
        if (typeof drop.dropped === "number") {
          this.#droppedTotal = drop.dropped;
        }
        this.#lastError = null;
        // Rewind the resume cursor to the dropped region's boundary: the
        // next reconnect re-delivers the missed range, never skips it.
        if (this.#checkpoint === null || drop.resumeFrom - 1 < this.#checkpoint) {
          this.#checkpoint = drop.resumeFrom - 1;
        }
        this.#handlers.onDrop?.(drop);
        return;
      }
      case "closed": {
        const closed = payload as unknown as StreamClosedPayload;
        this.#handlers.onClosed?.(closed);
        if (closed.reason === "engine-generation-lost") {
          if (typeof closed.resumeFrom === "number") {
            this.#checkpoint = closed.resumeFrom - 1;
          }
          this.#scheduleReconnect("the engine generation was lost");
          return;
        }
        this.#lastError = "the stream closed: " + String(closed.reason);
        this.close();
        this.#setState("failed");
        return;
      }
      default: {
        this.#reject("unhandled event " + JSON.stringify(name));
      }
    }
  }

  #handleBatch(batch: StreamBatchPayload): void {
    if (!Array.isArray(batch.events)) {
      this.#reject("batch carries no events array");
      return;
    }
    const replayedBatch = this.#firstBatchOnConnection && this.#openedFrom !== null;
    this.#firstBatchOnConnection = false;
    for (const raw of batch.events) {
      const record = raw as Record<string, unknown>;
      if (
        typeof record.sequence !== "number" ||
        !Number.isFinite(record.sequence) ||
        typeof record.operationId !== "string" ||
        typeof record.kind !== "string" ||
        !(KNOWN_CHANGE_KINDS as readonly string[]).includes(record.kind)
      ) {
        this.#reject("batch carries an invalid event (sequence, operationId, or closed kind)");
        continue;
      }
      if (this.#lastSeq !== null && record.sequence <= this.#lastSeq) {
        // At-least-once identity: a re-delivered sequence is counted, never
        // re-surfaced as a fresh change.
        this.#duplicateCount += 1;
        continue;
      }
      this.#lastSeq = record.sequence;
      this.#eventCount += 1;
      if (replayedBatch) {
        this.#replayedCount += 1;
      }
      this.#handlers.onEvent?.(raw, { replayed: replayedBatch });
    }
    if (typeof batch.nextSequence === "number" && batch.nextSequence > 0) {
      // The batch cursor is the authoritative checkpoint (not the SSE id
      // line, which a drop marker deliberately rewinds).
      const cursor = batch.nextSequence - 1;
      if (this.#checkpoint === null || cursor >= this.#checkpoint) {
        this.#checkpoint = cursor;
      }
    }
  }

  #setState(state: ChangesConnectionState): void {
    if (this.#state === state) {
      return;
    }
    this.#state = state;
    this.#handlers.onState?.(state, this.snapshot());
  }

  #reject(reason: string): void {
    this.#rejectedCount += 1;
    this.#lastError = reason;
    this.#handlers.onRejected?.(this.#rejectedCount, reason);
  }

  #scheduleReconnect(reason: string): void {
    if (this.#closed || this.#state === "failed") {
      return;
    }
    this.#setState("reconnecting");
    this.#lastError = reason;
    this.#detach();
    setTimeout(() => {
      void this.#open();
    }, this.#reconnectDelayMs);
  }

  #detach(): void {
    if (this.#controller !== null) {
      this.#controller.abort();
      this.#controller = null;
    }
    this.#reader = null;
    this.#carry = "";
  }

  #checkStale(): void {
    if (this.#closed || this.#state === "failed" || this.#staleMs === null) {
      return;
    }
    if (Date.now() - this.#lastFrameAt > this.#staleMs) {
      this.#lastFrameAt = Date.now();
      this.#scheduleReconnect("no events within the staleness window; forcing reconnect");
    }
  }
}
`;

const file = `// GENERATED FILE — DO NOT EDIT BY HAND.
//
// Source of truth: contract/terrabase-api.contract.json
// Regenerate with: vp run generate:client
//
// Contract: ${contract.contract} (${contract.status}) — the pre-landing shape of
// the SE-08 C# API host surface, authored from the SE-04 frame vocabulary and
// the engine PublicOperation set in src/operations.rs. Fail closed: unknown
// envelope versions, unknown frame types, correlation mismatches, digest
// mismatches, and non-monotonic delta sequence numbers are rejected, never
// silently accepted. No credentials exist on this surface (local operator
// tool, charter PD-2026-08-26-11); the host-enforced operator session is
// honored only through the typed session.unauthenticated error and its
// loginUrl redirect.

export const TERRABASE_API_CONTRACT_NAME = ${JSON.stringify(contract.contract)};

export const TERRABASE_API_CONTRACT_VERSION = ${contract.envelope.version};

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

${typesSection}
${frameTypeSection}
${subscriptionSection}
${changesSection}
export class TerrabaseApiError extends Error {
  readonly code: string;
  readonly retryable: boolean;
  readonly status: number;
  readonly correlationId: string | null;
  readonly loginUrl: string | null;

  constructor(
    code: string,
    message: string,
    options: { retryable?: boolean; status?: number; correlationId?: string | null; loginUrl?: string | null } = {},
  ) {
    super(message);
    this.name = "TerrabaseApiError";
    this.code = code;
    this.retryable = options.retryable ?? false;
    this.status = options.status ?? 0;
    this.correlationId = options.correlationId ?? null;
    this.loginUrl = options.loginUrl ?? null;
  }
}

function canonicalJson(value: Json): string {
  if (value === null || typeof value === "number" || typeof value === "boolean" || typeof value === "string") {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return \`[\${value.map((item) => canonicalJson(item)).join(",")}]\`;
  }
  const keys = Object.keys(value).sort();
  return \`{\${keys.map((key) => \`\${JSON.stringify(key)}:\${canonicalJson(value[key])}\`).join(",")}}\`;
}

async function digestOf(payload: Json): Promise<string> {
  const bytes = new TextEncoder().encode(canonicalJson(payload));
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  const hex = Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
  return \`sha256:\${hex}\`;
}

export interface StudioClientOptions {
  baseUrl: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  backendContext?: BackendContext;
}

export interface OperationOptions {
  idempotencyKey?: string;
  causationId?: string;
  timeoutMs?: number;
}

async function callOperation(
  clientOptions: StudioClientOptions,
  op: string,
  request: Json | null,
  options: OperationOptions,
): Promise<unknown> {
  const correlationId = crypto.randomUUID();
  const envelope = {
    v: TERRABASE_API_CONTRACT_VERSION,
    op,
    correlationId,
    causationId: options.causationId ?? null,
    idempotencyKey: options.idempotencyKey ?? null,
    request: request ?? {},
  };
  const doFetch = clientOptions.fetchImpl ?? fetch;
  const controller = new AbortController();
  const timeoutMs = options.timeoutMs ?? clientOptions.timeoutMs ?? 8000;
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await doFetch(\`\${clientOptions.baseUrl.replace(/\\/$/, "")}/api/v2/operations/\${op}\`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(envelope),
      signal: controller.signal,
      credentials: "omit",
    });
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      throw new TerrabaseApiError("protocol.body-invalid", \`Response for \${op} is not JSON.\`, {
        status: response.status,
        correlationId,
      });
    }
    if (typeof body !== "object" || body === null || Array.isArray(body)) {
      throw new TerrabaseApiError("protocol.envelope-invalid", \`Response for \${op} is not an envelope object.\`, {
        status: response.status,
        correlationId,
      });
    }
    const record = body as Record<string, unknown>;
    if (record.v !== TERRABASE_API_CONTRACT_VERSION) {
      throw new TerrabaseApiError(
        "protocol.version-unsupported",
        \`Response for \${op} carries envelope version \${JSON.stringify(record.v)}.\`,
        { status: response.status, correlationId },
      );
    }
    if (record.correlationId !== correlationId) {
      throw new TerrabaseApiError(
        "protocol.correlation-mismatch",
        \`Response for \${op} did not echo the request correlationId.\`,
        { status: response.status, correlationId },
      );
    }
    if (record.error !== undefined && record.error !== null) {
      const error = record.error as { code?: unknown; message?: unknown; retryable?: unknown; loginUrl?: unknown };
      throw new TerrabaseApiError(
        typeof error.code === "string" ? error.code : "error.code-missing",
        typeof error.message === "string" ? error.message : "The host returned an untyped error.",
        {
          retryable: error.retryable === true,
          status: response.status,
          correlationId,
          loginUrl: typeof error.loginUrl === "string" ? error.loginUrl : null,
        },
      );
    }
    if (response.status < 200 || response.status >= 300) {
      throw new TerrabaseApiError("protocol.status-invalid", \`Unexpected HTTP \${response.status} for \${op}.\`, {
        status: response.status,
        correlationId,
      });
    }
    return record.result;
  } catch (cause) {
    if (cause instanceof TerrabaseApiError) {
      throw cause;
    }
    const aborted = cause instanceof Error && cause.name === "AbortError";
    throw new TerrabaseApiError(
      aborted ? "transport.timeout" : "transport.unreachable",
      aborted ? \`Request for \${op} timed out after \${timeoutMs}ms.\` : \`Request for \${op} failed: \${String(cause)}\`,
      { retryable: true, correlationId },
    );
  } finally {
    clearTimeout(timeout);
  }
}

class ${pascal(subscription.name)}SubscriptionImpl implements ${pascal(subscription.name)}Subscription {
  readonly #clientOptions: StudioClientOptions;
  readonly #handlers: ${pascal(subscription.name)}SubscriptionHandlers;
  readonly #staleMs: number;
  readonly #path = ${JSON.stringify(subscription.path)};

  #source: EventSource | null = null;
  #watchdog: ReturnType<typeof setInterval> | null = null;
  #queue: Promise<void> = Promise.resolve();
  #state: ${pascal(subscription.name)}ConnectionState = "connecting";
  #openCount = 0;
  #connectionLosses = 0;
  #tickCount = 0;
  #replayedCount = 0;
  #rejectedCount = 0;
  #resyncCount = 0;
  #duplicateCount = 0;
  #lastSeq: number | null = null;
  #lastError: string | null = null;
  #lastFrameAt = Date.now();
  #closed = false;

  // Resume identity, reconstructed from the frames themselves (the SSE id
  // line is not exposed to script): session id from session.opened, plus the
  // seq of the last frame accepted on this subscription.
  #sessionId: string | null = null;
  #lastFrameSeq: number | null = null;
  // Delta-only monotonicity: control frames on a resumed connection carry
  // fresh seq identities past the replayed backlog and must not advance it.
  #lastDeltaSeq: number | null = null;
  // Set from subscription.established.lastSeq on a reconnect: replayed
  // deltas carry seq <= this high-water mark, live deltas above it.
  #replayHighWater: number | null = null;

  constructor(
    clientOptions: StudioClientOptions,
    handlers: ${pascal(subscription.name)}SubscriptionHandlers,
    options: ${pascal(subscription.name)}SubscriptionOptions,
  ) {
    this.#clientOptions = clientOptions;
    this.#handlers = handlers;
    this.#staleMs = options.staleMs ?? 10_000;
    this.#open();
    this.#watchdog = setInterval(() => this.#checkStale(), 1_000);
  }

  snapshot(): ${pascal(subscription.name)}SubscriptionSnapshot {
    return {
      state: this.#state,
      tickCount: this.#tickCount,
      replayedCount: this.#replayedCount,
      rejectedCount: this.#rejectedCount,
      reconnectCount: Math.max(0, this.#openCount - 1, this.#connectionLosses),
      resyncCount: this.#resyncCount,
      duplicateCount: this.#duplicateCount,
      lastSeq: this.#lastSeq,
      lastError: this.#lastError,
    };
  }

  close(): void {
    this.#closed = true;
    if (this.#watchdog !== null) {
      clearInterval(this.#watchdog);
      this.#watchdog = null;
    }
    this.#detach();
  }

  #checkpoint(): string | null {
    if (this.#sessionId === null || this.#lastFrameSeq === null) {
      return null;
    }
    return \`\${this.#sessionId}:\${this.#lastFrameSeq}\`;
  }

  #open(): void {
    if (this.#closed) {
      return;
    }
    this.#openCount += 1;
    // The browser's own reconnect path reuses the EventSource and re-sends
    // Last-Event-ID automatically; a source this client had to recreate
    // carries the checkpoint as a query parameter instead.
    const checkpoint = this.#checkpoint();
    const url =
      checkpoint === null
        ? \`\${this.#clientOptions.baseUrl.replace(/\\/$/, "")}\${this.#path}\`
        : \`\${this.#clientOptions.baseUrl.replace(/\\/$/, "")}\${this.#path}?lastEventId=\${encodeURIComponent(checkpoint)}\`;
    const source = new EventSource(url, { withCredentials: false });
    this.#source = source;
    source.onopen = () => {
      this.#lastFrameAt = Date.now();
    };
    source.onerror = () => {
      if (this.#closed) {
        return;
      }
      // The browser's automatic retry reuses the source (no manual open), so
      // connection losses are counted here rather than from #openCount alone.
      if (this.#state !== "reconnecting") {
        this.#connectionLosses += 1;
      }
      this.#setState("reconnecting");
      this.#lastError = "connection lost; resuming from checkpoint";
      if (source.readyState === EventSource.CLOSED) {
        this.#detach();
        setTimeout(() => this.#open(), 1_000);
      }
      // readyState CONNECTING: the browser is retrying this same source and
      // will re-send Last-Event-ID itself — leave it alone.
    };
    source.onmessage = (event: MessageEvent<string>) => {
      this.#lastFrameAt = Date.now();
      // Serialize async digest validation so frame order is preserved.
      this.#queue = this.#queue
        .then(() => this.#processFrame(event.data))
        .catch(() => {
          // #processFrame records its own failures; never let one reject the chain.
        });
    };
  }

  #detach(): void {
    if (this.#source !== null) {
      this.#source.close();
      this.#source.onopen = null;
      this.#source.onerror = null;
      this.#source.onmessage = null;
      this.#source = null;
    }
  }

  #setState(state: ${pascal(subscription.name)}ConnectionState): void {
    if (this.#state === state) {
      return;
    }
    this.#state = state;
    this.#handlers.onState?.(state, this.snapshot());
  }

  #reject(reason: string): void {
    this.#rejectedCount += 1;
    this.#lastError = reason;
    this.#handlers.onRejected?.(this.#rejectedCount, reason);
  }

  async #processFrame(raw: string): Promise<void> {
    let frame: unknown;
    try {
      frame = JSON.parse(raw);
    } catch {
      this.#reject("frame is not JSON");
      return;
    }
    if (typeof frame !== "object" || frame === null || Array.isArray(frame)) {
      this.#reject("frame is not an object");
      return;
    }
    const record = frame as Record<string, unknown>;
    if (record.v !== TERRABASE_API_CONTRACT_VERSION) {
      this.#reject(\`frame envelope version \${JSON.stringify(record.v)} is unsupported\`);
      return;
    }
    if (typeof record.type !== "string" || !(KNOWN_FRAME_TYPES as readonly string[]).includes(record.type)) {
      this.#reject(\`unknown frame type \${JSON.stringify(record.type)}\`);
      return;
    }
    if (typeof record.seq !== "number" || !Number.isFinite(record.seq)) {
      this.#reject("frame seq is not a finite number");
      return;
    }
    if (typeof record.digest !== "string") {
      this.#reject("frame digest is missing");
      return;
    }
    const payload = (record.payload ?? null) as Json;
    const digest = await digestOf(payload);
    if (digest !== record.digest) {
      this.#reject("frame digest mismatch");
      return;
    }

    switch (record.type as SyncFrameType) {
      case "session.opened": {
        const opened = payload as unknown as SessionOpenedPayload;
        if (typeof opened.sessionId === "string") {
          this.#sessionId = opened.sessionId;
        }
        this.#lastFrameSeq = record.seq;
        return;
      }
      case "subscription.established": {
        const established = payload as unknown as SubscriptionEstablishedPayload;
        // Any established frame after deltas were already consumed marks a
        // resume (the browser's automatic reconnect keeps #openCount at 1,
        // so prior history — not the open count — is the signal).
        if (this.#lastDeltaSeq !== null && typeof established.lastSeq === "number") {
          this.#replayHighWater = established.lastSeq;
        }
        this.#lastFrameSeq = record.seq;
        this.#setState("live");
        return;
      }
      case "delta": {
        const delta = payload as unknown as TickDeltaPayload;
        if (delta.seq !== record.seq) {
          this.#reject("delta payload seq does not match frame seq");
          return;
        }
        if (this.#lastDeltaSeq !== null && record.seq <= this.#lastDeltaSeq) {
          // At-least-once identity: replayed duplicates are skipped and
          // counted, never surfaced as fresh ticks.
          this.#duplicateCount += 1;
          this.#lastFrameSeq = record.seq;
          return;
        }
        this.#lastDeltaSeq = record.seq;
        this.#lastFrameSeq = record.seq;
        this.#lastSeq = record.seq;
        this.#tickCount += 1;
        if (this.#replayHighWater !== null) {
          if (record.seq <= this.#replayHighWater) {
            this.#replayedCount += 1;
          } else {
            // First live delta past the replayed backlog.
            const replayed = this.#replayedCount;
            this.#replayHighWater = null;
            this.#handlers.onReplayed?.(replayed);
          }
        }
        this.#handlers.onTick(delta);
        return;
      }
      case "heartbeat": {
        this.#lastFrameSeq = record.seq;
        return;
      }
      case "resync.required": {
        const resync = payload as unknown as ResyncRequiredPayload;
        this.#resyncCount += 1;
        this.#lastError = \`resync required: \${resync.reason}\`;
        this.#handlers.onResync?.(resync.reason);
        // The checkpoint is no longer resumable: drop all resume identity
        // and re-open a fresh session (never re-present the stale checkpoint).
        this.#sessionId = null;
        this.#lastFrameSeq = null;
        this.#lastDeltaSeq = null;
        this.#replayHighWater = null;
        this.#detach();
        this.#open();
        return;
      }
      case "fatal.error": {
        const failure = payload as unknown as FatalErrorPayload;
        this.#lastError = failure.message;
        this.close();
        this.#setState("failed");
        this.#handlers.onFatal?.(
          new TerrabaseApiError(failure.code, failure.message, { retryable: false }),
        );
        return;
      }
      default: {
        this.#reject(\`unhandled frame type \${JSON.stringify(record.type)}\`);
      }
    }
  }

  #checkStale(): void {
    if (this.#closed || this.#state === "failed") {
      return;
    }
    if (Date.now() - this.#lastFrameAt > this.#staleMs) {
      this.#lastFrameAt = Date.now();
      this.#setState("reconnecting");
      this.#lastError = "no frames within the staleness window; forcing reconnect";
      this.#detach();
      this.#open();
    }
  }
}

export class TerrabaseStudioClient {
  readonly #options: StudioClientOptions;

  constructor(options: StudioClientOptions) {
    this.#options = options;
  }

  get baseUrl(): string {
    return this.#options.baseUrl;
  }

${operations}

  ${docComment({ doc: subscription.doc }, 2)}  ${subscribeMethod}(
    handlers: ${pascal(subscription.name)}SubscriptionHandlers,
    options: ${pascal(subscription.name)}SubscriptionOptions = {},
  ): ${pascal(subscription.name)}Subscription {
    return new ${pascal(subscription.name)}SubscriptionImpl(this.#options, handlers, options);
  }

  /**
   * The backbone changes stream (DB-04): the C# API host's /v1/streams SSE
   * surface over the SE-03 event backbone — named events (opened, batch,
   * trigger, closed, drop), checkpoint-resumed reconnect, at-least-once
   * sequence identities deduplicated client-side, host-shed drops surfaced
   * in-band, and every kind of the closed selector vocabulary rendered.
   */
  subscribeChanges(
    handlers: ChangesSubscriptionHandlers,
    options: ChangesSubscriptionOptions = {},
  ): ChangesSubscription {
    return new ChangesSubscriptionImpl(this.#options, handlers, options);
  }
}
`;

if (checkMode) {
  const current = await readFile(outputPath, "utf8").catch(() => null);
  if (current !== file) {
    console.error(
      `[generate-client] ${outputPath} is out of sync with ${contractPath}; run: vp run generate:client`,
    );
    process.exit(1);
  }
  console.log("[generate-client] client.ts is in sync with the contract");
} else {
  await mkdir(dirname(outputPath), { recursive: true });
  await writeFile(outputPath, file, "utf8");
  console.log(`[generate-client] wrote ${outputPath}`);
}
console.log(
  `[generate-client] contract ${contract.contract}: ${contract.operations.length} operations, ${contract.subscriptions.length} subscriptions, ${typeNames.length} types`,
);
