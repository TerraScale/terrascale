import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  terrascaleSyncV2MsgpackManifestSha256,
  terrascaleSyncV2MsgpackMessageDirections,
  terrascaleSyncV2MsgpackProtocol,
  terrascaleSyncV2MsgpackSupportedFeatures,
  validateTerrascaleSyncV2MsgpackMessage,
  type TerrascaleSyncV2MsgpackEnvelope,
  type TerrascaleSyncV2MsgpackMessage,
  type TerrascaleSyncV2MsgpackProjectedField,
  type TerrascaleSyncV2MsgpackResultOperation,
  type TerrascaleSyncV2MsgpackSnapshot,
} from "../../msgpack/generated/terrascale-sync-v2-msgpack.js";
import {
  SyncV2ResultViewStore,
  SyncV2ViewCheckpoint,
  type SyncV2ResultDelta,
  type SyncV2ResultItem,
  type SyncV2ResultOperation,
  type SyncV2ResultSnapshot,
  type SyncV2ResultResetReason,
} from "./result-view.js";

export { SyncV2ViewCheckpoint };
export type { SyncV2ResultDelta, SyncV2ResultItem, SyncV2ResultOperation, SyncV2ResultSnapshot };

export const syncV2Protocol = terrascaleSyncV2MsgpackProtocol;
export const syncV2ManifestSha256 = terrascaleSyncV2MsgpackManifestSha256;

export type SyncV2Envelope = TerrascaleSyncV2MsgpackEnvelope;
export type SyncV2Message = TerrascaleSyncV2MsgpackMessage;

export type SyncV2OpenSubscriptionOptions = {
  readonly subscription: string;
  readonly query: string;
  readonly parameters: Uint8Array;
  readonly checkpoint?: SyncV2ViewCheckpoint | Uint8Array | undefined;
};

/** The reactive transport. HTTP is the cacheable, bounded default. */
export type SyncV2Transport = "http" | "websocket";

export type SyncV2AccessToken = string | (() => string | undefined | Promise<string | undefined>);

export type SyncV2ClientOptions = {
  /** The member/API origin used for the authenticated view attach. */
  readonly apiBaseUrl?: string;
  /** The global CDN origin. Reactive delivery never uses a regional host. */
  readonly syncBaseUrl?: string;
  readonly fetch?: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  readonly accessToken?: SyncV2AccessToken;
  /** Bounded server long-poll window. The contract ceiling is 25 seconds. */
  readonly pollWindowMs?: number;
  /** Delay between reconnect attempts after a transport failure. */
  readonly reconnectDelayMs?: number;
};

export type SyncV2SubscribeOptions = SyncV2OpenSubscriptionOptions & {
  /** Database id required by the attach route. */
  readonly databaseId?: string;
  /** Alias accepted by the public attach contract. */
  readonly database?: string;
  readonly transport?: SyncV2Transport;
  readonly regionHint?: string;
  readonly signal?: AbortSignal;
  /** Do not start the background HTTP poll loop until `poll` is called. */
  readonly autoStart?: boolean;
};

export type SyncV2Reset = {
  readonly reason: SyncV2ResultResetReason;
  readonly subscription?: string;
  readonly message?: string;
};

export type SyncV2Subscription = {
  readonly id: string;
  readonly view: SyncV2ResultViewStore;
};

export type SyncV2OpenSubscription = {
  readonly message: Extract<SyncV2Message, { readonly kind: "subscribe" }>;
  readonly subscription: SyncV2Subscription;
};

/** A live HTTP subscription returned by `subscribe`. */
export type SyncV2LiveSubscription = SyncV2Subscription & {
  readonly transport: SyncV2Transport;
  readonly generation: bigint;
  readonly capability: string;
  readonly logUrl: string;
  readonly snapshotUrls: readonly string[];
  readonly reset?: SyncV2Reset | undefined;
  readonly checkpoint: () => SyncV2ViewCheckpoint | undefined;
  readonly poll: (signal?: AbortSignal) => Promise<SyncV2Envelope | undefined>;
  readonly reconnect: () => Promise<void>;
  readonly close: () => void;
};

export type SyncV2ClientSnapshot = {
  readonly online: boolean;
  readonly paused: boolean;
  readonly region: string | null;
  readonly subscriptions: number;
};

export class TerraScaleSyncV2ClientError extends Error {
  public override readonly name = "TerraScaleSyncV2ClientError";

