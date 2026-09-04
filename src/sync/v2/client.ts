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
} from "./result-view.js";

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

export type SyncV2Subscription = {
  readonly id: string;
  readonly view: SyncV2ResultViewStore;
};

export type SyncV2OpenSubscription = {
  readonly message: Extract<SyncV2Message, { readonly kind: "subscribe" }>;
  readonly subscription: SyncV2Subscription;
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
      | "remote_error",
    reason: string,
    options?: ErrorOptions,
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
  #online = false;
  #paused = false;
  #region: string | null = null;

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
    current.view.setOnline(false);
    this.#subscriptions.delete(subscription);
    return Object.freeze({ kind: "unsubscribe", subscription });
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
        break;
      case "backpressure":
        this.#paused = envelope.message.mode === "pause";
        break;
      case "error":
        this.setOnline(false);
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
}

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
