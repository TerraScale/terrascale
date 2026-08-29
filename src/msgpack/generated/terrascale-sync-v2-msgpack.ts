// GENERATED FILE - DO NOT EDIT.
//
// Source of authority: src/msgpack/manifest/terrascale.sync.v2.msgpack.json
// Generator: scripts/generate-msgpack-protocol.mjs (ADR 0025: the public
// terrascale.sync.v2.msgpack WebSocket protocol is generated from one
// checked-in versioned manifest; this TypeScript half provides codecs,
// exact-shape validators, and public types for @terrascale/sdk).
//
// manifest sha256:   8bc2ab6f5d3f9fa5a77df1c48f4ece2462c9da9ef0ba76027c608c2c6980d523
// generator sha256:  66d0ac80bd46901fb1c78f3338dd5cff60d4288a744ec63a1944a6356430b077
//
// Regenerate: node scripts/generate-msgpack-protocol.mjs
// Drift gate:  node scripts/generate-msgpack-protocol.mjs --check
// Drift test:  node --test test/msgpack-manifest-drift.test.mjs

import {
  MsgpackReader,
  MsgpackWriter,
  TerrascaleMsgpackError,
  msgpackTextIsScalar,
  msgpackUtf8ByteLength,
  scanMessagePack,
  type TerrascaleMsgpackScanLimits,
} from "../runtime/bounded-msgpack.js";

function msgpackInvalid(path: string): TerrascaleMsgpackError {
  return new TerrascaleMsgpackError("invalid_shape", `${path} does not match the manifest shape`);
}

function msgpackShape(path: string): TerrascaleMsgpackError {
  return new TerrascaleMsgpackError("invalid_shape", `${path} does not carry its declared shape`);
}

function msgpackIsU32(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= 4294967295;
}

function msgpackIsU64(value: unknown): value is bigint {
  return typeof value === "bigint" && value >= 0n && value <= 18446744073709551615n;
}

function msgpackIsI64(value: unknown): value is bigint {
  return typeof value === "bigint" && value >= -9223372036854775808n && value <= 9223372036854775807n;
}

function msgpackIsF64(value: unknown): value is number {
  return typeof value === "number";
}

function msgpackIsBool(value: unknown): value is boolean {
  return typeof value === "boolean";
}

function msgpackIsText(value: unknown, maxBytes: number): value is string {
  return typeof value === "string" && msgpackTextIsScalar(value) && msgpackUtf8ByteLength(value) <= maxBytes;
}

function msgpackIsBinary(value: unknown, minBytes: number, maxBytes: number): value is Uint8Array {
  return value instanceof Uint8Array && value.length >= minBytes && value.length <= maxBytes;
}

/** The wire identity of the generated protocol. */
export const terrascaleSyncV2MsgpackProtocol = "terrascale.sync.v2.msgpack" as const;

/** The manifest revision this file was generated from. */
export const terrascaleSyncV2MsgpackManifestVersion = 1 as const;

/** The SHA-256 of the manifest file this file was generated from. */
export const terrascaleSyncV2MsgpackManifestSha256 = "8bc2ab6f5d3f9fa5a77df1c48f4ece2462c9da9ef0ba76027c608c2c6980d523" as const;

/** The protocol major this build speaks; anything else fails closed. */
export const terrascaleSyncV2MsgpackMajorVersion = 2 as const;

/** The protocol minor this build speaks; anything else fails closed. */
export const terrascaleSyncV2MsgpackMinorVersion = 0 as const;

/** The required feature bits this build understands; unknown bits fail closed. */
export const terrascaleSyncV2MsgpackSupportedFeatures = 0 as const;

/** The whole-message ceiling (bytes), enforced before any byte parses. */
export const terrascaleSyncV2MsgpackMaxMessageBytes = 8388608 as const;

/** The allocation-bounded structural scan limits, generated from the manifest. */
export const terrascaleSyncV2MsgpackScanLimits: Readonly<TerrascaleMsgpackScanLimits> = Object.freeze({
  maxMessageBytes: 8388608,
  maxDepth: 64,
  maxStringBytes: 16384,
  maxBinaryBytes: 4194304,
  maxArrayItems: 1024,
  maxMapEntries: 0,
  extensionsAllowed: false,
  float32Allowed: false,
});

/** The direction one message may travel. */
export type TerrascaleSyncV2MsgpackMessageDirection = "client_to_server" | "server_to_client" | "bidirectional";

/** The message kind names, in manifest order. */
export type TerrascaleSyncV2MsgpackMessageKindName =
  | "session_setup"
  | "session_accepted"
  | "subscription_modify"
  | "subscription_changed"
  | "checkpoint_ack"
  | "heartbeat"
  | "delta_batch"
  | "snapshot_answer"
  | "trigger"
  | "resync_required"
  | "region_route_change"
  | "history_expired"
  | "error"
  | "mutation_request"
  | "mutation_ack"
  | "causal_token"
  | "auth_generation_changed"
  | "operation_progress"
  | "backpressure";

/** The manifest message tags; tags are stable and never reused. */
export const terrascaleSyncV2MsgpackMessageTags: Readonly<Record<TerrascaleSyncV2MsgpackMessageKindName, number>> = Object.freeze({
  session_setup: 1,
  session_accepted: 2,
  subscription_modify: 3,
  subscription_changed: 4,
  checkpoint_ack: 5,
  heartbeat: 6,
  delta_batch: 7,
  snapshot_answer: 8,
  trigger: 9,
  resync_required: 10,
  region_route_change: 11,
  history_expired: 12,
  error: 13,
  mutation_request: 14,
  mutation_ack: 15,
  causal_token: 16,
  auth_generation_changed: 17,
  operation_progress: 18,
  backpressure: 19,
});

/** The reverse tag table; unknown tags fail closed on decode. */
export const terrascaleSyncV2MsgpackMessageNames: Readonly<Record<number, TerrascaleSyncV2MsgpackMessageKindName>> = Object.freeze({
  1: "session_setup",
  2: "session_accepted",
  3: "subscription_modify",
  4: "subscription_changed",
  5: "checkpoint_ack",
  6: "heartbeat",
  7: "delta_batch",
  8: "snapshot_answer",
  9: "trigger",
  10: "resync_required",
  11: "region_route_change",
  12: "history_expired",
  13: "error",
  14: "mutation_request",
  15: "mutation_ack",
  16: "causal_token",
  17: "auth_generation_changed",
  18: "operation_progress",
  19: "backpressure",
});

/** The manifest-recorded travel direction of each message. */
export const terrascaleSyncV2MsgpackMessageDirections: Readonly<Record<TerrascaleSyncV2MsgpackMessageKindName, TerrascaleSyncV2MsgpackMessageDirection>> = Object.freeze({
  session_setup: "client_to_server",
  session_accepted: "server_to_client",
  subscription_modify: "client_to_server",
  subscription_changed: "server_to_client",
  checkpoint_ack: "client_to_server",
  heartbeat: "bidirectional",
  delta_batch: "server_to_client",
  snapshot_answer: "server_to_client",
  trigger: "server_to_client",
  resync_required: "server_to_client",
  region_route_change: "server_to_client",
  history_expired: "server_to_client",
  error: "server_to_client",
  mutation_request: "client_to_server",
  mutation_ack: "server_to_client",
  causal_token: "server_to_client",
  auth_generation_changed: "server_to_client",
  operation_progress: "server_to_client",
  backpressure: "server_to_client",
});

