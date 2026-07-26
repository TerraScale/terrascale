import type { JsonValue } from "../models.js";

export const syncProtocolVersion = "terrascale.sync.v1" as const;

export type SyncUInt64 = `${bigint}`;

export type DatabaseRevision = {
  readonly RevisionEpoch: SyncUInt64;
  readonly CommitSequence: SyncUInt64;
};

export type SyncStateVersion = {
  readonly QuerySetVersion: SyncUInt64;
  readonly DatabaseRevision: DatabaseRevision;
  readonly IdentityVersion: SyncUInt64;
};

export type SyncError = {
  readonly Code: string;
  readonly Message: string;
  readonly Retryable: boolean;
};

export type QueryUpdated = {
  readonly QueryId: string;
  readonly QueryFingerprint: string;
  readonly Result: JsonValue;
  readonly ResultHash: string;
  readonly ExecutionRevision: DatabaseRevision;
  readonly ValidatedThroughRevision: DatabaseRevision;
  readonly Journal?: JsonValue | undefined;
};

export type QueryFailed = {
  readonly QueryId: string;
  readonly Error: SyncError;
  readonly ValidatedThroughRevision: DatabaseRevision;
};

export type QueryRemoved = {
  readonly QueryId: string;
};

export type SyncServerTiming = {
  readonly QueryMilliseconds: number;
  readonly ValidationMilliseconds: number;
  readonly SerializationMilliseconds: number;
};

export type CompleteTransition = {
  readonly FrameType: "Complete";
  readonly TransitionId: string;
  readonly StartVersion: SyncStateVersion;
  readonly EndVersion: SyncStateVersion;
  readonly QueryUpdated: readonly QueryUpdated[];
  readonly QueryFailed: readonly QueryFailed[];
  readonly QueryRemoved: readonly QueryRemoved[];
  readonly ServerTiming?: SyncServerTiming | undefined;
  readonly IngressRegion: string;
  readonly ServingRegion: string;
  readonly DatabaseRegion: string;
  readonly RouteGeneration: SyncUInt64;
};

export type CompleteTransitionEnvelope = {
  readonly Protocol: typeof syncProtocolVersion;
  readonly MessageType: "Transition";
  readonly ConnectionSequence: SyncUInt64;
  readonly MessageSequence: SyncUInt64;
  readonly Payload: CompleteTransition;
};

export type TransitionChunk = {
  readonly FrameType: "Chunk";
  readonly TransitionId: string;
  readonly ChunkIndex: number;
  readonly ChunkCount: number;
  readonly UncompressedLength: number;
  readonly Sha256: string;
  readonly Data: string;
};

export type MutationResult = {
  readonly ClientRequestId: string;
  readonly Succeeded: boolean;
  readonly Result?: JsonValue | undefined;
  readonly Error?: SyncError | undefined;
  readonly CommittedRevision?: DatabaseRevision | undefined;
};

export type AuthenticationError = {
  readonly Error: SyncError;
};

export type ResyncRequired = {
  readonly Reason: string;
  readonly EarliestRetainedRevision: DatabaseRevision;
  readonly LastAcknowledgedVersion?: SyncStateVersion | undefined;
};

export type RegionRouteChanged = {
  readonly IngressRegion: string;
  readonly ServingRegion: string;
  readonly DatabaseRegion: string;
  readonly RouteGeneration: SyncUInt64;
  readonly ReconnectRequired: boolean;
};

export type SyncPing = {
  readonly Nonce: SyncUInt64;
  readonly SentAt: string;
};

export type FatalProtocolError = {
  readonly Error: {
    readonly Code: string;
    readonly Message: string;
  };
  readonly ResumeVersion?: SyncStateVersion | undefined;
};

export type SyncServerEnvelope =
  | CompleteTransitionEnvelope
  | SyncEnvelope<"Transition", TransitionChunk>
  | SyncEnvelope<"MutationResult", MutationResult>
  | SyncEnvelope<"AuthenticationError", AuthenticationError>
  | SyncEnvelope<"ResyncRequired", ResyncRequired>
  | SyncEnvelope<"RegionRouteChanged", RegionRouteChanged>
  | SyncEnvelope<"Ping", SyncPing>
  | SyncEnvelope<"FatalProtocolError", FatalProtocolError>;