  public constructor(
    public readonly code:
      | "invalid_state"
      | "protocol_error"
      | "unknown_subscription"
      | "remote_error"
      | "reset_required"
      | "authentication_expired"
      | "permission_denied"
      | "slow_consumer"
      | "transport",
    reason: string,
    options?: ErrorOptions,
    public readonly reset?: SyncV2Reset,
  ) {
    super(`terrascale.sync.v2 client: ${code}: ${reason}`, options);
  }
}

/**
 * The single public reactive client state machine. Network transports feed and
 * drain generated MessagePack frames; this class owns no second wire schema.
 */
export class TerraScaleSyncV2Client {
  readonly #subscriptions = new Map<string, SyncV2Subscription>();
  readonly #live = new Map<string, LiveSubscriptionState>();
  readonly #fetch: ((input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) | undefined;
  readonly #apiBaseUrl: string;
  readonly #syncBaseUrl: string;
  readonly #accessToken: SyncV2AccessToken | undefined;
  readonly #pollWindowMs: number;
  readonly #reconnectDelayMs: number;
  #online = false;
  #paused = false;
  #region: string | null = null;

  public constructor(options: SyncV2ClientOptions = {}) {
    this.#fetch = options.fetch ?? globalThis.fetch?.bind(globalThis);
    this.#apiBaseUrl = normalizeOrigin(options.apiBaseUrl ?? "https://api.terrascale.tech");
    this.#syncBaseUrl = normalizeOrigin(options.syncBaseUrl ?? "https://sync.api.terrascale.tech");
    this.#accessToken = options.accessToken;
    this.#pollWindowMs = boundedDuration(options.pollWindowMs ?? 25_000, "pollWindowMs", 25_000);
    this.#reconnectDelayMs = boundedDuration(options.reconnectDelayMs ?? 250, "reconnectDelayMs", 30_000);
  }

  public getSnapshot(): SyncV2ClientSnapshot {
    return Object.freeze({
      online: this.#online,
      paused: this.#paused,
      region: this.#region,
      subscriptions: this.#subscriptions.size,
    });
  }

  public setOnline(online: boolean): void {
    if (typeof online !== "boolean") throw new TypeError("online must be a boolean");
    this.#online = online;
    for (const subscription of this.#subscriptions.values()) {
      subscription.view.setOnline(online);
    }
  }