/** One resumption checkpoint: version-vector end pairs in canonical ascending peer order plus the storage sequence absorbed (the engine corpus SyncCheckpoint). (indexed array of 2 fields) */
export type TerrascaleSyncV2MsgpackCheckpoint = {
  readonly ends: ReadonlyArray<readonly [bigint, bigint]>;
  readonly storage_sequence: bigint;
};

/** The stable closed error vocabulary the engine corpus records. */
export type TerrascaleSyncV2MsgpackErrorCodeName =
  | "unsupported_version"
  | "malformed_frame"
  | "checkpoint_refused"
  | "scope_refused"
  | "session_closed"
  | "overloaded";

/** Why a resync was demanded. */
export type TerrascaleSyncV2MsgpackResyncReasonName =
  | "behind_retained_floor"
  | "history_compacted";

/** The native payload codec identities; the numeric values are the engine corpus wire identities (3 = native delta, 4 = native snapshot). */
export type TerrascaleSyncV2MsgpackPayloadCodecName =
  | "terrabase_native_1"
  | "terrabase_snapshot_1";

/** One scalar document value of a mutation write; the closed value domain of the engine corpus. (tagged indexed array: [discriminant, fields...]) */
export type TerrascaleSyncV2MsgpackDocumentValue =
  | { readonly kind: "null_v"; }
  | { readonly kind: "bool_v"; readonly value: boolean; }
  | { readonly kind: "int_v"; readonly value: bigint; }
  | { readonly kind: "float_v"; readonly value: number; }
  | { readonly kind: "text_v"; readonly value: string; }
;

/** One named field of a document put. (indexed array of 2 fields) */
export type TerrascaleSyncV2MsgpackMutationField = {
  readonly key: string;
  readonly value: TerrascaleSyncV2MsgpackDocumentValue;
};

/** The typed write of one mutation request. (tagged indexed array: [discriminant, fields...]) */
export type TerrascaleSyncV2MsgpackDocumentWrite =
  | { readonly kind: "put"; readonly fields: ReadonlyArray<TerrascaleSyncV2MsgpackMutationField>; }
  | { readonly kind: "delete"; }
;

/** The authoritative outcome of one correlated mutation. (tagged indexed array: [discriminant, fields...]) */
export type TerrascaleSyncV2MsgpackMutationOutcome =
  | { readonly kind: "applied"; readonly storage_sequence: bigint; readonly operation_id: string; }
  | { readonly kind: "rejected"; readonly reason: TerrascaleSyncV2MsgpackErrorCodeName; }
;

/** The phase of one durable long-running operation. */
export type TerrascaleSyncV2MsgpackOperationPhaseName =
  | "queued"
  | "running"
  | "completed"
  | "failed"
  | "cancelled";

/** The server's backpressure instruction to the connection. */
export type TerrascaleSyncV2MsgpackBackpressureModeName =
  | "pause"
  | "resume";

/** One public terrascale.sync.v2.msgpack message body (the tagged member of the envelope). */
export type TerrascaleSyncV2MsgpackMessage =
  | { readonly kind: "session_setup"; readonly collections: ReadonlyArray<string>; readonly resume: TerrascaleSyncV2MsgpackCheckpoint | null; }
  | { readonly kind: "session_accepted"; readonly session: bigint; readonly server_checkpoint: TerrascaleSyncV2MsgpackCheckpoint; readonly collections: ReadonlyArray<string>; }
  | { readonly kind: "subscription_modify"; readonly add: ReadonlyArray<string>; readonly remove: ReadonlyArray<string>; }
  | { readonly kind: "subscription_changed"; readonly active: ReadonlyArray<string>; }
  | { readonly kind: "checkpoint_ack"; readonly checkpoint: TerrascaleSyncV2MsgpackCheckpoint; }
  | { readonly kind: "heartbeat"; }
  | { readonly kind: "delta_batch"; readonly codec: TerrascaleSyncV2MsgpackPayloadCodecName; readonly payload: Uint8Array; readonly next: TerrascaleSyncV2MsgpackCheckpoint; readonly within_budget: boolean; readonly single_commit_oversize: boolean; readonly commits: bigint; }
  | { readonly kind: "snapshot_answer"; readonly codec: TerrascaleSyncV2MsgpackPayloadCodecName; readonly payload: Uint8Array; readonly checkpoint: TerrascaleSyncV2MsgpackCheckpoint; }
  | { readonly kind: "trigger"; readonly collection: string; readonly storage_sequence: bigint; readonly operation_id: string; }
  | { readonly kind: "resync_required"; readonly floor: TerrascaleSyncV2MsgpackCheckpoint; readonly reason: TerrascaleSyncV2MsgpackResyncReasonName; }
  | { readonly kind: "region_route_change"; readonly region: string; }
  | { readonly kind: "history_expired"; readonly floor: TerrascaleSyncV2MsgpackCheckpoint; }
  | { readonly kind: "error"; readonly code: TerrascaleSyncV2MsgpackErrorCodeName; readonly detail: string | null; }
  | { readonly kind: "mutation_request"; readonly collection: string; readonly document: string; readonly idempotency: string; readonly write: TerrascaleSyncV2MsgpackDocumentWrite; }
  | { readonly kind: "mutation_ack"; readonly outcome: TerrascaleSyncV2MsgpackMutationOutcome; }
  | { readonly kind: "causal_token"; readonly token: Uint8Array; readonly sequence: bigint; }
  | { readonly kind: "auth_generation_changed"; readonly generation: bigint; }
  | { readonly kind: "operation_progress"; readonly operation: Uint8Array; readonly phase: TerrascaleSyncV2MsgpackOperationPhaseName; readonly done: bigint; readonly total: bigint | null; readonly detail: string | null; }
  | { readonly kind: "backpressure"; readonly mode: TerrascaleSyncV2MsgpackBackpressureModeName; }
;

/** One sealed protocol message: the envelope identities plus the tagged body. */
export type TerrascaleSyncV2MsgpackEnvelope = {
  readonly features: number;
  readonly correlation: bigint;
  readonly causation: bigint;
  readonly message: TerrascaleSyncV2MsgpackMessage;
};

const terrascaleSyncV2MsgpackErrorCodeCodes: Readonly<Record<TerrascaleSyncV2MsgpackErrorCodeName, number>> = Object.freeze({
  unsupported_version: 1,
  malformed_frame: 2,
  checkpoint_refused: 3,
  scope_refused: 4,
  session_closed: 5,
  overloaded: 6,
});

const terrascaleSyncV2MsgpackErrorCodeNames: Readonly<Record<number, TerrascaleSyncV2MsgpackErrorCodeName>> = Object.freeze({
  1: "unsupported_version",
  2: "malformed_frame",
  3: "checkpoint_refused",
  4: "scope_refused",
  5: "session_closed",
  6: "overloaded",
});

function msgpackIsErrorCode(value: unknown): value is TerrascaleSyncV2MsgpackErrorCodeName {
  if (value === "unsupported_version") { return true; }
  if (value === "malformed_frame") { return true; }
  if (value === "checkpoint_refused") { return true; }
  if (value === "scope_refused") { return true; }
  if (value === "session_closed") { return true; }
  if (value === "overloaded") { return true; }
  return false;
}

const terrascaleSyncV2MsgpackResyncReasonCodes: Readonly<Record<TerrascaleSyncV2MsgpackResyncReasonName, number>> = Object.freeze({
  behind_retained_floor: 1,
  history_compacted: 2,
});