type SyncEnvelope<TMessage extends string, TPayload> = {
  readonly Protocol: typeof syncProtocolVersion;
  readonly MessageType: TMessage;
  readonly ConnectionSequence: SyncUInt64;
  readonly MessageSequence: SyncUInt64;
  readonly Payload: TPayload;
};

export function parseCompleteTransitionEnvelope(value: unknown): CompleteTransitionEnvelope {
  const envelope = object(value, "envelope");
  exactKeys(
    envelope,
    ["Protocol", "MessageType", "ConnectionSequence", "MessageSequence", "Payload"],
    "envelope",
  );
  if (envelope.Protocol !== syncProtocolVersion || envelope.MessageType !== "Transition") {
    throw new TypeError("The value is not a TerraScale Sync v1 transition.");
  }

  return Object.freeze({
    Protocol: syncProtocolVersion,
    MessageType: "Transition",
    ConnectionSequence: uint64(envelope.ConnectionSequence, "ConnectionSequence"),
    MessageSequence: uint64(envelope.MessageSequence, "MessageSequence"),
    Payload: parseCompleteTransition(envelope.Payload),
  });
}

export function parseCompleteTransition(value: unknown): CompleteTransition {
  return transition(value);
}

export function parseSyncServerEnvelope(value: unknown): SyncServerEnvelope {
  const envelope = object(value, "envelope");
  exactKeys(
    envelope,
    ["Protocol", "MessageType", "ConnectionSequence", "MessageSequence", "Payload"],
    "envelope",
  );
  if (envelope.Protocol !== syncProtocolVersion) {
    throw new TypeError("The value is not a TerraScale Sync v1 envelope.");
  }
  const common = {
    Protocol: syncProtocolVersion,
    ConnectionSequence: uint64(envelope.ConnectionSequence, "ConnectionSequence"),
    MessageSequence: uint64(envelope.MessageSequence, "MessageSequence"),
  };

  switch (envelope.MessageType) {
    case "Transition": {
      const payload = object(envelope.Payload, "Payload");
      return Object.freeze(payload.FrameType === "Complete"
        ? { ...common, MessageType: "Transition", Payload: transition(payload) }
        : { ...common, MessageType: "Transition", Payload: transitionChunk(payload) });
    }
    case "MutationResult":
      return Object.freeze({
        ...common,
        MessageType: "MutationResult",
        Payload: mutationResult(envelope.Payload),
      });
    case "AuthenticationError":
      return Object.freeze({
        ...common,
        MessageType: "AuthenticationError",
        Payload: authenticationError(envelope.Payload),
      });
    case "ResyncRequired":
      return Object.freeze({
        ...common,
        MessageType: "ResyncRequired",
        Payload: resyncRequired(envelope.Payload),
      });
    case "RegionRouteChanged":
      return Object.freeze({
        ...common,
        MessageType: "RegionRouteChanged",
        Payload: regionRouteChanged(envelope.Payload),
      });
    case "Ping":
      return Object.freeze({ ...common, MessageType: "Ping", Payload: ping(envelope.Payload) });
    case "FatalProtocolError":
      return Object.freeze({
        ...common,
        MessageType: "FatalProtocolError",
        Payload: fatalProtocolError(envelope.Payload),
      });
    default:
      throw new TypeError("MessageType is not a TerraScale Sync v1 server message.");
  }
}

export function compareDatabaseRevision(left: DatabaseRevision, right: DatabaseRevision): number {
  const epoch = compareUInt64(left.RevisionEpoch, right.RevisionEpoch);
  return epoch === 0 ? compareUInt64(left.CommitSequence, right.CommitSequence) : epoch;
}

export function equalStateVersion(left: SyncStateVersion, right: SyncStateVersion): boolean {
  return left.QuerySetVersion === right.QuerySetVersion
    && left.IdentityVersion === right.IdentityVersion
    && compareDatabaseRevision(left.DatabaseRevision, right.DatabaseRevision) === 0;
}

export function parseSyncUInt64(value: string): bigint {
  uint64(value, "value");
  return BigInt(value);
}

