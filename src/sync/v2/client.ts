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
  syncV2ResultDigest,
  type SyncV2ResultDelta,
  type SyncV2ResultItem,
  type SyncV2ResultOperation,
  type SyncV2ResultSnapshot,
  type SyncV2ResultResetReason,
} from "./result-view.js";

const MAX_U64 = 0xffff_ffff_ffff_ffffn;

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

/** The small WebSocket surface used by the optional direct transport. */
export type SyncV2WebSocket = {
  binaryType: string;
  send: (data: Uint8Array | ArrayBuffer) => void;
  close: (code?: number, reason?: string) => void;
  addEventListener: (type: "open" | "message" | "error" | "close", listener: (event: unknown) => void) => void;
  removeEventListener: (type: "open" | "message" | "error" | "close", listener: (event: unknown) => void) => void;
};

export type SyncV2WebSocketFactory = (url: string) => SyncV2WebSocket;

export type SyncV2ClientOptions = {
  /** The member/API origin used for the authenticated view attach. */
  readonly apiBaseUrl?: string;
  /** The global CDN origin. Reactive delivery never uses a regional host. */
  readonly syncBaseUrl?: string;
  readonly fetch?: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  readonly accessToken?: SyncV2AccessToken;
  /** Optional direct WebSocket constructor. HTTP remains the default. */
  readonly webSocketFactory?: SyncV2WebSocketFactory;
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
  readonly #webSocketFactory: SyncV2WebSocketFactory | undefined;
  readonly #pollWindowMs: number;
  readonly #reconnectDelayMs: number;
  #online = false;
  #paused = false;
  #region: string | null = null;
  #correlation = 1n;

  public constructor(options: SyncV2ClientOptions = {}) {
    this.#fetch = options.fetch ?? globalThis.fetch?.bind(globalThis);
    this.#apiBaseUrl = normalizeOrigin(options.apiBaseUrl ?? "https://api.terrascale.tech");
    this.#syncBaseUrl = normalizeOrigin(options.syncBaseUrl ?? "https://sync.api.terrascale.tech");
    this.#accessToken = options.accessToken;
    this.#webSocketFactory = options.webSocketFactory;
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
    if (typeof options.subscription !== "string" || options.subscription.trim().length === 0) {
      throw new TerraScaleSyncV2ClientError("invalid_state", "subscription must be a non-empty string");
    }
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
    if (live !== undefined) {
      live.closed = true;
      live.abortController.abort();
    }
    this.#live.delete(subscription);
    current.view.setOnline(false);
    this.#subscriptions.delete(subscription);
    if (this.#subscriptions.size === 0) this.#online = false;
    return Object.freeze({ kind: "unsubscribe", subscription });
  }

  /**
   * Attaches to the canonical shared view and starts bounded HTTP long-polling.
   * WebSocket is an explicit override. HTTP remains the cacheable default.
   */
  public async subscribe(options: SyncV2SubscribeOptions): Promise<SyncV2LiveSubscription> {
    const transport = options.transport ?? "http";
    if (transport !== "http" && transport !== "websocket") {
      throw new TerraScaleSyncV2ClientError("invalid_state", `unsupported reactive transport '${String(transport)}'`);
    }
    if (this.#fetch === undefined) {
      throw new TerraScaleSyncV2ClientError("transport", "no fetch implementation is available");
    }
    const databaseId = options.databaseId ?? options.database;
    if (databaseId === undefined || databaseId.trim().length === 0) {
      throw new TerraScaleSyncV2ClientError("invalid_state", "databaseId is required for a reactive view attach");
    }
    const opened = this.openSubscription(options);
    const previousOnline = this.#online;
    let attached: AttachedView;
    let initialReset: SyncV2Reset | undefined;
    try {
      attached = await this.#attach(databaseId, options, opened.message, options.signal);
    } catch (error) {
      if (error instanceof TerraScaleSyncV2ClientError && error.code === "reset_required" && options.checkpoint !== undefined) {
        // The supplied checkpoint is stale. Retry attach exactly once without
        // it; the new authorized snapshot becomes the reset cut.
        initialReset = error.reset;
        try {
          attached = await this.#attach(databaseId, options, { ...opened.message, checkpoint: null }, options.signal);
        } catch (retryError) {
          this.#subscriptions.delete(options.subscription);
          throw retryError;
        }
      } else {
        this.#subscriptions.delete(options.subscription);
        throw error;
      }
    }
    const state: LiveSubscriptionState = {
      opened,
      databaseId,
      generation: attached.generation,
      capability: attached.capability,
      capabilityHeader: attached.capabilityHeader,
      logUrl: attached.logUrl,
      snapshotUrls: attached.snapshotUrls,
      closed: false,
      abortController: new AbortController(),
      freshRequired: false,
      transport,
      websocketUrl: attached.websocketUrl,
      socket: undefined,
      etag: undefined,
      etagOffset: undefined,
      loop: undefined,
      reset: initialReset,
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
      state.closed = true;
      state.abortController.abort();
      this.#live.delete(options.subscription);
      this.#subscriptions.delete(options.subscription);
      opened.subscription.view.setOnline(false);
      // setOnline(true) above also touched any pre-existing subscriptions.
      // Restore the transport state that existed before this attach attempt.
      this.setOnline(previousOnline);
      throw error;
    }

    if (transport === "websocket") {
      try {
        await this.#openWebSocket(state, options.signal);
      } catch (error) {
        state.closed = true;
        state.abortController.abort();
        this.#live.delete(options.subscription);
        this.#subscriptions.delete(options.subscription);
        opened.subscription.view.setOnline(false);
        this.setOnline(previousOnline);
        throw error;
      }
    }

    const live = this.#publicLiveSubscription(state);
    if (options.autoStart !== false && transport === "http") state.loop = this.#pollLoop(state, options.signal);
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
            "authentication_expired",
            envelope.message.detail ?? "the reactive session expired",
          );
        }
        if (envelope.message.code === "permission_denied") {
          throw new TerraScaleSyncV2ClientError(
            "permission_denied",
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

  /** Applies one complete WebSocket MessagePack message.
   *
   * The protocol defines one envelope per binary WebSocket message; the
   * plural name remains useful to transport adapters delivering messages in
   * a batch callback.
   */
  public receiveMany(frame: Uint8Array): readonly SyncV2Envelope[] {
    return [this.receive(frame)];
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
    const linked = linkAbortSignals(state.abortController.signal, signal);
    try {
      const response = await this.#request(
        url,
        {
          method: "GET",
          headers: await this.#headers("application/msgpack", state.capability, state.capabilityHeader),
          signal: linked.signal,
        },
        "reactive snapshot/log",
      );
      if (state.closed) return undefined;
      if (response.status === 204 || response.status === 304) return undefined;
      if (!response.ok) throw await this.#httpError(response, "reactive snapshot/log");
      const bytes = new Uint8Array(await response.arrayBuffer());
      return bytes.length === 0 ? undefined : bytes;
    } finally {
      linked.dispose();
    }
  }

  async #pollOnce(state: LiveSubscriptionState, signal?: AbortSignal): Promise<SyncV2Envelope | undefined> {
    while (this.#paused && !state.closed) await delay(this.#reconnectDelayMs, signal ?? state.abortController.signal);
    if (state.closed) return undefined;
    const revision = state.opened.subscription.view.getRevision();
    const url = new URL(state.logUrl);
    url.searchParams.set("offset", revision.toString(10));
    const timeout = new AbortController();
    if (signal?.aborted) throw signal.reason;
    const abort = () => timeout.abort(state.abortController.signal.reason ?? signal?.reason);
    state.abortController.signal.addEventListener("abort", abort, { once: true });
    if (signal !== undefined) {
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
            headers: await this.#headers("application/msgpack", state.capability, state.capabilityHeader),
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
      if (state.closed) return undefined;
      if (response.status === 204 || response.status === 304) return undefined;
      if (!response.ok) throw await this.#httpError(response, "reactive log poll");
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (state.closed) return undefined;
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
    } catch (error) {
      if (error instanceof TerraScaleSyncV2ClientError && error.code === "reset_required") {
        if (state.freshRequired) throw error;
        state.reset = error.reset;
        state.freshRequired = true;
        // A refused/expired checkpoint is not a transport failure. Reattach
        // once without it so the server can issue a fresh authorized cut.
        // The resulting snapshot is installed before this method returns,
        // notifying view observers atomically.
        await this.#reconnect(state, signal);
        return undefined;
      }
      throw error;
    } finally {
      globalThis.clearTimeout(timer);
      signal?.removeEventListener("abort", abort);
      state.abortController.signal.removeEventListener("abort", abort);
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
        if (error instanceof TerraScaleSyncV2ClientError && error.code === "reset_required" && state.freshRequired) return;
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
    state.socket?.close(1000, "reconnect");
    state.socket = undefined;
    const current = state.freshRequired ? undefined : state.opened.subscription.view.getCheckpoint();
    const message = state.opened.message;
    const options: SyncV2SubscribeOptions = {
      subscription: message.subscription,
      query: message.query,
      parameters: new Uint8Array(message.parameters),
      checkpoint: current,
      databaseId: state.databaseId,
      transport: state.transport,
      autoStart: false,
    };
    const linked = linkAbortSignals(state.abortController.signal, signal);
    let attached: AttachedView;
    try {
      try {
        attached = await this.#attach(state.databaseId, options, {
          ...message,
          checkpoint: current?.toBytes() ?? null,
        }, linked.signal);
      } catch (error) {
        if (error instanceof TerraScaleSyncV2ClientError && error.code === "reset_required" && !state.freshRequired) {
          state.reset = error.reset;
          state.freshRequired = true;
          attached = await this.#attach(state.databaseId, options, { ...message, checkpoint: null }, linked.signal);
        } else {
          throw error;
        }
      }
    } finally {
      linked.dispose();
    }
    if (state.closed) return;
    state.generation = attached.generation;
    state.capability = attached.capability;
    state.capabilityHeader = attached.capabilityHeader;
    state.logUrl = attached.logUrl;
    state.snapshotUrls = attached.snapshotUrls;
    state.websocketUrl = attached.websocketUrl;
    if (attached.snapshot !== undefined) {
      installAttachedSnapshot(state.opened.subscription.view, attached.snapshot);
    } else {
      for (const snapshotUrl of attached.snapshotUrls) {
        const bytes = await this.#getBytes(snapshotUrl, state, signal);
        if (bytes !== undefined) this.receive(bytes);
      }
    }
    if (state.transport === "websocket") await this.#openWebSocket(state, signal);
    state.freshRequired = false;
    state.regionMoved = false;
    this.setOnline(true);
  }

  async #openWebSocket(state: LiveSubscriptionState, signal?: AbortSignal): Promise<void> {
    const factory = this.#webSocketFactory ?? (typeof globalThis.WebSocket === "function"
      ? (url: string) => new globalThis.WebSocket(url) as unknown as SyncV2WebSocket
      : undefined);
    if (factory === undefined) {
      throw new TerraScaleSyncV2ClientError(
        "transport",
        "WebSocket transport was requested but no WebSocket implementation is available",
      );
    }
    const url = state.websocketUrl ?? websocketUrl(this.#syncBaseUrl, state.opened.subscription.id, state.generation);
    const socket = factory(url);
    if (socket === undefined || socket === null || typeof socket.send !== "function" || typeof socket.close !== "function") {
      throw new TerraScaleSyncV2ClientError("transport", "the WebSocket factory returned an invalid socket");
    }
    socket.binaryType = "arraybuffer";
    state.socket = socket;
    await new Promise<void>((resolve, reject) => {
      let opened = false;
      let settled = false;
      const cleanupOpen = () => socket.removeEventListener("open", onOpen);
      const abort = () => {
        socket.close(1000, "aborted");
        if (!settled) {
          settled = true;
          reject(signal?.reason ?? state.abortController.signal.reason);
        }
      };
      const onOpen = () => {
        opened = true;
        cleanupOpen();
        if (signal?.aborted || state.abortController.signal.aborted) {
          abort();
          return;
        }
        try {
          socket.send(this.encode(state.opened.message, this.#correlation++));
          if (!settled) {
            settled = true;
            resolve();
          }
        } catch (error) {
          if (!settled) {
            settled = true;
            reject(error instanceof TerraScaleSyncV2ClientError ? error : new TerraScaleSyncV2ClientError("transport", "WebSocket subscribe failed", { cause: error }));
          }
        }
      };
      const onMessage = (event: unknown) => {
        void (async () => {
          try {
            const frame = await socketFrame(event);
            if (frame === undefined) throw new TerraScaleSyncV2ClientError("protocol_error", "WebSocket data was not binary MessagePack");
            this.receiveMany(frame);
          } catch (error) {
            this.setOnline(false);
            socket.close(1002, "invalid reactive frame");
            if (!opened && !settled) {
              settled = true;
              reject(error);
            }
          }
        })();
      };
      const onError = (event: unknown) => {
        this.setOnline(false);
        if (!opened && !settled) {
          settled = true;
          reject(new TerraScaleSyncV2ClientError("transport", "WebSocket connection failed", { cause: event }));
        }
      };
      const onClose = () => {
        this.setOnline(false);
        if (!opened && !settled) {
          settled = true;
          reject(new TerraScaleSyncV2ClientError("transport", "WebSocket closed before it opened"));
        }
      };
      socket.addEventListener("open", onOpen);
      socket.addEventListener("message", onMessage);
      socket.addEventListener("error", onError);
      socket.addEventListener("close", onClose);
      state.abortController.signal.addEventListener("abort", abort, { once: true });
      signal?.addEventListener("abort", abort, { once: true });
    });
  }

  #publicLiveSubscription(state: LiveSubscriptionState): SyncV2LiveSubscription {
    return {
      id: state.opened.subscription.id,
      view: state.opened.subscription.view,
      get transport() { return state.transport; },
      get generation() { return state.generation; },
      get capability() { return state.capability; },
      get logUrl() { return state.logUrl; },
      get snapshotUrls() { return Object.freeze([...state.snapshotUrls]); },
      get reset() { return state.reset; },
      checkpoint: () => state.opened.subscription.view.getCheckpoint(),
      poll: signal => this.#pollOnce(state, signal),
      reconnect: () => this.#reconnect(state),
      close: () => {
        if (state.closed) return;
        state.closed = true;
        state.abortController.abort();
        state.socket?.close(1000, "closed");
        state.socket = undefined;
        this.#live.delete(state.opened.subscription.id);
        this.#subscriptions.delete(state.opened.subscription.id);
        state.opened.subscription.view.setOnline(false);
        if (this.#subscriptions.size === 0) this.#online = false;
      },
    };
  }

  async #headers(accept: string, capability: string | undefined, capabilityHeader = "x-terrascale-view-capability"): Promise<Headers> {
    const headers = new Headers({ accept });
    if (capability !== undefined) {
      headers.set(capabilityHeader, capability);
      // Keep compatibility with early E-5 members while the declared
      // response header is being rolled out. The server verifies the
      // canonical view header; the alias is harmless and can be removed once
      // old members are no longer supported.
      if (capabilityHeader !== "x-terrascale-capability") {
        headers.set("x-terrascale-capability", capability);
      }
    }
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
    let error: Record<string, unknown> | undefined;
    let reset: SyncV2Reset | undefined;
    try {
      const body: unknown = await response.clone().json();
      if (isRecord(body)) {
        error = isRecord(body.error) ? body.error : body;
        if (typeof error.message === "string") detail = error.message;
      }
    } catch {
      // Status and operation are sufficient when an error body is unavailable.
    }

    // Authentication and authorization are terminal for this session. Check
    // their typed contract codes before interpreting a status or reset reason,
    // so an expired session can never be retried or misclassified as a stale
    // checkpoint.
    if (response.status === 401 || error?.code === "authentication_expired") {
      return new TerraScaleSyncV2ClientError("authentication_expired", detail);
    }
    if (response.status === 403 || error?.code === "permission_denied") {
      return new TerraScaleSyncV2ClientError("permission_denied", detail);
    }
    if (error?.code === "checkpoint_refused") {
      reset = { reason: "checkpoint-expired", message: detail };
    } else if (typeof error?.reason === "string") {
      reset = { reason: normalizeResetReason(error.reason), message: detail };
    }
    if (reset === undefined && response.status === 410) {
      reset = { reason: "retention-advanced", message: detail };
    }
    if (reset !== undefined) return new TerraScaleSyncV2ClientError("reset_required", detail, undefined, reset);
    return new TerraScaleSyncV2ClientError("transport", detail);
  }
}