const terrascaleSyncV2MsgpackResyncReasonNames: Readonly<Record<number, TerrascaleSyncV2MsgpackResyncReasonName>> = Object.freeze({
  1: "behind_retained_floor",
  2: "history_compacted",
});

function msgpackIsResyncReason(value: unknown): value is TerrascaleSyncV2MsgpackResyncReasonName {
  if (value === "behind_retained_floor") { return true; }
  if (value === "history_compacted") { return true; }
  return false;
}

const terrascaleSyncV2MsgpackPayloadCodecCodes: Readonly<Record<TerrascaleSyncV2MsgpackPayloadCodecName, number>> = Object.freeze({
  terrabase_native_1: 3,
  terrabase_snapshot_1: 4,
});

const terrascaleSyncV2MsgpackPayloadCodecNames: Readonly<Record<number, TerrascaleSyncV2MsgpackPayloadCodecName>> = Object.freeze({
  3: "terrabase_native_1",
  4: "terrabase_snapshot_1",
});

function msgpackIsPayloadCodec(value: unknown): value is TerrascaleSyncV2MsgpackPayloadCodecName {
  if (value === "terrabase_native_1") { return true; }
  if (value === "terrabase_snapshot_1") { return true; }
  return false;
}

const terrascaleSyncV2MsgpackOperationPhaseCodes: Readonly<Record<TerrascaleSyncV2MsgpackOperationPhaseName, number>> = Object.freeze({
  queued: 1,
  running: 2,
  completed: 3,
  failed: 4,
  cancelled: 5,
});

const terrascaleSyncV2MsgpackOperationPhaseNames: Readonly<Record<number, TerrascaleSyncV2MsgpackOperationPhaseName>> = Object.freeze({
  1: "queued",
  2: "running",
  3: "completed",
  4: "failed",
  5: "cancelled",
});

function msgpackIsOperationPhase(value: unknown): value is TerrascaleSyncV2MsgpackOperationPhaseName {
  if (value === "queued") { return true; }
  if (value === "running") { return true; }
  if (value === "completed") { return true; }
  if (value === "failed") { return true; }
  if (value === "cancelled") { return true; }
  return false;
}

const terrascaleSyncV2MsgpackBackpressureModeCodes: Readonly<Record<TerrascaleSyncV2MsgpackBackpressureModeName, number>> = Object.freeze({
  pause: 1,
  resume: 2,
});

const terrascaleSyncV2MsgpackBackpressureModeNames: Readonly<Record<number, TerrascaleSyncV2MsgpackBackpressureModeName>> = Object.freeze({
  1: "pause",
  2: "resume",
});

function msgpackIsBackpressureMode(value: unknown): value is TerrascaleSyncV2MsgpackBackpressureModeName {
  if (value === "pause") { return true; }
  if (value === "resume") { return true; }
  return false;
}

/** The exact-shape validator for the checkpoint structured type. */
export function validateTerrascaleSyncV2MsgpackCheckpoint(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null
    || !("ends" in candidate)
    || !("storage_sequence" in candidate)
  ) {
    throw msgpackInvalid("checkpoint");
  }
  if (Object.keys(candidate).length !== 2) {
    throw msgpackInvalid("checkpoint");
  }
  const ends = (candidate as { ends: unknown }).ends;
  if (!Array.isArray(ends) || ends.length > 64) {
    throw msgpackInvalid("checkpoint.ends");
  }
  for (const element of ends) {
    if (!Array.isArray(element) || element.length !== 2) {
      throw msgpackInvalid("checkpoint.ends[]");
    }
    if (!msgpackIsU64(element[0])) {
      throw msgpackInvalid("checkpoint.ends[][0]");
    }
    if (!msgpackIsU64(element[1])) {
      throw msgpackInvalid("checkpoint.ends[][1]");
    }
  }
  const storage_sequence = (candidate as { storage_sequence: unknown }).storage_sequence;
  if (!msgpackIsU64(storage_sequence)) {
    throw msgpackInvalid("checkpoint.storage_sequence");
  }
}

/** The exact-shape validator for the document_value union. */
export function validateTerrascaleSyncV2MsgpackDocumentValue(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {
    throw msgpackInvalid("document_value");
  }
  const kind = (candidate as { kind: unknown }).kind;
  if (kind === "null_v") {
    if (Object.keys(candidate).length !== 1) {
      throw msgpackInvalid("document_value.null_v");
    }
    return;
  }
  if (kind === "bool_v") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("document_value.bool_v");
    }
    if (!("value" in candidate)) {
      throw msgpackInvalid("document_value.bool_v");
    }
    const value = (candidate as { value: unknown }).value;
    if (!msgpackIsBool(value)) {
      throw msgpackInvalid("document_value.bool_v.value");
    }
    return;
  }
  if (kind === "int_v") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("document_value.int_v");
    }
    if (!("value" in candidate)) {
      throw msgpackInvalid("document_value.int_v");
    }
    const value = (candidate as { value: unknown }).value;
    if (!msgpackIsI64(value)) {
      throw msgpackInvalid("document_value.int_v.value");
    }
    return;
  }
  if (kind === "float_v") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("document_value.float_v");
    }
    if (!("value" in candidate)) {
      throw msgpackInvalid("document_value.float_v");
    }
    const value = (candidate as { value: unknown }).value;
    if (!msgpackIsF64(value)) {
      throw msgpackInvalid("document_value.float_v.value");
    }
    return;
  }
  if (kind === "text_v") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("document_value.text_v");
    }
    if (!("value" in candidate)) {
      throw msgpackInvalid("document_value.text_v");
    }
    const value = (candidate as { value: unknown }).value;
    if (!msgpackIsText(value, 256)) {
      throw msgpackInvalid("document_value.text_v.value");
    }
    return;
  }
  throw msgpackInvalid("document_value.kind");
}

/** The exact-shape validator for the mutation_field structured type. */
export function validateTerrascaleSyncV2MsgpackMutationField(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null
    || !("key" in candidate)
    || !("value" in candidate)
  ) {
    throw msgpackInvalid("mutation_field");
  }
  if (Object.keys(candidate).length !== 2) {
    throw msgpackInvalid("mutation_field");
  }
  const key = (candidate as { key: unknown }).key;
  if (!msgpackIsText(key, 256)) {
    throw msgpackInvalid("mutation_field.key");
  }
  const value = (candidate as { value: unknown }).value;
  validateTerrascaleSyncV2MsgpackDocumentValue(value);
}

/** The exact-shape validator for the document_write union. */
export function validateTerrascaleSyncV2MsgpackDocumentWrite(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {
    throw msgpackInvalid("document_write");
  }
  const kind = (candidate as { kind: unknown }).kind;
  if (kind === "put") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("document_write.put");
    }
    if (!("fields" in candidate)) {
      throw msgpackInvalid("document_write.put");
    }
    const fields = (candidate as { fields: unknown }).fields;
    if (!Array.isArray(fields) || fields.length > 1024) {
      throw msgpackInvalid("document_write.put.fields");
    }
    for (const element of fields) {
      validateTerrascaleSyncV2MsgpackMutationField(element);
    }
    return;
  }
  if (kind === "delete") {
    if (Object.keys(candidate).length !== 1) {
      throw msgpackInvalid("document_write.delete");
    }
    return;
  }
  throw msgpackInvalid("document_write.kind");
}