  /** Opens one live SQL result view and returns the generated subscribe message to send. */
  public openSubscription(options: SyncV2OpenSubscriptionOptions): SyncV2OpenSubscription {
    if (this.#subscriptions.has(options.subscription)) {
      throw new TerraScaleSyncV2ClientError(
        "invalid_state",
        `subscription '${options.subscription}' is already open`,
      );
    }
    if (this.#subscriptions.size >= 128) {
      throw new TerraScaleSyncV2ClientError("invalid_state", "the 128-subscription bound is reached");
    }
    const message = Object.freeze({
      kind: "subscribe" as const,
      subscription: options.subscription,
      query: options.query,
      parameters: new Uint8Array(options.parameters),
      checkpoint: checkpointBytes(options.checkpoint),
    });
    validateTerrascaleSyncV2MsgpackMessage(message);

    const view = new SyncV2ResultViewStore();
    view.setOnline(this.#online);
    const subscription = Object.freeze({ id: options.subscription, view });
    this.#subscriptions.set(options.subscription, subscription);
    return Object.freeze({ message, subscription });
  }

  /** Closes one view and returns the generated unsubscribe message to send. */
  public closeSubscription(subscription: string): Extract<SyncV2Message, { readonly kind: "unsubscribe" }> {
    const current = this.#requireSubscription(subscription);
    const live = this.#live.get(subscription);
    if (live !== undefined) live.closed = true;
    this.#live.delete(subscription);
    current.view.setOnline(false);
    this.#subscriptions.delete(subscription);
    return Object.freeze({ kind: "unsubscribe", subscription });
  }

  /**
   * Attaches to the canonical shared view and starts bounded HTTP long-polling.
   * WebSocket is an explicit override and is rejected here until an injected
   * WebSocket transport is supplied by a future contract revision.
   */
  public async subscribe(options: SyncV2SubscribeOptions): Promise<SyncV2LiveSubscription> {
    const transport = options.transport ?? "http";
    if (transport === "websocket") {
      throw new TerraScaleSyncV2ClientError(
        "invalid_state",
        "WebSocket transport is opt-in and requires the direct WebSocket transport; HTTP long-polling is the default",
      );
    }
    if (this.#fetch === undefined) {
      throw new TerraScaleSyncV2ClientError("transport", "no fetch implementation is available");
    }
    const databaseId = options.databaseId ?? options.database;
    if (databaseId === undefined || databaseId.trim().length === 0) {
      throw new TerraScaleSyncV2ClientError("invalid_state", "databaseId is required for a reactive view attach");
    }
    const opened = this.openSubscription(options);
    let attached: AttachedView;
    try {
      attached = await this.#attach(databaseId, options, opened.message, options.signal);
    } catch (error) {
      this.#subscriptions.delete(options.subscription);
      throw error;
    }
    const state: LiveSubscriptionState = {
      opened,
      databaseId,
      generation: attached.generation,
      capability: attached.capability,
      logUrl: attached.logUrl,
      snapshotUrls: attached.snapshotUrls,
      closed: false,
      loop: undefined,
      reset: undefined,
    };
    this.#live.set(options.subscription, state);
    this.setOnline(true);

    try {
      if (attached.snapshot !== undefined) {
        installAttachedSnapshot(opened.subscription.view, attached.snapshot);
      } else {
        for (const snapshotUrl of attached.snapshotUrls) {
          const frame = await this.#getBytes(snapshotUrl, state, options.signal);
          if (frame === undefined) continue;
          this.receive(frame);
        }
      }
    } catch (error) {
      this.#live.delete(options.subscription);
      this.#subscriptions.delete(options.subscription);
      throw error;
    }

    const live = this.#publicLiveSubscription(state);
    if (options.autoStart !== false) state.loop = this.#pollLoop(state, options.signal);
    return live;
  }

  /** Returns the acknowledgement for the latest installed opaque checkpoint. */
  public checkpointAcknowledgement(
    subscription: string,
  ): Extract<SyncV2Message, { readonly kind: "checkpoint_ack" }> {
    const checkpoint = this.#requireSubscription(subscription).view.getCheckpoint();
    if (checkpoint === undefined) {
      throw new TerraScaleSyncV2ClientError(
        "invalid_state",
        `subscription '${subscription}' has no checkpoint to acknowledge`,
      );
    }
    return Object.freeze({
      kind: "checkpoint_ack",
      subscription,
      checkpoint: checkpoint.toBytes(),
    });
  }

  /** Encodes one generated client message for a WebSocket or HTTP fallback transport. */
  public encode(message: SyncV2Message, correlation: bigint, causation = 0n): Uint8Array {
    const direction = terrascaleSyncV2MsgpackMessageDirections[message.kind];
    if (direction === "server_to_client") {
      throw new TerraScaleSyncV2ClientError(
        "protocol_error",
        `message '${message.kind}' is server-to-client only`,
      );
    }
    if (!this.#online) {
      throw new TerraScaleSyncV2ClientError("invalid_state", "the reactive transport is offline");
    }
    if (this.#paused && message.kind !== "heartbeat") {
      throw new TerraScaleSyncV2ClientError("invalid_state", "the server paused client traffic");
    }
    return encodeTerrascaleSyncV2MsgpackEnvelope({
      features: terrascaleSyncV2MsgpackSupportedFeatures,
      correlation,
      causation,
      message,
    });
  }

  /** Decodes and atomically applies one server frame. */
  public receive(frame: Uint8Array): SyncV2Envelope {
    let envelope: SyncV2Envelope;
    try {
      envelope = decodeTerrascaleSyncV2MsgpackEnvelope(frame);
    } catch (error) {
      throw new TerraScaleSyncV2ClientError(
        "protocol_error",
        error instanceof Error ? error.message : String(error),
        { cause: error },
      );
    }
    if (terrascaleSyncV2MsgpackMessageDirections[envelope.message.kind] === "client_to_server") {
      throw new TerraScaleSyncV2ClientError(
        "protocol_error",
        `received client-to-server message '${envelope.message.kind}'`,
      );
    }

    switch (envelope.message.kind) {
      case "snapshot":
        this.#requireSubscription(envelope.message.subscription).view.bootstrap(
          resultSnapshot(envelope.message.snapshot),
        );
        break;
      case "delta": {
        const view = this.#requireSubscription(envelope.message.subscription).view;
        const outcome = view.applyDelta(resultDelta(envelope.message));
        if (outcome !== "applied" && outcome !== "duplicate") {
          throw new TerraScaleSyncV2ClientError(
            "protocol_error",
            `subscription '${envelope.message.subscription}' requires reset after ${outcome}`,
          );
        }
        break;
      }
      case "reset":
        this.#requireSubscription(envelope.message.subscription).view.reset(
          resultSnapshot(envelope.message.snapshot),
        );
        break;
      case "authorization_changed":
        this.#requireSubscription(envelope.message.subscription).view.transitionAuthorization(
          envelope.message.authorization_generation,
          envelope.message.removed,
          resultSnapshot(envelope.message.snapshot),
        );
        break;
      case "region_route_change":
        this.#region = envelope.message.region;
        this.setOnline(false);
        for (const state of this.#live.values()) state.regionMoved = true;
        break;
      case "backpressure":
        this.#paused = envelope.message.mode === "pause";
        break;
      case "error":
        this.setOnline(false);
        if (envelope.message.code === "slow_consumer") {
          throw new TerraScaleSyncV2ClientError(
            "slow_consumer",
            envelope.message.detail ?? "the server dropped this slow consumer",
            undefined,
            { reason: "backpressure", ...(envelope.message.detail === null ? {} : { message: envelope.message.detail }) },
          );
        }
        if (envelope.message.code === "authentication_expired") {
          throw new TerraScaleSyncV2ClientError(
            "remote_error",
            envelope.message.detail ?? "the reactive session expired",
          );
        }
        if (envelope.message.code === "permission_denied") {
          throw new TerraScaleSyncV2ClientError(
            "remote_error",
            envelope.message.detail ?? "the reactive view is no longer authorized",
          );
        }
        throw new TerraScaleSyncV2ClientError(
          "remote_error",
          `${envelope.message.code}${envelope.message.detail === null ? "" : `: ${envelope.message.detail}`}`,
        );
      case "heartbeat":
        break;
      default:
        throw new TerraScaleSyncV2ClientError(
          "protocol_error",
          `message '${envelope.message.kind}' is not valid from the server`,
        );
    }
    return envelope;
  }

  #requireSubscription(subscription: string): SyncV2Subscription {
    const current = this.#subscriptions.get(subscription);
    if (current === undefined) {
      throw new TerraScaleSyncV2ClientError(
        "unknown_subscription",
        `subscription '${subscription}' is not open`,
      );
    }
    return current;
  }

  async #attach(
    databaseId: string,
    options: SyncV2SubscribeOptions,
    message: Extract<SyncV2Message, { readonly kind: "subscribe" }>,
    signal?: AbortSignal,
  ): Promise<AttachedView> {
    const headers = await this.#headers("application/json", undefined);
    headers.set("Content-Type", "application/json");
    if (options.regionHint !== undefined) headers.set("X-TerraScale-Region-Hint", options.regionHint);
    const response = await this.#request(
      `${this.#apiBaseUrl}/api/v1/reactive/views`,
      {
        method: "POST",
        headers,
        body: JSON.stringify({
          databaseId,
          subscription: message.subscription,
          statement: message.query,
          parameters: bytesToBase64(message.parameters),
          checkpoint: message.checkpoint === null ? null : bytesToBase64(message.checkpoint),
          transport: "http",
        }),
        ...(signal === undefined ? {} : { signal }),
      },
      "reactive view attach",
    );
    if (!response.ok) throw await this.#httpError(response, "reactive view attach");
    let value: unknown;
    try {
      value = await response.json();
    } catch (error) {
      throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach was not valid JSON", { cause: error });
    }
    return decodeAttachedView(value, this.#syncBaseUrl, message.subscription);
  }

  async #getBytes(url: string, state: LiveSubscriptionState, signal?: AbortSignal): Promise<Uint8Array | undefined> {
    const response = await this.#request(
      url,
      {
        method: "GET",
        headers: await this.#headers("application/msgpack", state.capability),
        ...(signal === undefined ? {} : { signal }),
      },
      "reactive snapshot/log",
    );
    if (response.status === 204 || response.status === 304) return undefined;
    if (!response.ok) throw await this.#httpError(response, "reactive snapshot/log");
    const bytes = new Uint8Array(await response.arrayBuffer());
    return bytes.length === 0 ? undefined : bytes;
  }

  async #pollOnce(state: LiveSubscriptionState, signal?: AbortSignal): Promise<SyncV2Envelope | undefined> {
    while (this.#paused && !state.closed) await delay(this.#reconnectDelayMs, signal);
    if (state.closed) return undefined;
    const revision = state.opened.subscription.view.getRevision();
    const url = new URL(state.logUrl);
    url.searchParams.set("offset", revision.toString(10));
    const timeout = new AbortController();
    const abort = () => timeout.abort(signal?.reason);
    if (signal !== undefined) {
      if (signal.aborted) throw signal.reason;
      signal.addEventListener("abort", abort, { once: true });
    }
    const timer = globalThis.setTimeout(() => timeout.abort(), this.#pollWindowMs);
    try {
      let response: Response;
      try {
        response = await this.#request(
          url,
          {
            method: "GET",
            headers: await this.#headers("application/msgpack", state.capability),
            signal: timeout.signal,
          },
          "reactive log poll",
        );
      } catch (error) {
        // A server-side 25 second long-poll timeout is an empty response. It
        // must not trigger a new attach or consume the reconnect budget.
        if (error instanceof TerraScaleSyncV2ClientError &&
          error.code === "transport" && isAbortError(error.cause) &&
          !signal?.aborted) return undefined;
        throw error;
      }
      if (response.status === 204 || response.status === 304) return undefined;
      if (!response.ok) throw await this.#httpError(response, "reactive log poll");
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (bytes.length === 0) return undefined;
      let envelope: SyncV2Envelope;
      try {
        envelope = this.receive(bytes);
      } catch (error) {
        if (error instanceof TerraScaleSyncV2ClientError && error.reset !== undefined) state.reset = error.reset;
        throw error;
      }
      if (envelope.message.kind === "reset") {
        state.reset = { reason: normalizeResetReason(envelope.message.reason), subscription: state.opened.subscription.id };
      }
      return envelope;
    } finally {
      globalThis.clearTimeout(timer);
      signal?.removeEventListener("abort", abort);
    }
  }

  async #pollLoop(state: LiveSubscriptionState, signal?: AbortSignal): Promise<void> {
    while (!state.closed && !signal?.aborted) {
      try {
        const envelope = await this.#pollOnce(state, signal);
        if (envelope?.message.kind === "reset") {
          state.reset = { reason: normalizeResetReason(envelope.message.reason), subscription: state.opened.subscription.id };
        }
        if (envelope === undefined) await delay(100, signal);
        if (state.regionMoved) {
          state.regionMoved = false;
          await this.#reconnect(state, signal);
        }
      } catch (error) {
        if (state.closed || signal?.aborted) return;
        if (error instanceof TerraScaleSyncV2ClientError && error.reset !== undefined) {
          state.reset = error.reset;
        }
        if (error instanceof TerraScaleSyncV2ClientError &&
          (error.code === "authentication_expired" || error.code === "permission_denied" || error.code === "slow_consumer" || error.code === "remote_error")) {
          if (error.reset !== undefined) state.reset = error.reset;
          return;
        }
        this.setOnline(false);
        await delay(this.#reconnectDelayMs, signal);
        if (!state.closed && !signal?.aborted) {
          try {
            await this.#reconnect(state, signal);
          } catch {
            continue;
          }
        }
      }
    }
  }

  async #reconnect(state: LiveSubscriptionState, signal?: AbortSignal): Promise<void> {
    if (state.closed || this.#fetch === undefined) return;
    const current = state.opened.subscription.view.getCheckpoint();
    const message = state.opened.message;
    const options: SyncV2SubscribeOptions = {
      subscription: message.subscription,
      query: message.query,
      parameters: new Uint8Array(message.parameters),
      checkpoint: current,
      databaseId: state.databaseId,
      autoStart: false,
    };
    const attached = await this.#attach(state.databaseId, options, {
      ...message,
      checkpoint: current?.toBytes() ?? null,
    }, signal);
    state.generation = attached.generation;
    state.capability = attached.capability;
    state.logUrl = attached.logUrl;
    state.snapshotUrls = attached.snapshotUrls;
    if (attached.snapshot !== undefined) {
      installAttachedSnapshot(state.opened.subscription.view, attached.snapshot);
    } else {
      for (const snapshotUrl of attached.snapshotUrls) {
        const bytes = await this.#getBytes(snapshotUrl, state, signal);
        if (bytes !== undefined) this.receive(bytes);
      }
    }
    this.setOnline(true);
  }

  #publicLiveSubscription(state: LiveSubscriptionState): SyncV2LiveSubscription {
    return {
      id: state.opened.subscription.id,
      view: state.opened.subscription.view,
      transport: "http",
      get generation() { return state.generation; },
      get capability() { return state.capability; },
      get logUrl() { return state.logUrl; },
      get snapshotUrls() { return Object.freeze([...state.snapshotUrls]); },
      get reset() { return state.reset; },
      checkpoint: () => state.opened.subscription.view.getCheckpoint(),
      poll: signal => this.#pollOnce(state, signal),
      reconnect: () => this.#reconnect(state),
      close: () => {
        state.closed = true;
        this.#live.delete(state.opened.subscription.id);
        this.setOnline(false);
      },
    };
  }

  async #headers(accept: string, capability: string | undefined): Promise<Headers> {
    const headers = new Headers({ accept });
    if (capability !== undefined) headers.set("X-TerraScale-View-Capability", capability);
    const token = await resolveToken(this.#accessToken);
    if (token !== undefined && token.length > 0) headers.set("Authorization", token.startsWith("Bearer ") ? token : `Bearer ${token}`);
    return headers;
  }

  async #request(input: RequestInfo | URL, init: RequestInit, operation: string): Promise<Response> {
    if (this.#fetch === undefined) throw new TerraScaleSyncV2ClientError("transport", `no fetch implementation is available for ${operation}`);
    try {
      return await this.#fetch(input, init);
    } catch (error) {
      throw new TerraScaleSyncV2ClientError("transport", `${operation} failed`, { cause: error });
    }
  }

  async #httpError(response: Response, operation: string): Promise<TerraScaleSyncV2ClientError> {
    let detail = `${operation} returned HTTP ${response.status}`;
    let reset: SyncV2Reset | undefined;
    try {
      const body: unknown = await response.clone().json();
      if (isRecord(body)) {
        const error = isRecord(body.error) ? body.error : body;
        if (typeof error.message === "string") detail = error.message;
        if (typeof error.reason === "string") reset = { reason: normalizeResetReason(error.reason), message: detail };
        if (typeof error.code === "string" && error.code === "checkpoint_refused") {
          reset = { reason: "checkpoint-expired", message: detail };
        }
      }
    } catch {
      // Status and operation are sufficient when an error body is unavailable.
    }
    if (reset === undefined && response.status === 410) {
      reset = { reason: "retention-advanced", message: detail };
    }
    if (reset !== undefined) return new TerraScaleSyncV2ClientError("reset_required", detail, undefined, reset);
    if (response.status === 401) return new TerraScaleSyncV2ClientError("authentication_expired", detail);
    if (response.status === 403) return new TerraScaleSyncV2ClientError("permission_denied", detail);
    return new TerraScaleSyncV2ClientError("transport", detail);
  }
}