type AttachedView = {
  readonly generation: bigint;
  readonly checkpoint: Uint8Array | undefined;
  readonly capability: string;
  readonly capabilityHeader: string;
  readonly snapshotUrls: readonly string[];
  readonly logUrl: string;
  readonly snapshot: TerrascaleSyncV2MsgpackSnapshot | undefined;
  readonly websocketUrl: string | undefined;
};

type LiveSubscriptionState = {
  readonly opened: SyncV2OpenSubscription;
  readonly databaseId: string;
  generation: bigint;
  capability: string;
  capabilityHeader: string;
  snapshotUrls: readonly string[];
  logUrl: string;
  closed: boolean;
  readonly abortController: AbortController;
  freshRequired: boolean;
  transport: SyncV2Transport;
  websocketUrl: string | undefined;
  socket: SyncV2WebSocket | undefined;
  etag: string | undefined;
  etagOffset: bigint | undefined;
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
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new TypeError("reactive origins must use HTTP or HTTPS");
  if (url.username !== "" || url.password !== "") throw new TypeError("reactive origins cannot contain credentials");
  if (url.pathname !== "/" || url.search !== "" || url.hash !== "") throw new TypeError("reactive origins must not contain a path or query");
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

async function socketFrame(event: unknown): Promise<Uint8Array | undefined> {
  if (!isRecord(event)) return undefined;
  const data = event.data;
  if (data instanceof Uint8Array) return new Uint8Array(data);
  if (data instanceof ArrayBuffer) return new Uint8Array(data.slice(0));
  if (typeof Blob !== "undefined" && data instanceof Blob) {
    return new Uint8Array(await data.arrayBuffer());
  }
  return undefined;
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
  if (Array.isArray(value) && value.length <= 4096 && value.every(item => typeof item === "number" && Number.isInteger(item) && item >= 0 && item <= 255)) {
    return Uint8Array.from(value as number[]);
  }
  if (typeof value === "string" && value.length <= 8192) {
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
    const parsed = typeof value === "bigint"
      ? value
      : typeof value === "number" && Number.isSafeInteger(value) && value >= 0
        ? BigInt(value)
        : typeof value === "string" && /^\d+$/u.test(value)
          ? BigInt(value)
          : undefined;
    if (parsed !== undefined && parsed >= 0n && parsed <= MAX_U64) return parsed;
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
  const view = typeof value.viewId === "string"
    ? value.viewId
    : typeof value.view_id === "string"
      ? value.view_id
      : typeof value.view === "string"
        ? value.view
        : subscription;
  if (!/^[A-Za-z0-9_-]{1,128}$/u.test(view)) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has an invalid view id");
  }
  const generation = bigintFromUnknown(value.generation ?? value.view_generation ?? value.viewGeneration, "generation");
  const capabilityValue = value.capability ?? value.viewCapability ?? value.view_capability;
  if (typeof capabilityValue !== "string" || capabilityValue.trim().length === 0 || capabilityValue.length > 4096 || capabilityValue.includes(",")) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has no view capability");
  }
  const capabilityHeader = value.capability_header ?? value.capabilityHeader;
  if (capabilityHeader !== undefined && (typeof capabilityHeader !== "string" ||
    !new Set(["x-terrascale-view-capability", "x-terrascale-capability"]).has(capabilityHeader.toLowerCase()))) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has an unsupported capability header");
  }
  const normalizedCapabilityHeader = typeof capabilityHeader === "string"
    ? capabilityHeader.toLowerCase()
    : "x-terrascale-view-capability";
  const websocketValue = value.websocketUrl ?? value.websocket_url ?? value.wsUrl ?? value.ws_url;
  const websocketUrl = websocketValue === undefined
    ? undefined
    : canonicalWebSocketUrl(websocketValue, syncOrigin, view, generation);
  const expectedLogPath = canonicalViewPath(view, generation, "log");
  const rawLogUrl = value.logUrl ?? value.log_url ?? value.log;
  const logUrl = rawLogUrl === undefined
    ? canonicalUrl(syncOrigin, expectedLogPath)
    : canonicalAttachedUrl(rawLogUrl, syncOrigin, expectedLogPath, "log");
  const rawSnapshotUrls = value.snapshotUrls ?? value.snapshot_urls ?? value.snapshotChunkUrls ?? value.snapshot_chunk_urls ?? value.snapshot_chunks;
  const snapshotUrls: string[] = [];
  if (Array.isArray(rawSnapshotUrls)) {
    if (rawSnapshotUrls.length > 1024) throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has too many snapshot URLs");
    for (const [index, raw] of rawSnapshotUrls.entries()) {
      if (typeof raw !== "string") throw new TerraScaleSyncV2ClientError("protocol_error", `snapshot URL ${index} is not a string`);
      const path = canonicalViewPath(view, generation, `snapshot/${index}`);
      snapshotUrls.push(canonicalAttachedUrl(raw, syncOrigin, path, "snapshot"));
    }
  } else if (rawSnapshotUrls !== undefined) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response snapshot URLs are not an array");
  }
  const checkpointValue = value.checkpoint;
  const checkpoint = checkpointValue === undefined || checkpointValue === null ? undefined : bytesFromUnknown(checkpointValue, "checkpoint");
  const snapshot = decodeInlineSnapshot(value.snapshot, checkpoint);
  if (snapshot !== undefined && snapshot.view_generation !== generation) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "inline snapshot generation does not match the attached view");
  }
  if (snapshot === undefined && snapshotUrls.length === 0 && checkpoint === undefined) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "reactive attach response has no snapshot or snapshot URLs");
  }
  // A checkpoint-only attach is a valid resume bootstrap. The opaque token is
  // installed at revision zero; subsequent deltas advance from that cut.
  if (snapshot === undefined && snapshotUrls.length === 0 && checkpoint !== undefined) {
    return {
      generation,
      checkpoint,
      capability: capabilityValue,
      capabilityHeader: normalizedCapabilityHeader,
      snapshotUrls,
      logUrl,
      snapshot: {
        view_generation: generation,
        authorization_generation: 0n,
        revision: 0n,
        items: [],
        digest: syncV2ResultDigest([]),
        checkpoint,
      },
      websocketUrl,
    };
  }
  return { generation, checkpoint, capability: capabilityValue, capabilityHeader: normalizedCapabilityHeader, snapshotUrls, logUrl, snapshot, websocketUrl };
}