/** The exact-shape validator for the mutation_outcome union. */
export function validateTerrascaleSyncV2MsgpackMutationOutcome(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {
    throw msgpackInvalid("mutation_outcome");
  }
  const kind = (candidate as { kind: unknown }).kind;
  if (kind === "applied") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("mutation_outcome.applied");
    }
    if (!("storage_sequence" in candidate)) {
      throw msgpackInvalid("mutation_outcome.applied");
    }
    const storage_sequence = (candidate as { storage_sequence: unknown }).storage_sequence;
    if (!msgpackIsU64(storage_sequence)) {
      throw msgpackInvalid("mutation_outcome.applied.storage_sequence");
    }
    if (!("operation_id" in candidate)) {
      throw msgpackInvalid("mutation_outcome.applied");
    }
    const operation_id = (candidate as { operation_id: unknown }).operation_id;
    if (!msgpackIsText(operation_id, 128)) {
      throw msgpackInvalid("mutation_outcome.applied.operation_id");
    }
    return;
  }
  if (kind === "rejected") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("mutation_outcome.rejected");
    }
    if (!("reason" in candidate)) {
      throw msgpackInvalid("mutation_outcome.rejected");
    }
    const reason = (candidate as { reason: unknown }).reason;
    if (!msgpackIsErrorCode(reason)) {
      throw msgpackInvalid("mutation_outcome.rejected.reason");
    }
    return;
  }
  throw msgpackInvalid("mutation_outcome.kind");
}