type AttachedView = {
  readonly generation: bigint;
  readonly checkpoint: Uint8Array | undefined;
  readonly capability: string;
  readonly snapshotUrls: readonly string[];
  readonly logUrl: string;
  readonly snapshot: TerrascaleSyncV2MsgpackSnapshot | undefined;
};

type LiveSubscriptionState = {
  readonly opened: SyncV2OpenSubscription;
  readonly databaseId: string;
  generation: bigint;
  capability: string;
  snapshotUrls: readonly string[];
  logUrl: string;
  closed: boolean;
  regionMoved?: boolean;
  reset: SyncV2Reset | undefined;
  loop: Promise<void> | undefined;
};

function checkpointBytes(
  checkpoint: SyncV2ViewCheckpoint | Uint8Array | undefined,
): Uint8Array | null {
  if (checkpoint === undefined) return null;
  return checkpoint instanceof SyncV2ViewCheckpoint
    ? checkpoint.toBytes()
    : new Uint8Array(checkpoint);
}

function resultSnapshot(snapshot: TerrascaleSyncV2MsgpackSnapshot): SyncV2ResultSnapshot {
  return {
    view_generation: snapshot.view_generation,
    authorization_generation: snapshot.authorization_generation,
    revision: snapshot.revision,
    items: snapshot.items.map(resultItem),
    digest: snapshot.digest,
    checkpoint: new SyncV2ViewCheckpoint(snapshot.checkpoint),
  };
}