function transition(value: unknown): CompleteTransition {
  const frame = object(value, "Payload");
  exactKeys(
    frame,
    [
      "FrameType",
      "TransitionId",
      "StartVersion",
      "EndVersion",
      "QueryUpdated",
      "QueryFailed",
      "QueryRemoved",
      "ServerTiming",
      "IngressRegion",
      "ServingRegion",
      "DatabaseRegion",
      "RouteGeneration",
    ],
    "Payload",
  );
  if (frame.FrameType !== "Complete") {
    throw new TypeError("Only complete transition frames can be applied.");
  }

  const updated = array(frame.QueryUpdated, "QueryUpdated").map(queryUpdated);
  const failed = array(frame.QueryFailed, "QueryFailed").map(queryFailed);
  const removed = array(frame.QueryRemoved, "QueryRemoved").map(queryRemoved);
  if (updated.length + failed.length + removed.length > 100) {
    throw new RangeError("A transition cannot contain more than 100 query states.");
  }
  uniqueQueryIds(updated, failed, removed);

  const startVersion = stateVersion(frame.StartVersion, "StartVersion");
  const endVersion = stateVersion(frame.EndVersion, "EndVersion");
  if (
    compareUInt64(endVersion.QuerySetVersion, startVersion.QuerySetVersion) < 0
    || compareDatabaseRevision(
      endVersion.DatabaseRevision,
      startVersion.DatabaseRevision,
    ) < 0
    || compareUInt64(endVersion.IdentityVersion, startVersion.IdentityVersion) < 0
  ) {
    throw new TypeError("A transition version cannot move backward.");
  }
  for (const query of [...updated, ...failed]) {
    if (compareDatabaseRevision(query.ValidatedThroughRevision, endVersion.DatabaseRevision) !== 0) {
      throw new TypeError("Every query state must be valid through EndVersion.");
    }
  }
  for (const query of updated) {
    if (
      compareDatabaseRevision(query.ExecutionRevision, query.ValidatedThroughRevision) > 0
    ) {
      throw new TypeError("A query execution revision cannot exceed its validation revision.");
    }
  }

  const parsed = {
    FrameType: "Complete" as const,
    TransitionId: ulid(frame.TransitionId, "TransitionId"),
    StartVersion: startVersion,
    EndVersion: endVersion,
    QueryUpdated: Object.freeze(updated),
    QueryFailed: Object.freeze(failed),
    QueryRemoved: Object.freeze(removed),
    IngressRegion: region(frame.IngressRegion, "IngressRegion"),
    ServingRegion: region(frame.ServingRegion, "ServingRegion"),
    DatabaseRegion: region(frame.DatabaseRegion, "DatabaseRegion"),
    RouteGeneration: uint64(frame.RouteGeneration, "RouteGeneration"),
  };

  return Object.freeze(frame.ServerTiming === undefined
    ? parsed
    : { ...parsed, ServerTiming: serverTiming(frame.ServerTiming) });
}

function transitionChunk(value: unknown): TransitionChunk {
  const chunk = object(value, "Payload");
  exactKeys(
    chunk,
    [
      "FrameType",
      "TransitionId",
      "ChunkIndex",
      "ChunkCount",
      "UncompressedLength",
      "Sha256",
      "Data",
    ],
    "Payload",
  );
  if (chunk.FrameType !== "Chunk") {
    throw new TypeError("Transition FrameType must be Complete or Chunk.");
  }
  const uncompressedLength = integer(
    chunk.UncompressedLength,
    1,
    8 * 1024 * 1024,
    "UncompressedLength",
  );
  const chunkCount = integer(chunk.ChunkCount, 1, 32, "ChunkCount");
  const chunkIndex = integer(chunk.ChunkIndex, 0, chunkCount - 1, "ChunkIndex");
  const expectedChunkCount = Math.ceil(uncompressedLength / (256 * 1024));
  if (chunkCount !== expectedChunkCount) {
    throw new TypeError("ChunkCount does not match UncompressedLength.");
  }
  const data = base64(chunk.Data, "Data");
  const expectedLength = chunkIndex === chunkCount - 1
    ? uncompressedLength - chunkIndex * 256 * 1024
    : 256 * 1024;
  if (base64DecodedLength(data) !== expectedLength) {
    throw new TypeError("Data length does not match the declared chunk position.");
  }
  return Object.freeze({
    FrameType: "Chunk",
    TransitionId: ulid(chunk.TransitionId, "TransitionId"),
    ChunkIndex: chunkIndex,
    ChunkCount: chunkCount,
    UncompressedLength: uncompressedLength,
    Sha256: hash(chunk.Sha256, "Sha256"),
    Data: data,
  });
}

