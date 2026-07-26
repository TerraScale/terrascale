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
    Payload: transition(envelope.Payload),
  });
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
  const error = object(query.Error, `${path}.Error`);
  exactKeys(error, ["Code", "Message", "Retryable"], `${path}.Error`);
  return Object.freeze({
    QueryId: ulid(query.QueryId, `${path}.QueryId`),
    Error: Object.freeze({
      Code: limitedString(error.Code, 128, `${path}.Error.Code`),
      Message: limitedString(error.Message, 4 * 1024, `${path}.Error.Message`),
      Retryable: boolean(error.Retryable, `${path}.Error.Retryable`),
    }),
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

function exactKeys(value: Record<string, unknown>, allowed: readonly string[], path: string): void {
  const allowedKeys = new Set(allowed);
  for (const key of Object.keys(value)) {
    if (!allowedKeys.has(key)) {
      throw new TypeError(`${path} contains unsupported member '${key}'.`);
    }
  }
  for (const key of allowed) {
    if (key !== "Journal" && key !== "ServerTiming" && !(key in value)) {
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
  if (!Number.isInteger(value) || typeof value !== "number" || value < 0 || value > 60_000) {
    throw new TypeError(`${path} must be an integer from 0 through 60000.`);
  }
  return value;
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