function canonicalWebSocketUrl(raw: unknown, apiOrigin: string, view: string, generation: bigint): string {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 4096) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "WebSocket URL is outside its bound");
  }
  let parsed: URL;
  try {
    parsed = new URL(raw, apiOrigin);
  } catch (error) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "WebSocket URL is invalid", { cause: error });
  }
  const expectedOrigin = new URL(apiOrigin).origin.replace(/^http/iu, "ws");
  const expectedPath = canonicalViewPath(view, generation, "websocket");
  if (parsed.protocol !== "ws:" && parsed.protocol !== "wss:") {
    throw new TerraScaleSyncV2ClientError("protocol_error", "WebSocket URL must use ws or wss");
  }
  if (parsed.origin !== expectedOrigin || parsed.username !== "" || parsed.password !== "" || parsed.hash !== "" || parsed.pathname !== expectedPath) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "WebSocket URL is not the canonical API URL");
  }
  if ([...parsed.searchParams.keys()].length !== 0) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "WebSocket URL must not carry query parameters");
  }
  return parsed.toString();
}

function websocketUrl(apiOrigin: string, view: string, generation: bigint): string {
  const parsed = new URL(apiOrigin);
  parsed.protocol = parsed.protocol === "https:" ? "wss:" : "ws:";
  parsed.pathname = canonicalViewPath(view, generation, "websocket");
  return parsed.toString();
}