/** The exact-shape validator for one public message body (untrusted in-memory values too). */
export function validateTerrascaleSyncV2MsgpackMessage(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {
    throw msgpackInvalid("message");
  }
  const kind = (candidate as { kind: unknown }).kind;
  if (kind === "session_setup") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("session_setup");
    }
    if (!("collections" in candidate)) {
      throw msgpackInvalid("session_setup");
    }
    const collections = (candidate as { collections: unknown }).collections;
    if (!Array.isArray(collections) || collections.length > 128) {
      throw msgpackInvalid("session_setup.collections");
    }
    for (const element of collections) {
      if (!msgpackIsText(element, 256)) {
        throw msgpackInvalid("session_setup.collections[]");
      }
    }
    if (!("resume" in candidate)) {
      throw msgpackInvalid("session_setup");
    }
    const resume = (candidate as { resume: unknown }).resume;
    if (resume !== null) {
      validateTerrascaleSyncV2MsgpackCheckpoint(resume);
    }
    return;
  }
  if (kind === "session_accepted") {
    if (Object.keys(candidate).length !== 4) {
      throw msgpackInvalid("session_accepted");
    }
    if (!("session" in candidate)) {
      throw msgpackInvalid("session_accepted");
    }
    const session = (candidate as { session: unknown }).session;
    if (!msgpackIsU64(session)) {
      throw msgpackInvalid("session_accepted.session");
    }
    if (!("server_checkpoint" in candidate)) {
      throw msgpackInvalid("session_accepted");
    }
    const server_checkpoint = (candidate as { server_checkpoint: unknown }).server_checkpoint;
    validateTerrascaleSyncV2MsgpackCheckpoint(server_checkpoint);
    if (!("collections" in candidate)) {
      throw msgpackInvalid("session_accepted");
    }
    const collections = (candidate as { collections: unknown }).collections;
    if (!Array.isArray(collections) || collections.length > 128) {
      throw msgpackInvalid("session_accepted.collections");
    }
    for (const element of collections) {
      if (!msgpackIsText(element, 256)) {
        throw msgpackInvalid("session_accepted.collections[]");
      }
    }
    return;
  }
  if (kind === "subscription_modify") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("subscription_modify");
    }
    if (!("add" in candidate)) {
      throw msgpackInvalid("subscription_modify");
    }
    const add = (candidate as { add: unknown }).add;
    if (!Array.isArray(add) || add.length > 128) {
      throw msgpackInvalid("subscription_modify.add");
    }
    for (const element of add) {
      if (!msgpackIsText(element, 256)) {
        throw msgpackInvalid("subscription_modify.add[]");
      }
    }
    if (!("remove" in candidate)) {
      throw msgpackInvalid("subscription_modify");
    }
    const remove = (candidate as { remove: unknown }).remove;
    if (!Array.isArray(remove) || remove.length > 128) {
      throw msgpackInvalid("subscription_modify.remove");
    }
    for (const element of remove) {
      if (!msgpackIsText(element, 256)) {
        throw msgpackInvalid("subscription_modify.remove[]");
      }
    }
    return;
  }
  if (kind === "subscription_changed") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("subscription_changed");
    }
    if (!("active" in candidate)) {
      throw msgpackInvalid("subscription_changed");
    }
    const active = (candidate as { active: unknown }).active;
    if (!Array.isArray(active) || active.length > 128) {
      throw msgpackInvalid("subscription_changed.active");
    }
    for (const element of active) {
      if (!msgpackIsText(element, 256)) {
        throw msgpackInvalid("subscription_changed.active[]");
      }
    }
    return;
  }
  if (kind === "checkpoint_ack") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("checkpoint_ack");
    }
    if (!("checkpoint" in candidate)) {
      throw msgpackInvalid("checkpoint_ack");
    }
    const checkpoint = (candidate as { checkpoint: unknown }).checkpoint;
    validateTerrascaleSyncV2MsgpackCheckpoint(checkpoint);
    return;
  }
  if (kind === "heartbeat") {
    if (Object.keys(candidate).length !== 1) {
      throw msgpackInvalid("heartbeat");
    }
    return;
  }
  if (kind === "delta_batch") {
    if (Object.keys(candidate).length !== 7) {
      throw msgpackInvalid("delta_batch");
    }
    if (!("codec" in candidate)) {
      throw msgpackInvalid("delta_batch");
    }
    const codec = (candidate as { codec: unknown }).codec;
    if (!msgpackIsPayloadCodec(codec)) {
      throw msgpackInvalid("delta_batch.codec");
    }
    if (!("payload" in candidate)) {
      throw msgpackInvalid("delta_batch");
    }
    const payload = (candidate as { payload: unknown }).payload;
    if (!msgpackIsBinary(payload, 0, 4194304)) {
      throw msgpackInvalid("delta_batch.payload");
    }
    if (!("next" in candidate)) {
      throw msgpackInvalid("delta_batch");
    }
    const next = (candidate as { next: unknown }).next;
    validateTerrascaleSyncV2MsgpackCheckpoint(next);
    if (!("within_budget" in candidate)) {
      throw msgpackInvalid("delta_batch");
    }
    const within_budget = (candidate as { within_budget: unknown }).within_budget;
    if (!msgpackIsBool(within_budget)) {
      throw msgpackInvalid("delta_batch.within_budget");
    }
    if (!("single_commit_oversize" in candidate)) {
      throw msgpackInvalid("delta_batch");
    }
    const single_commit_oversize = (candidate as { single_commit_oversize: unknown }).single_commit_oversize;
    if (!msgpackIsBool(single_commit_oversize)) {
      throw msgpackInvalid("delta_batch.single_commit_oversize");
    }
    if (!("commits" in candidate)) {
      throw msgpackInvalid("delta_batch");
    }
    const commits = (candidate as { commits: unknown }).commits;
    if (!msgpackIsU64(commits)) {
      throw msgpackInvalid("delta_batch.commits");
    }
    return;
  }
  if (kind === "snapshot_answer") {
    if (Object.keys(candidate).length !== 4) {
      throw msgpackInvalid("snapshot_answer");
    }
    if (!("codec" in candidate)) {
      throw msgpackInvalid("snapshot_answer");
    }
    const codec = (candidate as { codec: unknown }).codec;
    if (!msgpackIsPayloadCodec(codec)) {
      throw msgpackInvalid("snapshot_answer.codec");
    }
    if (!("payload" in candidate)) {
      throw msgpackInvalid("snapshot_answer");
    }
    const payload = (candidate as { payload: unknown }).payload;
    if (!msgpackIsBinary(payload, 0, 4194304)) {
      throw msgpackInvalid("snapshot_answer.payload");
    }
    if (!("checkpoint" in candidate)) {
      throw msgpackInvalid("snapshot_answer");
    }
    const checkpoint = (candidate as { checkpoint: unknown }).checkpoint;
    validateTerrascaleSyncV2MsgpackCheckpoint(checkpoint);
    return;
  }
  if (kind === "trigger") {
    if (Object.keys(candidate).length !== 4) {
      throw msgpackInvalid("trigger");
    }
    if (!("collection" in candidate)) {
      throw msgpackInvalid("trigger");
    }
    const collection = (candidate as { collection: unknown }).collection;
    if (!msgpackIsText(collection, 64)) {
      throw msgpackInvalid("trigger.collection");
    }
    if (!("storage_sequence" in candidate)) {
      throw msgpackInvalid("trigger");
    }
    const storage_sequence = (candidate as { storage_sequence: unknown }).storage_sequence;
    if (!msgpackIsU64(storage_sequence)) {
      throw msgpackInvalid("trigger.storage_sequence");
    }
    if (!("operation_id" in candidate)) {
      throw msgpackInvalid("trigger");
    }
    const operation_id = (candidate as { operation_id: unknown }).operation_id;
    if (!msgpackIsText(operation_id, 64)) {
      throw msgpackInvalid("trigger.operation_id");
    }
    return;
  }
  if (kind === "resync_required") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("resync_required");
    }
    if (!("floor" in candidate)) {
      throw msgpackInvalid("resync_required");
    }
    const floor = (candidate as { floor: unknown }).floor;
    validateTerrascaleSyncV2MsgpackCheckpoint(floor);
    if (!("reason" in candidate)) {
      throw msgpackInvalid("resync_required");
    }
    const reason = (candidate as { reason: unknown }).reason;
    if (!msgpackIsResyncReason(reason)) {
      throw msgpackInvalid("resync_required.reason");
    }
    return;
  }
  if (kind === "region_route_change") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("region_route_change");
    }
    if (!("region" in candidate)) {
      throw msgpackInvalid("region_route_change");
    }
    const region = (candidate as { region: unknown }).region;
    if (!msgpackIsText(region, 256)) {
      throw msgpackInvalid("region_route_change.region");
    }
    return;
  }
  if (kind === "history_expired") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("history_expired");
    }
    if (!("floor" in candidate)) {
      throw msgpackInvalid("history_expired");
    }
    const floor = (candidate as { floor: unknown }).floor;
    validateTerrascaleSyncV2MsgpackCheckpoint(floor);
    return;
  }
  if (kind === "error") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("error");
    }
    if (!("code" in candidate)) {
      throw msgpackInvalid("error");
    }
    const code = (candidate as { code: unknown }).code;
    if (!msgpackIsErrorCode(code)) {
      throw msgpackInvalid("error.code");
    }
    if (!("detail" in candidate)) {
      throw msgpackInvalid("error");
    }
    const detail = (candidate as { detail: unknown }).detail;
    if (detail !== null) {
      if (!msgpackIsText(detail, 256)) {
        throw msgpackInvalid("error.detail");
      }
    }
    return;
  }
  if (kind === "mutation_request") {
    if (Object.keys(candidate).length !== 5) {
      throw msgpackInvalid("mutation_request");
    }
    if (!("collection" in candidate)) {
      throw msgpackInvalid("mutation_request");
    }
    const collection = (candidate as { collection: unknown }).collection;
    if (!msgpackIsText(collection, 256)) {
      throw msgpackInvalid("mutation_request.collection");
    }
    if (!("document" in candidate)) {
      throw msgpackInvalid("mutation_request");
    }
    const document = (candidate as { document: unknown }).document;
    if (!msgpackIsText(document, 256)) {
      throw msgpackInvalid("mutation_request.document");
    }
    if (!("idempotency" in candidate)) {
      throw msgpackInvalid("mutation_request");
    }
    const idempotency = (candidate as { idempotency: unknown }).idempotency;
    if (!msgpackIsText(idempotency, 128)) {
      throw msgpackInvalid("mutation_request.idempotency");
    }
    if (!("write" in candidate)) {
      throw msgpackInvalid("mutation_request");
    }
    const write = (candidate as { write: unknown }).write;
    validateTerrascaleSyncV2MsgpackDocumentWrite(write);
    return;
  }
  if (kind === "mutation_ack") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("mutation_ack");
    }
    if (!("outcome" in candidate)) {
      throw msgpackInvalid("mutation_ack");
    }
    const outcome = (candidate as { outcome: unknown }).outcome;
    validateTerrascaleSyncV2MsgpackMutationOutcome(outcome);
    return;
  }
  if (kind === "causal_token") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("causal_token");
    }
    if (!("token" in candidate)) {
      throw msgpackInvalid("causal_token");
    }
    const token = (candidate as { token: unknown }).token;
    if (!msgpackIsBinary(token, 0, 16384)) {
      throw msgpackInvalid("causal_token.token");
    }
    if (!("sequence" in candidate)) {
      throw msgpackInvalid("causal_token");
    }
    const sequence = (candidate as { sequence: unknown }).sequence;
    if (!msgpackIsU64(sequence)) {
      throw msgpackInvalid("causal_token.sequence");
    }
    return;
  }
  if (kind === "auth_generation_changed") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("auth_generation_changed");
    }
    if (!("generation" in candidate)) {
      throw msgpackInvalid("auth_generation_changed");
    }
    const generation = (candidate as { generation: unknown }).generation;
    if (!msgpackIsU64(generation)) {
      throw msgpackInvalid("auth_generation_changed.generation");
    }
    return;
  }
  if (kind === "operation_progress") {
    if (Object.keys(candidate).length !== 6) {
      throw msgpackInvalid("operation_progress");
    }
    if (!("operation" in candidate)) {
      throw msgpackInvalid("operation_progress");
    }
    const operation = (candidate as { operation: unknown }).operation;
    if (!msgpackIsBinary(operation, 16, 16)) {
      throw msgpackInvalid("operation_progress.operation");
    }
    if (!("phase" in candidate)) {
      throw msgpackInvalid("operation_progress");
    }
    const phase = (candidate as { phase: unknown }).phase;
    if (!msgpackIsOperationPhase(phase)) {
      throw msgpackInvalid("operation_progress.phase");
    }
    if (!("done" in candidate)) {
      throw msgpackInvalid("operation_progress");
    }
    const done = (candidate as { done: unknown }).done;
    if (!msgpackIsU64(done)) {
      throw msgpackInvalid("operation_progress.done");
    }
    if (!("total" in candidate)) {
      throw msgpackInvalid("operation_progress");
    }
    const total = (candidate as { total: unknown }).total;
    if (total !== null) {
      if (!msgpackIsU64(total)) {
        throw msgpackInvalid("operation_progress.total");
      }
    }
    if (!("detail" in candidate)) {
      throw msgpackInvalid("operation_progress");
    }
    const detail = (candidate as { detail: unknown }).detail;
    if (detail !== null) {
      if (!msgpackIsText(detail, 256)) {
        throw msgpackInvalid("operation_progress.detail");
      }
    }
    return;
  }
  if (kind === "backpressure") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("backpressure");
    }
    if (!("mode" in candidate)) {
      throw msgpackInvalid("backpressure");
    }
    const mode = (candidate as { mode: unknown }).mode;
    if (!msgpackIsBackpressureMode(mode)) {
      throw msgpackInvalid("backpressure.mode");
    }
    return;
  }
  throw msgpackInvalid("message.kind");
}