function installAttachedSnapshot(view: SyncV2ResultViewStore, snapshot: TerrascaleSyncV2MsgpackSnapshot): void {
  const result = resultSnapshot(snapshot);
  if (view.getCheckpoint() === undefined) {
    view.bootstrap(result);
    return;
  }
  if (result.view_generation > view.getViewGeneration() || result.revision > view.getRevision()) {
    view.reset(result);
  }
}

function resultDelta(
  message: Extract<SyncV2Message, { readonly kind: "delta" }>,
): SyncV2ResultDelta {
  return {
    view_generation: message.view_generation,
    from_revision: message.from_revision,
    to_revision: message.to_revision,
    operations: message.operations.map(resultOperation),
    digest: message.digest,
    checkpoint: new SyncV2ViewCheckpoint(message.checkpoint),
  };
}

function resultItem(item: {
  readonly identity: string;
  readonly fields: ReadonlyArray<TerrascaleSyncV2MsgpackProjectedField>;
}): SyncV2ResultItem {
  return {
    identity: item.identity,
    value: projectedValue(item.fields),
  };
}

function resultOperation(operation: TerrascaleSyncV2MsgpackResultOperation): SyncV2ResultOperation {
  switch (operation.kind) {
    case "insert":
      return {
        kind: "Insert",
        identity: operation.identity,
        position: operation.position,
        value: projectedValue(operation.fields),
      };
    case "update":
      return {
        kind: "Update",
        identity: operation.identity,
        value: projectedValue(operation.fields),
      };
    case "patch":
      return {
        kind: "Patch",
        identity: operation.identity,
        changes: operation.changes.map(change => ({
          path: change.path,
          value: change.value === null ? null : new Uint8Array(change.value),
        })),
      };
    case "remove":
      return { kind: "Remove", identity: operation.identity };
    case "move":
      return { kind: "Move", identity: operation.identity, position: operation.position };
  }
}