function canonicalAttachedUrl(raw: unknown, syncOrigin: string, expectedPath: string, kind: "log" | "snapshot"): string {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 4096) {
    throw new TerraScaleSyncV2ClientError("protocol_error", `${kind} URL is outside its bound`);
  }
  let parsed: URL;
  try {
    parsed = new URL(raw, syncOrigin);
  } catch (error) {
    throw new TerraScaleSyncV2ClientError("protocol_error", `${kind} URL is invalid`, { cause: error });
  }
  const origin = new URL(syncOrigin).origin;
  if (parsed.origin !== origin || parsed.username !== "" || parsed.password !== "" || parsed.hash !== "" || parsed.pathname !== expectedPath) {
    throw new TerraScaleSyncV2ClientError("protocol_error", `${kind} URL is not the canonical sync URL`);
  }
  const allowed = kind === "snapshot" ? "cut" : "offset";
  const keys = [...new Set([...parsed.searchParams.keys()])];
  if (keys.some(key => key !== allowed) || parsed.searchParams.getAll(allowed).length > 1) {
    throw new TerraScaleSyncV2ClientError("protocol_error", `${kind} URL has unsupported query parameters`);
  }
  const value = parsed.searchParams.get(allowed);
  if (kind === "snapshot" && (value === null || value.length === 0)) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "snapshot URL is missing its immutable cut");
  }
  if (kind === "snapshot" && value !== null) {
    try {
      if (!/^\d+$/u.test(value) || BigInt(value) > MAX_U64) throw new Error("invalid cut");
    } catch (error) {
      throw new TerraScaleSyncV2ClientError("protocol_error", "snapshot URL has an invalid immutable cut", { cause: error });
    }
  }
  if (kind === "log" && value !== null) {
    try {
      if (!/^\d+$/u.test(value) || BigInt(value) > MAX_U64) throw new Error("invalid offset");
    } catch (error) {
      throw new TerraScaleSyncV2ClientError("protocol_error", "log URL has an invalid offset", { cause: error });
    }
  }
  const canonical = new URL(expectedPath, syncOrigin);
  // The attach-time log offset is only a hint; polling always uses the
  // installed revision. Snapshot cuts, however, are immutable identity and
  // must remain part of the public URL.
  if (kind === "snapshot" && value !== null) canonical.searchParams.set(allowed, value);
  return canonical.toString();
}