function mutationResult(value: unknown): MutationResult {
  const result = object(value, "Payload");
  exactKeys(
    result,
    ["ClientRequestId", "Succeeded", "Result", "Error", "CommittedRevision"],
    "Payload",
    ["Result", "Error", "CommittedRevision"],
  );
  const succeeded = boolean(result.Succeeded, "Succeeded");
  const parsed = {
    ClientRequestId: ulid(result.ClientRequestId, "ClientRequestId"),
    Succeeded: succeeded,
  };
  if (succeeded) {
    if (result.Error !== undefined || result.CommittedRevision === undefined) {
      throw new TypeError("A successful mutation requires CommittedRevision and no Error.");
    }
    return Object.freeze({
      ...parsed,
      Result: result.Result === undefined ? undefined : json(result.Result, "Result"),
      CommittedRevision: revision(result.CommittedRevision, "CommittedRevision"),
    });
  }
  if (result.Error === undefined || result.CommittedRevision !== undefined) {
    throw new TypeError("A failed mutation requires Error and no CommittedRevision.");
  }
  return Object.freeze({ ...parsed, Error: syncError(result.Error, "Error") });
}

function authenticationError(value: unknown): AuthenticationError {
  const payload = object(value, "Payload");
  exactKeys(payload, ["Error"], "Payload");
  return Object.freeze({ Error: syncError(payload.Error, "Error") });
}

function resyncRequired(value: unknown): ResyncRequired {
  const payload = object(value, "Payload");
  exactKeys(
    payload,
    ["Reason", "EarliestRetainedRevision", "LastAcknowledgedVersion"],
    "Payload",
    ["LastAcknowledgedVersion"],
  );
  const parsed = {
    Reason: limitedString(payload.Reason, 4 * 1024, "Reason"),
    EarliestRetainedRevision: revision(
      payload.EarliestRetainedRevision,
      "EarliestRetainedRevision",
    ),
  };
  return Object.freeze(payload.LastAcknowledgedVersion === undefined
    ? parsed
    : {
        ...parsed,
        LastAcknowledgedVersion: stateVersion(
          payload.LastAcknowledgedVersion,
          "LastAcknowledgedVersion",
        ),
      });
}

function regionRouteChanged(value: unknown): RegionRouteChanged {
  const payload = object(value, "Payload");
  exactKeys(
    payload,
    [
      "IngressRegion",
      "ServingRegion",
      "DatabaseRegion",
      "RouteGeneration",
      "ReconnectRequired",
    ],
    "Payload",
  );
  return Object.freeze({
    IngressRegion: region(payload.IngressRegion, "IngressRegion"),
    ServingRegion: region(payload.ServingRegion, "ServingRegion"),
    DatabaseRegion: region(payload.DatabaseRegion, "DatabaseRegion"),
    RouteGeneration: uint64(payload.RouteGeneration, "RouteGeneration"),
    ReconnectRequired: boolean(payload.ReconnectRequired, "ReconnectRequired"),
  });
}

function ping(value: unknown): SyncPing {
  const payload = object(value, "Payload");
  exactKeys(payload, ["Nonce", "SentAt"], "Payload");
  const sentAt = limitedString(payload.SentAt, 64, "SentAt");
  if (!Number.isFinite(Date.parse(sentAt))) {
    throw new TypeError("SentAt must be an ISO-8601 timestamp.");
  }
  return Object.freeze({ Nonce: uint64(payload.Nonce, "Nonce"), SentAt: sentAt });
}

function fatalProtocolError(value: unknown): FatalProtocolError {
  const payload = object(value, "Payload");
  exactKeys(payload, ["Error", "ResumeVersion"], "Payload", ["ResumeVersion"]);
  const error = object(payload.Error, "Error");
  exactKeys(error, ["Code", "Message"], "Error");
  const parsed = {
    Error: Object.freeze({
      Code: errorCode(error.Code, "Error.Code"),
      Message: limitedString(error.Message, 4 * 1024, "Error.Message"),
    }),
  };
  return Object.freeze(payload.ResumeVersion === undefined
    ? parsed
    : { ...parsed, ResumeVersion: stateVersion(payload.ResumeVersion, "ResumeVersion") });
}