function projectedValue(
  fields: ReadonlyArray<TerrascaleSyncV2MsgpackProjectedField>,
): Readonly<Record<string, Uint8Array>> {
  const value: Record<string, Uint8Array> = Object.create(null) as Record<string, Uint8Array>;
  for (const field of fields) {
    if (Object.hasOwn(value, field.path)) {
      throw new TerraScaleSyncV2ClientError(
        "protocol_error",
        `projected field '${field.path}' appears more than once`,
      );
    }
    value[field.path] = new Uint8Array(field.value);
  }
  return value;
}

function normalizeOrigin(origin: string): string {
  const value = origin.trim();
  if (value.length === 0) throw new TypeError("reactive origins cannot be empty");
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new TypeError("reactive origins must be absolute URLs");
  }
  if (url.username !== "" || url.password !== "") throw new TypeError("reactive origins cannot contain credentials");
  return url.toString().replace(/\/$/u, "");
}

function boundedDuration(value: number, name: string, max: number): number {
  if (!Number.isInteger(value) || value < 0 || value > max) {
    throw new TypeError(`${name} must be an integer between 0 and ${max}`);
  }
  return value;
}

async function resolveToken(token: SyncV2AccessToken | undefined): Promise<string | undefined> {
  if (token === undefined) return undefined;
  return typeof token === "function" ? await token() : token;
}