function decodeInlineSnapshot(value: unknown, checkpoint: Uint8Array | undefined): TerrascaleSyncV2MsgpackSnapshot | undefined {
  if (!isRecord(value)) return undefined;
  const itemsValue = value.items;
  if (!Array.isArray(itemsValue)) throw new TerraScaleSyncV2ClientError("protocol_error", "inline snapshot items are not an array");
  if (itemsValue.length > 10_000) throw new TerraScaleSyncV2ClientError("protocol_error", "inline snapshot has too many items");
  const items = itemsValue.map((raw, index) => {
    if (!isRecord(raw) || typeof raw.identity !== "string" || !Array.isArray(raw.fields)) {
      throw new TerraScaleSyncV2ClientError("protocol_error", `inline snapshot item ${index} is malformed`);
    }
    if (raw.fields.length > 1_024) throw new TerraScaleSyncV2ClientError("protocol_error", `inline snapshot item ${index} has too many fields`);
    return {
      identity: raw.identity,
      fields: raw.fields.map((field, fieldIndex) => {
        if (!isRecord(field) || typeof field.path !== "string") throw new TerraScaleSyncV2ClientError("protocol_error", `inline snapshot field ${fieldIndex} is malformed`);
        return { path: field.path, value: bytesFromUnknown(field.value, `snapshot.items[${index}].fields[${fieldIndex}].value`) };
      }),
    };
  });
  if (typeof value.digest !== "string") throw new TerraScaleSyncV2ClientError("protocol_error", "inline snapshot digest is missing");
  const inlineCheckpoint = bytesFromUnknown(value.checkpoint, "snapshot.checkpoint");
  if (checkpoint !== undefined && !bytesEqual(checkpoint, inlineCheckpoint)) {
    throw new TerraScaleSyncV2ClientError("protocol_error", "attach checkpoint does not match inline snapshot checkpoint");
  }
  return {
    view_generation: bigintFromUnknown(value.view_generation ?? value.viewGeneration, "snapshot.view_generation"),
    authorization_generation: bigintFromUnknown(value.authorization_generation ?? value.authorizationGeneration ?? 0, "snapshot.authorization_generation"),
    revision: bigintFromUnknown(value.revision, "snapshot.revision"),
    items,
    digest: value.digest,
    checkpoint: checkpoint ?? inlineCheckpoint,
  };
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length && left.every((byte, index) => byte === right[index]);
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

function linkAbortSignals(...signals: (AbortSignal | undefined)[]): {
  readonly signal: AbortSignal;
  readonly dispose: () => void;
} {
  const controller = new AbortController();
  const listeners: Array<() => void> = [];
  const abort = (signal: AbortSignal): void => {
    if (!controller.signal.aborted) controller.abort(signal.reason);
  };
  for (const signal of signals) {
    if (signal === undefined) continue;
    const listener = () => abort(signal);
    if (signal.aborted) abort(signal);
    else {
      signal.addEventListener("abort", listener, { once: true });
      listeners.push(() => signal.removeEventListener("abort", listener));
    }
  }
  return {
    signal: controller.signal,
    dispose: () => {
      for (const remove of listeners) remove();
    },
  };
}