function stateVersion(value: unknown, path: string): SyncStateVersion {
  const version = object(value, path);
  exactKeys(version, ["QuerySetVersion", "DatabaseRevision", "IdentityVersion"], path);
  return Object.freeze({
    QuerySetVersion: uint64(version.QuerySetVersion, `${path}.QuerySetVersion`),
    DatabaseRevision: revision(version.DatabaseRevision, `${path}.DatabaseRevision`),
    IdentityVersion: uint64(version.IdentityVersion, `${path}.IdentityVersion`),
  });
}

function revision(value: unknown, path: string): DatabaseRevision {
  const databaseRevision = object(value, path);
  exactKeys(databaseRevision, ["RevisionEpoch", "CommitSequence"], path);
  return Object.freeze({
    RevisionEpoch: uint64(databaseRevision.RevisionEpoch, `${path}.RevisionEpoch`),
    CommitSequence: uint64(databaseRevision.CommitSequence, `${path}.CommitSequence`),
  });
}

function queryUpdated(value: unknown, index: number): QueryUpdated {
  const path = `QueryUpdated[${index}]`;
  const query = object(value, path);
  exactKeys(
    query,
    [
      "QueryId",
      "QueryFingerprint",
      "Result",
      "ResultHash",
      "ExecutionRevision",
      "ValidatedThroughRevision",
      "Journal",
    ],
    path,
  );
  const parsed = {
    QueryId: ulid(query.QueryId, `${path}.QueryId`),
    QueryFingerprint: hash(query.QueryFingerprint, `${path}.QueryFingerprint`),
    Result: json(query.Result, `${path}.Result`),
    ResultHash: hash(query.ResultHash, `${path}.ResultHash`),
    ExecutionRevision: revision(query.ExecutionRevision, `${path}.ExecutionRevision`),
    ValidatedThroughRevision: revision(
      query.ValidatedThroughRevision,
      `${path}.ValidatedThroughRevision`,
    ),
  };
  return Object.freeze(query.Journal === undefined
    ? parsed
    : { ...parsed, Journal: json(query.Journal, `${path}.Journal`) });
}

function queryFailed(value: unknown, index: number): QueryFailed {
  const path = `QueryFailed[${index}]`;
  const query = object(value, path);
  exactKeys(query, ["QueryId", "Error", "ValidatedThroughRevision"], path);
  return Object.freeze({
    QueryId: ulid(query.QueryId, `${path}.QueryId`),
    Error: syncError(query.Error, `${path}.Error`),
    ValidatedThroughRevision: revision(
      query.ValidatedThroughRevision,
      `${path}.ValidatedThroughRevision`,
    ),
  });
}

function queryRemoved(value: unknown, index: number): QueryRemoved {
  const path = `QueryRemoved[${index}]`;
  const query = object(value, path);
  exactKeys(query, ["QueryId"], path);
  return Object.freeze({ QueryId: ulid(query.QueryId, `${path}.QueryId`) });
}

function serverTiming(value: unknown): SyncServerTiming {
  const timing = object(value, "ServerTiming");
  exactKeys(
    timing,
    ["QueryMilliseconds", "ValidationMilliseconds", "SerializationMilliseconds"],
    "ServerTiming",
  );
  return Object.freeze({
    QueryMilliseconds: milliseconds(timing.QueryMilliseconds, "QueryMilliseconds"),
    ValidationMilliseconds: milliseconds(
      timing.ValidationMilliseconds,
      "ValidationMilliseconds",
    ),
    SerializationMilliseconds: milliseconds(
      timing.SerializationMilliseconds,
      "SerializationMilliseconds",
    ),
  });
}