async function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  if (milliseconds === 0) return;
  await new Promise<void>((resolve, reject) => {
    const timer = globalThis.setTimeout(resolve, milliseconds);
    if (signal === undefined) return;
    const abort = () => {
      globalThis.clearTimeout(timer);
      reject(signal.reason);
    };
    if (signal.aborted) abort();
    else {
      signal.addEventListener("abort", abort, { once: true });
      globalThis.setTimeout(() => signal.removeEventListener("abort", abort), milliseconds);
    }
  });
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
  }
  return globalThis.btoa(binary);
}

function bytesFromUnknown(value: unknown, field: string): Uint8Array {
  if (value instanceof Uint8Array) return new Uint8Array(value);
  if (Array.isArray(value) && value.every(item => typeof item === "number" && Number.isInteger(item) && item >= 0 && item <= 255)) {
    return Uint8Array.from(value as number[]);
  }
  if (typeof value === "string") {
    // Attach responses use base64 for bytes. Plain strings remain accepted for
    // local fixtures and are encoded as UTF-8, without ever parsing JSON data.
    try {
      const binary = globalThis.atob(value);
      const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
      if (bytes.length > 0) return bytes;
    } catch {
      // Fall through to the opaque UTF-8 fixture form.
    }
    return new TextEncoder().encode(value);
  }
  throw new TerraScaleSyncV2ClientError("protocol_error", `attach response field '${field}' is not bytes`);
}