/** The exact-shape validator for one sealed protocol message. */
export function validateTerrascaleSyncV2MsgpackEnvelope(envelope: unknown): void {
  if (typeof envelope !== "object" || envelope === null
    || !("features" in envelope) || !("correlation" in envelope)
    || !("causation" in envelope) || !("message" in envelope)) {
    throw msgpackInvalid("envelope");
  }
  if (Object.keys(envelope).length !== 4) {
    throw msgpackInvalid("envelope");
  }
  const candidate = envelope as { features: unknown; correlation: unknown; causation: unknown; message: unknown };
  if (!msgpackIsU32(candidate.features)) {
    throw msgpackInvalid("envelope.features");
  }
  if (!msgpackIsU64(candidate.correlation)) {
    throw msgpackInvalid("envelope.correlation");
  }
  if (!msgpackIsU64(candidate.causation)) {
    throw msgpackInvalid("envelope.causation");
  }
  if ((candidate.features & ~terrascaleSyncV2MsgpackSupportedFeatures) !== 0) {
    throw new TerrascaleMsgpackError("unknown_feature", "the envelope requires feature bits this build does not speak");
  }
  validateTerrascaleSyncV2MsgpackMessage(candidate.message);
}

function msgpackEncodeCheckpoint(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackCheckpoint): void {
  writer.arrayHeader(2);
  writer.arrayHeader(value.ends.length);
  for (const element of value.ends) {
    writer.arrayHeader(2);
    writer.u64(element[0]);
    writer.u64(element[1]);
  }
  writer.u64(value.storage_sequence);
}

function msgpackEncodeErrorCode(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackErrorCodeName): void {
  writer.u32(terrascaleSyncV2MsgpackErrorCodeCodes[value]);
}

function msgpackEncodeResyncReason(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackResyncReasonName): void {
  writer.u32(terrascaleSyncV2MsgpackResyncReasonCodes[value]);
}

function msgpackEncodePayloadCodec(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackPayloadCodecName): void {
  writer.u32(terrascaleSyncV2MsgpackPayloadCodecCodes[value]);
}

function msgpackEncodeDocumentValue(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackDocumentValue): void {
  switch (value.kind) {
    case "null_v": {
      writer.arrayHeader(1);
      writer.u32(0);
      break;
    }
    case "bool_v": {
      writer.arrayHeader(2);
      writer.u32(1);
      writer.bool(value.value);
      break;
    }
    case "int_v": {
      writer.arrayHeader(2);
      writer.u32(2);
      writer.i64(value.value);
      break;
    }
    case "float_v": {
      writer.arrayHeader(2);
      writer.u32(3);
      writer.f64(value.value);
      break;
    }
    case "text_v": {
      writer.arrayHeader(2);
      writer.u32(4);
      writer.text(value.value, 256);
      break;
    }
  }
}

function msgpackEncodeMutationField(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackMutationField): void {
  writer.arrayHeader(2);
  writer.text(value.key, 256);
  msgpackEncodeDocumentValue(writer, value.value);
}

function msgpackEncodeDocumentWrite(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackDocumentWrite): void {
  switch (value.kind) {
    case "put": {
      writer.arrayHeader(2);
      writer.u32(1);
      writer.arrayHeader(value.fields.length);
      for (const element of value.fields) {
        msgpackEncodeMutationField(writer, element);
      }
      break;
    }
    case "delete": {
      writer.arrayHeader(1);
      writer.u32(2);
      break;
    }
  }
}

function msgpackEncodeMutationOutcome(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackMutationOutcome): void {
  switch (value.kind) {
    case "applied": {
      writer.arrayHeader(3);
      writer.u32(1);
      writer.u64(value.storage_sequence);
      writer.text(value.operation_id, 128);
      break;
    }
    case "rejected": {
      writer.arrayHeader(2);
      writer.u32(2);
      msgpackEncodeErrorCode(writer, value.reason);
      break;
    }
  }
}

function msgpackEncodeOperationPhase(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackOperationPhaseName): void {
  writer.u32(terrascaleSyncV2MsgpackOperationPhaseCodes[value]);
}

function msgpackEncodeBackpressureMode(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackBackpressureModeName): void {
  writer.u32(terrascaleSyncV2MsgpackBackpressureModeCodes[value]);
}

function msgpackEncodeMessageBody(writer: MsgpackWriter, message: TerrascaleSyncV2MsgpackMessage): void {
  switch (message.kind) {
    case "session_setup": {
      writer.arrayHeader(2);
      writer.arrayHeader(message.collections.length);
      for (const element of message.collections) {
        writer.text(element, 256);
      }
      if (message.resume === null) {
        writer.nil();
      } else {
        msgpackEncodeCheckpoint(writer, message.resume);
      }
      break;
    }
    case "session_accepted": {
      writer.arrayHeader(3);
      writer.u64(message.session);
      msgpackEncodeCheckpoint(writer, message.server_checkpoint);
      writer.arrayHeader(message.collections.length);
      for (const element of message.collections) {
        writer.text(element, 256);
      }
      break;
    }
    case "subscription_modify": {
      writer.arrayHeader(2);
      writer.arrayHeader(message.add.length);
      for (const element of message.add) {
        writer.text(element, 256);
      }
      writer.arrayHeader(message.remove.length);
      for (const element of message.remove) {
        writer.text(element, 256);
      }
      break;
    }
    case "subscription_changed": {
      writer.arrayHeader(1);
      writer.arrayHeader(message.active.length);
      for (const element of message.active) {
        writer.text(element, 256);
      }
      break;
    }
    case "checkpoint_ack": {
      writer.arrayHeader(1);
      msgpackEncodeCheckpoint(writer, message.checkpoint);
      break;
    }
    case "heartbeat": {
      writer.arrayHeader(0);
      break;
    }
    case "delta_batch": {
      writer.arrayHeader(6);
      msgpackEncodePayloadCodec(writer, message.codec);
      writer.binary(message.payload, 0, 4194304);
      msgpackEncodeCheckpoint(writer, message.next);
      writer.bool(message.within_budget);
      writer.bool(message.single_commit_oversize);
      writer.u64(message.commits);
      break;
    }
    case "snapshot_answer": {
      writer.arrayHeader(3);
      msgpackEncodePayloadCodec(writer, message.codec);
      writer.binary(message.payload, 0, 4194304);
      msgpackEncodeCheckpoint(writer, message.checkpoint);
      break;
    }
    case "trigger": {
      writer.arrayHeader(3);
      writer.text(message.collection, 64);
      writer.u64(message.storage_sequence);
      writer.text(message.operation_id, 64);
      break;
    }
    case "resync_required": {
      writer.arrayHeader(2);
      msgpackEncodeCheckpoint(writer, message.floor);
      msgpackEncodeResyncReason(writer, message.reason);
      break;
    }
    case "region_route_change": {
      writer.arrayHeader(1);
      writer.text(message.region, 256);
      break;
    }
    case "history_expired": {
      writer.arrayHeader(1);
      msgpackEncodeCheckpoint(writer, message.floor);
      break;
    }
    case "error": {
      writer.arrayHeader(2);
      msgpackEncodeErrorCode(writer, message.code);
      if (message.detail === null) {
        writer.nil();
      } else {
        writer.text(message.detail, 256);
      }
      break;
    }
    case "mutation_request": {
      writer.arrayHeader(4);
      writer.text(message.collection, 256);
      writer.text(message.document, 256);
      writer.text(message.idempotency, 128);
      msgpackEncodeDocumentWrite(writer, message.write);
      break;
    }
    case "mutation_ack": {
      writer.arrayHeader(1);
      msgpackEncodeMutationOutcome(writer, message.outcome);
      break;
    }
    case "causal_token": {
      writer.arrayHeader(2);
      writer.binary(message.token, 0, 16384);
      writer.u64(message.sequence);
      break;
    }
    case "auth_generation_changed": {
      writer.arrayHeader(1);
      writer.u64(message.generation);
      break;
    }
    case "operation_progress": {
      writer.arrayHeader(5);
      writer.binary(message.operation, 16, 16);
      msgpackEncodeOperationPhase(writer, message.phase);
      writer.u64(message.done);
      if (message.total === null) {
        writer.nil();
      } else {
        writer.u64(message.total);
      }
      if (message.detail === null) {
        writer.nil();
      } else {
        writer.text(message.detail, 256);
      }
      break;
    }
    case "backpressure": {
      writer.arrayHeader(1);
      msgpackEncodeBackpressureMode(writer, message.mode);
      break;
    }
  }
}