function object(value: unknown, path: string): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new TypeError(`${path} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function array(value: unknown, path: string): readonly unknown[] {
  if (!Array.isArray(value)) {
    throw new TypeError(`${path} must be an array.`);
  }
  return value;
}

function exactKeys(
  value: Record<string, unknown>,
  allowed: readonly string[],
  path: string,
  optional: readonly string[] = ["Journal", "ServerTiming"],
): void {
  const allowedKeys = new Set(allowed);
  for (const key of Object.keys(value)) {
    if (!allowedKeys.has(key)) {
      throw new TypeError(`${path} contains unsupported member '${key}'.`);
    }
  }
  for (const key of allowed) {
    if (!optional.includes(key) && !(key in value)) {
      throw new TypeError(`${path} is missing required member '${key}'.`);
    }
  }
}

function uint64(value: unknown, path: string): SyncUInt64 {
  if (typeof value !== "string" || !/^(?:0|[1-9][0-9]{0,19})$/.test(value)) {
    throw new TypeError(`${path} must be a canonical UInt64 decimal string.`);
  }
  const parsed = BigInt(value);
  if (parsed > 18_446_744_073_709_551_615n) {
    throw new RangeError(`${path} exceeds UInt64.`);
  }
  return value as SyncUInt64;
}

function ulid(value: unknown, path: string): string {
  if (typeof value !== "string" || !/^[0-9A-HJKMNP-TV-Z]{26}$/.test(value)) {
    throw new TypeError(`${path} must be an uppercase ULID.`);
  }
  return value;
}

function hash(value: unknown, path: string): string {
  if (typeof value !== "string" || !/^[0-9a-f]{64}$/.test(value)) {
    throw new TypeError(`${path} must be a lowercase SHA-256 value.`);
  }
  return value;
}

function region(value: unknown, path: string): string {
  if (typeof value !== "string" || !/^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$/.test(value)) {
    throw new TypeError(`${path} must be a valid region code.`);
  }
  return value;
}

function limitedString(value: unknown, maximumLength: number, path: string): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximumLength) {
    throw new TypeError(`${path} has an invalid length.`);
  }
  return value;
}

function boolean(value: unknown, path: string): boolean {
  if (typeof value !== "boolean") {
    throw new TypeError(`${path} must be a boolean.`);
  }
  return value;
}

function milliseconds(value: unknown, path: string): number {
  return integer(value, 0, 60_000, path);
}

function integer(value: unknown, minimum: number, maximum: number, path: string): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < minimum || value > maximum) {
    throw new TypeError(`${path} must be an integer from ${minimum} through ${maximum}.`);
  }
  return value;
}

function syncError(value: unknown, path: string): SyncError {
  const error = object(value, path);
  exactKeys(error, ["Code", "Message", "Retryable"], path);
  return Object.freeze({
    Code: errorCode(error.Code, `${path}.Code`),
    Message: limitedString(error.Message, 4 * 1024, `${path}.Message`),
    Retryable: boolean(error.Retryable, `${path}.Retryable`),
  });
}

function errorCode(value: unknown, path: string): string {
  const code = limitedString(value, 128, path);
  if (!/^[A-Za-z0-9._-]+$/.test(code)) {
    throw new TypeError(`${path} contains unsupported characters.`);
  }
  return code;
}

function base64(value: unknown, path: string): string {
  if (
    typeof value !== "string"
    || value.length === 0
    || value.length > Math.ceil(256 * 1024 / 3) * 4
    || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)
  ) {
    throw new TypeError(`${path} must be canonical bounded base64.`);
  }
  return value;
}

function base64DecodedLength(value: string): number {
  return value.length / 4 * 3
    - (value.endsWith("==") ? 2 : value.endsWith("=") ? 1 : 0);
}

function json(value: unknown, path: string): JsonValue {
  if (
    value === null
    || typeof value === "boolean"
    || typeof value === "string"
    || typeof value === "number" && Number.isFinite(value)
  ) {
    return value as JsonValue;
  }
  if (Array.isArray(value)) {
    return Object.freeze(value.map((item, index) => json(item, `${path}[${index}]`)));
  }
  const source = object(value, path);
  const result = Object.create(null) as Record<string, JsonValue>;
  for (const key of Object.keys(source)) {
    result[key] = json(source[key], `${path}.${key}`);
  }
  return Object.freeze(result);
}

function compareUInt64(left: SyncUInt64, right: SyncUInt64): number {
  const leftValue = BigInt(left);
  const rightValue = BigInt(right);
  return leftValue < rightValue ? -1 : leftValue > rightValue ? 1 : 0;
}

function uniqueQueryIds(
  updated: readonly QueryUpdated[],
  failed: readonly QueryFailed[],
  removed: readonly QueryRemoved[],
): void {
  const queryIds = new Set<string>();
  for (const query of [...updated, ...failed, ...removed]) {
    if (queryIds.has(query.QueryId)) {
      throw new TypeError(`Query '${query.QueryId}' occurs more than once in a transition.`);
    }
    queryIds.add(query.QueryId);
  }
}