function bigintFromUnknown(value: unknown, field: string): bigint {
  try {
    if (typeof value === "bigint") return value;
    if (typeof value === "number" && Number.isSafeInteger(value) && value >= 0) return BigInt(value);
    if (typeof value === "string" && /^\d+$/u.test(value)) return BigInt(value);
  } catch {
    // Fall through to the typed protocol error below.
  }
  throw new TerraScaleSyncV2ClientError("protocol_error", `attach response field '${field}' is not a u64`);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function canonicalViewPath(view: string, generation: bigint, leaf: string): string {
  return `/api/v1/reactive/views/${encodeURIComponent(view)}/${generation.toString(10)}/${leaf}`;
}

function canonicalUrl(syncOrigin: string, path: string): string {
  return `${syncOrigin}${path}`;
}

function decodeAttachedView(value: unknown, syncOrigin: string, subscription: string): AttachedView {
  if (!isRecord(value)) throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response is not an object");
  const view = typeof value.viewId === "string" ? value.viewId : typeof value.view_id === "string" ? value.view_id : subscription;
  if (view.length === 0) throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has no view id");
  const generation = bigintFromUnknown(value.generation ?? value.view_generation ?? value.viewGeneration, "generation");
  const capabilityValue = value.capability ?? value.viewCapability ?? value.view_capability;
  if (typeof capabilityValue !== "string" || capabilityValue.length === 0) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has no view capability");
  }
  const logUrl = canonicalUrl(syncOrigin, canonicalViewPath(view, generation, "log"));
  const rawSnapshotUrls = value.snapshotUrls ?? value.snapshot_urls ?? value.snapshotChunkUrls ?? value.snapshot_chunk_urls;
  const snapshotUrls: string[] = [];
  if (Array.isArray(rawSnapshotUrls)) {
    for (const [index, raw] of rawSnapshotUrls.entries()) {
      if (typeof raw !== "string") throw new TerraScaleSyncV2ClientError("protocol_error", `snapshot URL ${index} is not a string`);
      const parsed = new URL(raw, syncOrigin);
      const prefix = canonicalViewPath(view, generation, "snapshot/");
      if (parsed.pathname.startsWith(prefix)) snapshotUrls.push(canonicalUrl(syncOrigin, parsed.pathname));
      else snapshotUrls.push(canonicalUrl(syncOrigin, canonicalViewPath(view, generation, `snapshot/${index}`)));
    }
  }
  const checkpointValue = value.checkpoint;
  const checkpoint = checkpointValue === undefined || checkpointValue === null ? undefined : bytesFromUnknown(checkpointValue, "checkpoint");
  const snapshot = decodeInlineSnapshot(value.snapshot, checkpoint);
  if (snapshot === undefined && snapshotUrls.length === 0 && checkpoint === undefined) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has no snapshot or snapshot URLs");
  }
  return { generation, checkpoint, capability: capabilityValue, snapshotUrls, logUrl, snapshot };
}

function decodeInlineSnapshot(value: unknown, checkpoint: Uint8Array | undefined): TerrascaleSyncV2MsgpackSnapshot | undefined {
  if (!isRecord(value)) return undefined;
  const itemsValue = value.items;
  if (!Array.isArray(itemsValue)) throw new TerraScaleSyncV2ClientError("protocol_error", "inline snapshot items are not an array");
  const items = itemsValue.map((raw, index) => {
    if (!isRecord(raw) || typeof raw.identity !== "string" || !Array.isArray(raw.fields)) {
      throw new TerraScaleSyncV2ClientError("protocol_error", `inline snapshot item ${index} is malformed`);
    }
    return {
      identity: raw.identity,
      fields: raw.fields.map((field, fieldIndex) => {
        if (!isRecord(field) || typeof field.path !== "string") throw new TerraScaleSyncV2ClientError("protocol_error", `inline snapshot field ${fieldIndex} is malformed`);
        return { path: field.path, value: bytesFromUnknown(field.value, `snapshot.items[${index}].fields[${fieldIndex}].value`) };
      }),
    };
  });
  if (typeof value.digest !== "string") throw new TerraScaleSyncV2ClientError("protocol_error", "inline snapshot digest is missing");
  return {
    view_generation: bigintFromUnknown(value.view_generation ?? value.viewGeneration, "snapshot.view_generation"),
    authorization_generation: bigintFromUnknown(value.authorization_generation ?? value.authorizationGeneration ?? 0, "snapshot.authorization_generation"),
    revision: bigintFromUnknown(value.revision, "snapshot.revision"),
    items,
    digest: value.digest,
    checkpoint: checkpoint ?? bytesFromUnknown(value.checkpoint, "snapshot.checkpoint"),
  };
}

function normalizeResetReason(value: string): SyncV2ResultResetReason {
  const normalized = value.replace(/_/gu, "-") as SyncV2ResultResetReason;
  const reasons: readonly SyncV2ResultResetReason[] = [
    "checkpoint-expired", "retention-advanced", "generation-changed", "checkpoint-not-current",
    "view-diverged", "oversized", "invalid-operations", "invalid-checkpoint", "backpressure",
  ];
  return reasons.includes(normalized) ? normalized : "checkpoint-expired";
}

function isAbortError(value: unknown): boolean {
  return isRecord(value) && value.name === "AbortError";
}