/** Encodes one message body as its indexed array (the envelope body element). */
export function encodeTerrascaleSyncV2MsgpackMessage(message: TerrascaleSyncV2MsgpackMessage): Uint8Array {
  validateTerrascaleSyncV2MsgpackMessage(message);
  const writer = new MsgpackWriter();
  msgpackEncodeMessageBody(writer, message);
  return writer.finish(terrascaleSyncV2MsgpackMaxMessageBytes);
}

/** Encodes one complete protocol message (envelope plus body). */
export function encodeTerrascaleSyncV2MsgpackEnvelope(envelope: TerrascaleSyncV2MsgpackEnvelope): Uint8Array {
  validateTerrascaleSyncV2MsgpackEnvelope(envelope);
  const writer = new MsgpackWriter();
  writer.arrayHeader(7);
  writer.u32(terrascaleSyncV2MsgpackMajorVersion);
  writer.u32(terrascaleSyncV2MsgpackMinorVersion);
  writer.u32(envelope.features);
  writer.u32(terrascaleSyncV2MsgpackMessageTags[envelope.message.kind]);
  writer.u64(envelope.correlation);
  writer.u64(envelope.causation);
  msgpackEncodeMessageBody(writer, envelope.message);
  return writer.finish(terrascaleSyncV2MsgpackMaxMessageBytes);
}

function msgpackDecodeCheckpoint(reader: MsgpackReader): TerrascaleSyncV2MsgpackCheckpoint {
  reader.arrayHeader(2);
  const endsCount = reader.arrayCount(64);
  const ends: Array<readonly [bigint, bigint]> = [];
  for (let index = 0; index < endsCount; index++) {
    reader.arrayHeader(2);
    const endsItemPart0 = reader.u64();
    const endsItemPart1 = reader.u64();
    const endsItem: readonly [bigint, bigint] = [endsItemPart0, endsItemPart1];
    ends.push(endsItem);
  }
  const storage_sequence = reader.u64();
  return { ends, storage_sequence };
}

function msgpackDecodeErrorCode(reader: MsgpackReader): TerrascaleSyncV2MsgpackErrorCodeName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackErrorCodeNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `error_code value ${code} is not defined by the manifest`);
  }
  return name;
}

function msgpackDecodeResyncReason(reader: MsgpackReader): TerrascaleSyncV2MsgpackResyncReasonName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackResyncReasonNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `resync_reason value ${code} is not defined by the manifest`);
  }
  return name;
}

function msgpackDecodePayloadCodec(reader: MsgpackReader): TerrascaleSyncV2MsgpackPayloadCodecName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackPayloadCodecNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `payload_codec value ${code} is not defined by the manifest`);
  }
  return name;
}

function msgpackDecodeDocumentValue(reader: MsgpackReader): TerrascaleSyncV2MsgpackDocumentValue {
  const count = reader.arrayCount(2);
  const discriminant = reader.u32();
  if (discriminant === 0) {
    if (count !== 1) {
      throw msgpackShape("document_value.null_v");
    }
    return { kind: "null_v" };
  }
  if (discriminant === 1) {
    if (count !== 2) {
      throw msgpackShape("document_value.bool_v");
    }
      const value = reader.bool();
    return { kind: "bool_v", value };
  }
  if (discriminant === 2) {
    if (count !== 2) {
      throw msgpackShape("document_value.int_v");
    }
      const value = reader.i64();
    return { kind: "int_v", value };
  }
  if (discriminant === 3) {
    if (count !== 2) {
      throw msgpackShape("document_value.float_v");
    }
      const value = reader.f64();
    return { kind: "float_v", value };
  }
  if (discriminant === 4) {
    if (count !== 2) {
      throw msgpackShape("document_value.text_v");
    }
      const value = reader.text(256);
    return { kind: "text_v", value };
  }
  throw new TerrascaleMsgpackError("unknown_variant", `document_value discriminant ${discriminant} is not defined by the manifest`);
}

function msgpackDecodeMutationField(reader: MsgpackReader): TerrascaleSyncV2MsgpackMutationField {
  reader.arrayHeader(2);
  const key = reader.text(256);
  const value = msgpackDecodeDocumentValue(reader);
  return { key, value };
}

function msgpackDecodeDocumentWrite(reader: MsgpackReader): TerrascaleSyncV2MsgpackDocumentWrite {
  const count = reader.arrayCount(2);
  const discriminant = reader.u32();
  if (discriminant === 1) {
    if (count !== 2) {
      throw msgpackShape("document_write.put");
    }
      const fieldsCount = reader.arrayCount(1024);
      const fields: Array<TerrascaleSyncV2MsgpackMutationField> = [];
      for (let index = 0; index < fieldsCount; index++) {
        fields.push(msgpackDecodeMutationField(reader));
      }
    return { kind: "put", fields };
  }
  if (discriminant === 2) {
    if (count !== 1) {
      throw msgpackShape("document_write.delete");
    }
    return { kind: "delete" };
  }
  throw new TerrascaleMsgpackError("unknown_variant", `document_write discriminant ${discriminant} is not defined by the manifest`);
}

function msgpackDecodeMutationOutcome(reader: MsgpackReader): TerrascaleSyncV2MsgpackMutationOutcome {
  const count = reader.arrayCount(3);
  const discriminant = reader.u32();
  if (discriminant === 1) {
    if (count !== 3) {
      throw msgpackShape("mutation_outcome.applied");
    }
      const storage_sequence = reader.u64();
      const operation_id = reader.text(128);
    return { kind: "applied", storage_sequence, operation_id };
  }
  if (discriminant === 2) {
    if (count !== 2) {
      throw msgpackShape("mutation_outcome.rejected");
    }
      const reason = msgpackDecodeErrorCode(reader);
    return { kind: "rejected", reason };
  }
  throw new TerrascaleMsgpackError("unknown_variant", `mutation_outcome discriminant ${discriminant} is not defined by the manifest`);
}

function msgpackDecodeOperationPhase(reader: MsgpackReader): TerrascaleSyncV2MsgpackOperationPhaseName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackOperationPhaseNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `operation_phase value ${code} is not defined by the manifest`);
  }
  return name;
}

function msgpackDecodeBackpressureMode(reader: MsgpackReader): TerrascaleSyncV2MsgpackBackpressureModeName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackBackpressureModeNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `backpressure_mode value ${code} is not defined by the manifest`);
  }
  return name;
}

function msgpackDecodeMessageBody(reader: MsgpackReader, tag: number): TerrascaleSyncV2MsgpackMessage {
  switch (tag) {
    case 1: {
      reader.arrayHeader(2);
      const collectionsCount = reader.arrayCount(128);
      const collections: Array<string> = [];
      for (let index = 0; index < collectionsCount; index++) {
        collections.push(reader.text(256));
      }
      const resume = reader.optionNil() ? null : msgpackDecodeCheckpoint(reader);
      return { kind: "session_setup", collections, resume };
    }
    case 2: {
      reader.arrayHeader(3);
      const session = reader.u64();
      const server_checkpoint = msgpackDecodeCheckpoint(reader);
      const collectionsCount = reader.arrayCount(128);
      const collections: Array<string> = [];
      for (let index = 0; index < collectionsCount; index++) {
        collections.push(reader.text(256));
      }
      return { kind: "session_accepted", session, server_checkpoint, collections };
    }
    case 3: {
      reader.arrayHeader(2);
      const addCount = reader.arrayCount(128);
      const add: Array<string> = [];
      for (let index = 0; index < addCount; index++) {
        add.push(reader.text(256));
      }
      const removeCount = reader.arrayCount(128);
      const remove: Array<string> = [];
      for (let index = 0; index < removeCount; index++) {
        remove.push(reader.text(256));
      }
      return { kind: "subscription_modify", add, remove };
    }
    case 4: {
      reader.arrayHeader(1);
      const activeCount = reader.arrayCount(128);
      const active: Array<string> = [];
      for (let index = 0; index < activeCount; index++) {
        active.push(reader.text(256));
      }
      return { kind: "subscription_changed", active };
    }
    case 5: {
      reader.arrayHeader(1);
      const checkpoint = msgpackDecodeCheckpoint(reader);
      return { kind: "checkpoint_ack", checkpoint };
    }
    case 6: {
      reader.arrayHeader(0);
      return { kind: "heartbeat" };
    }
    case 7: {
      reader.arrayHeader(6);
      const codec = msgpackDecodePayloadCodec(reader);
      const payload = reader.binary(0, 4194304);
      const next = msgpackDecodeCheckpoint(reader);
      const within_budget = reader.bool();
      const single_commit_oversize = reader.bool();
      const commits = reader.u64();
      return { kind: "delta_batch", codec, payload, next, within_budget, single_commit_oversize, commits };
    }
    case 8: {
      reader.arrayHeader(3);
      const codec = msgpackDecodePayloadCodec(reader);
      const payload = reader.binary(0, 4194304);
      const checkpoint = msgpackDecodeCheckpoint(reader);
      return { kind: "snapshot_answer", codec, payload, checkpoint };
    }
    case 9: {
      reader.arrayHeader(3);
      const collection = reader.text(64);
      const storage_sequence = reader.u64();
      const operation_id = reader.text(64);
      return { kind: "trigger", collection, storage_sequence, operation_id };
    }
    case 10: {
      reader.arrayHeader(2);
      const floor = msgpackDecodeCheckpoint(reader);
      const reason = msgpackDecodeResyncReason(reader);
      return { kind: "resync_required", floor, reason };
    }
    case 11: {
      reader.arrayHeader(1);
      const region = reader.text(256);
      return { kind: "region_route_change", region };
    }
    case 12: {
      reader.arrayHeader(1);
      const floor = msgpackDecodeCheckpoint(reader);
      return { kind: "history_expired", floor };
    }
    case 13: {
      reader.arrayHeader(2);
      const code = msgpackDecodeErrorCode(reader);
      const detail = reader.optionNil() ? null : reader.text(256);
      return { kind: "error", code, detail };
    }
    case 14: {
      reader.arrayHeader(4);
      const collection = reader.text(256);
      const document = reader.text(256);
      const idempotency = reader.text(128);
      const write = msgpackDecodeDocumentWrite(reader);
      return { kind: "mutation_request", collection, document, idempotency, write };
    }
    case 15: {
      reader.arrayHeader(1);
      const outcome = msgpackDecodeMutationOutcome(reader);
      return { kind: "mutation_ack", outcome };
    }
    case 16: {
      reader.arrayHeader(2);
      const token = reader.binary(0, 16384);
      const sequence = reader.u64();
      return { kind: "causal_token", token, sequence };
    }
    case 17: {
      reader.arrayHeader(1);
      const generation = reader.u64();
      return { kind: "auth_generation_changed", generation };
    }
    case 18: {
      reader.arrayHeader(5);
      const operation = reader.binary(16, 16);
      const phase = msgpackDecodeOperationPhase(reader);
      const done = reader.u64();
      const total = reader.optionNil() ? null : reader.u64();
      const detail = reader.optionNil() ? null : reader.text(256);
      return { kind: "operation_progress", operation, phase, done, total, detail };
    }
    case 19: {
      reader.arrayHeader(1);
      const mode = msgpackDecodeBackpressureMode(reader);
      return { kind: "backpressure", mode };
    }
    default:
      throw new TerrascaleMsgpackError("unknown_message_tag", `message tag ${tag} is not defined by the manifest`);
  }
}

/**
 * Decodes one complete protocol message from untrusted bytes. The
 * allocation-bounded structural scan runs first; version and feature
 * checks fail closed before any body field is read; and the exact-shape
 * decode assembles the public value only after every field validated, so
 * a hostile message never yields a partially constructed value.
 */
export function decodeTerrascaleSyncV2MsgpackEnvelope(bytes: Uint8Array): TerrascaleSyncV2MsgpackEnvelope {
  scanMessagePack(bytes, terrascaleSyncV2MsgpackScanLimits);
  const reader = new MsgpackReader(bytes);
  reader.arrayHeader(7);
  const major = reader.u32();
  if (major !== terrascaleSyncV2MsgpackMajorVersion) {
    throw new TerrascaleMsgpackError("unsupported_version", `protocol major ${major} is not spoken; this build speaks 2`);
  }
  const minor = reader.u32();
  if (minor !== terrascaleSyncV2MsgpackMinorVersion) {
    throw new TerrascaleMsgpackError("unsupported_version", `protocol minor ${minor} is not spoken; this build speaks 0`);
  }
  const features = reader.u32();
  if ((features & ~terrascaleSyncV2MsgpackSupportedFeatures) !== 0) {
    throw new TerrascaleMsgpackError("unknown_feature", "the message requires feature bits this build does not speak");
  }
  const tag = reader.u32();
  const correlation = reader.u64();
  const causation = reader.u64();
  const message = msgpackDecodeMessageBody(reader, tag);
  reader.end();
  return { features, correlation, causation, message };
}
