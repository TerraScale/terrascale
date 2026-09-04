// GENERATED FILE - DO NOT EDIT.
//
// Source of authority: src/msgpack/manifest/terrascale.sync.v2.msgpack.json
// Generator: scripts/generate-msgpack-protocol.mjs (ADR 0025: the public
// terrascale.sync.v2.msgpack WebSocket protocol is generated from one
// checked-in versioned manifest; this TypeScript half provides codecs,
// exact-shape validators, and public types for @terrascale/sdk).
//
// manifest sha256:   90baa4a1f566fd3270ce29d11d103017e343ccfc68235c3f7f5ec29b0cce05ab
// generator sha256:  9c0a2c65e331a1ca8a012b3394df927d3c807797fc4ed04d09a38996c1692ee0
//
// Regenerate: node scripts/generate-msgpack-protocol.mjs
// Drift gate:  node scripts/generate-msgpack-protocol.mjs --check
// Drift test:  npm test (test/msgpack-manifest-drift.test.mjs)

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

function msgpackIsText(value: unknown, minBytes: number, maxBytes: number): value is string {
  const bytes = typeof value === "string" ? msgpackUtf8ByteLength(value) : -1;
  return typeof value === "string" && msgpackTextIsScalar(value) && bytes >= minBytes && bytes <= maxBytes;
}

function msgpackIsBinary(value: unknown, minBytes: number, maxBytes: number): value is Uint8Array {
  return value instanceof Uint8Array && value.length >= minBytes && value.length <= maxBytes;
}

/** The wire identity of the generated protocol. */
export const terrascaleSyncV2MsgpackProtocol = "terrascale.sync.v2.msgpack" as const;

/** The manifest revision this file was generated from. */
export const terrascaleSyncV2MsgpackManifestVersion = 1 as const;

/** The SHA-256 of the manifest file this file was generated from. */
export const terrascaleSyncV2MsgpackManifestSha256 = "90baa4a1f566fd3270ce29d11d103017e343ccfc68235c3f7f5ec29b0cce05ab" as const;

/** The protocol major this build speaks; anything else fails closed. */
export const terrascaleSyncV2MsgpackMajorVersion = 2 as const;

/** The protocol minor this build speaks; anything else fails closed. */
export const terrascaleSyncV2MsgpackMinorVersion = 1 as const;

/** The required feature bits this build understands; unknown bits fail closed. */
export const terrascaleSyncV2MsgpackSupportedFeatures = 0 as const;

/** The whole-message ceiling (bytes), enforced before any byte parses. */
export const terrascaleSyncV2MsgpackMaxMessageBytes = 8388608 as const;

/** The allocation-bounded structural scan limits, generated from the manifest. */
export const terrascaleSyncV2MsgpackScanLimits: Readonly<TerrascaleMsgpackScanLimits> = Object.freeze({
  maxMessageBytes: 8388608,
  maxDepth: 64,
  maxStringBytes: 262144,
  maxBinaryBytes: 1048576,
  maxArrayItems: 10000,
  maxMapEntries: 0,
  extensionsAllowed: false,
  float32Allowed: false,
});

/** The direction one message may travel. */
export type TerrascaleSyncV2MsgpackMessageDirection = "client_to_server" | "server_to_client" | "bidirectional";

/** The message kind names, in manifest order. */
export type TerrascaleSyncV2MsgpackMessageKindName =
  | "subscribe"
  | "unsubscribe"
  | "snapshot"
  | "delta"
  | "reset"
  | "heartbeat"
  | "checkpoint_ack"
  | "authorization_changed"
  | "region_route_change"
  | "error"
  | "backpressure";

/** The manifest message tags; tags are stable and never reused. */
export const terrascaleSyncV2MsgpackMessageTags: Readonly<Record<TerrascaleSyncV2MsgpackMessageKindName, number>> = Object.freeze({
  subscribe: 1,
  unsubscribe: 2,
  snapshot: 3,
  delta: 4,
  reset: 5,
  heartbeat: 6,
  checkpoint_ack: 7,
  authorization_changed: 8,
  region_route_change: 9,
  error: 10,
  backpressure: 11,
});

/** The reverse tag table; unknown tags fail closed on decode. */
export const terrascaleSyncV2MsgpackMessageNames: Readonly<Record<number, TerrascaleSyncV2MsgpackMessageKindName>> = Object.freeze({
  1: "subscribe",
  2: "unsubscribe",
  3: "snapshot",
  4: "delta",
  5: "reset",
  6: "heartbeat",
  7: "checkpoint_ack",
  8: "authorization_changed",
  9: "region_route_change",
  10: "error",
  11: "backpressure",
});

/** The manifest-recorded travel direction of each message. */
export const terrascaleSyncV2MsgpackMessageDirections: Readonly<Record<TerrascaleSyncV2MsgpackMessageKindName, TerrascaleSyncV2MsgpackMessageDirection>> = Object.freeze({
  subscribe: "client_to_server",
  unsubscribe: "client_to_server",
  snapshot: "server_to_client",
  delta: "server_to_client",
  reset: "server_to_client",
  heartbeat: "bidirectional",
  checkpoint_ack: "client_to_server",
  authorization_changed: "server_to_client",
  region_route_change: "server_to_client",
  error: "server_to_client",
  backpressure: "server_to_client",
});

/** One projected field in an authorized result item. Its value is an opaque canonical JSON byte string produced by the engine and parsed at most once by the browser client. (indexed array of 2 fields) */
export type TerrascaleSyncV2MsgpackProjectedField = {
  readonly path: string;
  readonly value: Uint8Array;
};

/** One authorized result item in canonical query order. The sum of its projected canonical JSON value bytes must not exceed the 1 MiB document limit. (indexed array of 2 fields) */
export type TerrascaleSyncV2MsgpackResultItem = {
  readonly identity: string;
  readonly fields: ReadonlyArray<TerrascaleSyncV2MsgpackProjectedField>;
};

/** A complete authorized result view at one opaque checkpoint. (indexed array of 6 fields) */
export type TerrascaleSyncV2MsgpackSnapshot = {
  readonly view_generation: bigint;
  readonly authorization_generation: bigint;
  readonly revision: bigint;
  readonly items: ReadonlyArray<TerrascaleSyncV2MsgpackResultItem>;
  readonly digest: string;
  readonly checkpoint: Uint8Array;
};

/** One projected field replacement or removal. A replacement is an opaque canonical JSON byte string, not a nested MessagePack document value. (indexed array of 2 fields) */
export type TerrascaleSyncV2MsgpackFieldChange = {
  readonly path: string;
  readonly value: Uint8Array | null;
};

/** One ordered mutation of an authorized result view. (tagged indexed array: [discriminant, fields...]) */
export type TerrascaleSyncV2MsgpackResultOperation =
  | { readonly kind: "insert"; readonly identity: string; readonly position: number; readonly fields: ReadonlyArray<TerrascaleSyncV2MsgpackProjectedField>; }
  | { readonly kind: "update"; readonly identity: string; readonly fields: ReadonlyArray<TerrascaleSyncV2MsgpackProjectedField>; }
  | { readonly kind: "patch"; readonly identity: string; readonly changes: ReadonlyArray<TerrascaleSyncV2MsgpackFieldChange>; }
  | { readonly kind: "remove"; readonly identity: string; }
  | { readonly kind: "move"; readonly identity: string; readonly position: number; }
;

/** Why the server replaced an incremental view with a snapshot. */
export type TerrascaleSyncV2MsgpackResetReasonName =
  | "checkpoint_expired"
  | "retention_advanced"
  | "generation_changed"
  | "checkpoint_not_current"
  | "view_diverged"
  | "oversized"
  | "invalid_operations"
  | "invalid_checkpoint"
  | "backpressure";

/** The closed public reactive error vocabulary. */
export type TerrascaleSyncV2MsgpackErrorCodeName =
  | "unsupported_version"
  | "malformed_frame"
  | "authentication_expired"
  | "permission_denied"
  | "checkpoint_refused"
  | "slow_consumer"
  | "overloaded"
  | "unavailable";

/** The server's bounded-consumer instruction. */
export type TerrascaleSyncV2MsgpackBackpressureModeName =
  | "pause"
  | "resume";

/** One public terrascale.sync.v2.msgpack message body (the tagged member of the envelope). */
export type TerrascaleSyncV2MsgpackMessage =
  | { readonly kind: "subscribe"; readonly subscription: string; readonly query: string; readonly parameters: Uint8Array; readonly checkpoint: Uint8Array | null; }
  | { readonly kind: "unsubscribe"; readonly subscription: string; }
  | { readonly kind: "snapshot"; readonly subscription: string; readonly snapshot: TerrascaleSyncV2MsgpackSnapshot; }
  | { readonly kind: "delta"; readonly subscription: string; readonly view_generation: bigint; readonly from_revision: bigint; readonly to_revision: bigint; readonly operations: ReadonlyArray<TerrascaleSyncV2MsgpackResultOperation>; readonly digest: string; readonly checkpoint: Uint8Array; }
  | { readonly kind: "reset"; readonly subscription: string; readonly reason: TerrascaleSyncV2MsgpackResetReasonName; readonly snapshot: TerrascaleSyncV2MsgpackSnapshot; }
  | { readonly kind: "heartbeat"; }
  | { readonly kind: "checkpoint_ack"; readonly subscription: string; readonly checkpoint: Uint8Array; }
  | { readonly kind: "authorization_changed"; readonly subscription: string; readonly authorization_generation: bigint; readonly removed: ReadonlyArray<string>; readonly snapshot: TerrascaleSyncV2MsgpackSnapshot; }
  | { readonly kind: "region_route_change"; readonly region: string; }
  | { readonly kind: "error"; readonly code: TerrascaleSyncV2MsgpackErrorCodeName; readonly detail: string | null; }
  | { readonly kind: "backpressure"; readonly mode: TerrascaleSyncV2MsgpackBackpressureModeName; }
;

/** One sealed protocol message: the envelope identities plus the tagged body. */
export type TerrascaleSyncV2MsgpackEnvelope = {
  readonly features: number;
  readonly correlation: bigint;
  readonly causation: bigint;
  readonly message: TerrascaleSyncV2MsgpackMessage;
};

const terrascaleSyncV2MsgpackResetReasonCodes: Readonly<Record<TerrascaleSyncV2MsgpackResetReasonName, number>> = Object.freeze({
  checkpoint_expired: 1,
  retention_advanced: 2,
  generation_changed: 3,
  checkpoint_not_current: 4,
  view_diverged: 5,
  oversized: 6,
  invalid_operations: 7,
  invalid_checkpoint: 8,
  backpressure: 9,
});

const terrascaleSyncV2MsgpackResetReasonNames: Readonly<Record<number, TerrascaleSyncV2MsgpackResetReasonName>> = Object.freeze({
  1: "checkpoint_expired",
  2: "retention_advanced",
  3: "generation_changed",
  4: "checkpoint_not_current",
  5: "view_diverged",
  6: "oversized",
  7: "invalid_operations",
  8: "invalid_checkpoint",
  9: "backpressure",
});

function msgpackIsResetReason(value: unknown): value is TerrascaleSyncV2MsgpackResetReasonName {
  if (value === "checkpoint_expired") { return true; }
  if (value === "retention_advanced") { return true; }
  if (value === "generation_changed") { return true; }
  if (value === "checkpoint_not_current") { return true; }
  if (value === "view_diverged") { return true; }
  if (value === "oversized") { return true; }
  if (value === "invalid_operations") { return true; }
  if (value === "invalid_checkpoint") { return true; }
  if (value === "backpressure") { return true; }
  return false;
}

const terrascaleSyncV2MsgpackErrorCodeCodes: Readonly<Record<TerrascaleSyncV2MsgpackErrorCodeName, number>> = Object.freeze({
  unsupported_version: 1,
  malformed_frame: 2,
  authentication_expired: 3,
  permission_denied: 4,
  checkpoint_refused: 5,
  slow_consumer: 6,
  overloaded: 7,
  unavailable: 8,
});

const terrascaleSyncV2MsgpackErrorCodeNames: Readonly<Record<number, TerrascaleSyncV2MsgpackErrorCodeName>> = Object.freeze({
  1: "unsupported_version",
  2: "malformed_frame",
  3: "authentication_expired",
  4: "permission_denied",
  5: "checkpoint_refused",
  6: "slow_consumer",
  7: "overloaded",
  8: "unavailable",
});

function msgpackIsErrorCode(value: unknown): value is TerrascaleSyncV2MsgpackErrorCodeName {
  if (value === "unsupported_version") { return true; }
  if (value === "malformed_frame") { return true; }
  if (value === "authentication_expired") { return true; }
  if (value === "permission_denied") { return true; }
  if (value === "checkpoint_refused") { return true; }
  if (value === "slow_consumer") { return true; }
  if (value === "overloaded") { return true; }
  if (value === "unavailable") { return true; }
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

/** The exact-shape validator for the projected_field structured type. */
export function validateTerrascaleSyncV2MsgpackProjectedField(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null
    || !("path" in candidate)
    || !("value" in candidate)
  ) {
    throw msgpackInvalid("projected_field");
  }
  if (Object.keys(candidate).length !== 2) {
    throw msgpackInvalid("projected_field");
  }
  const path = (candidate as { path: unknown }).path;
  if (!msgpackIsText(path, 1, 256)) {
    throw msgpackInvalid("projected_field.path");
  }
  const value = (candidate as { value: unknown }).value;
  if (!msgpackIsBinary(value, 0, 1048576)) {
    throw msgpackInvalid("projected_field.value");
  }
}

/** The exact-shape validator for the result_item structured type. */
export function validateTerrascaleSyncV2MsgpackResultItem(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null
    || !("identity" in candidate)
    || !("fields" in candidate)
  ) {
    throw msgpackInvalid("result_item");
  }
  if (Object.keys(candidate).length !== 2) {
    throw msgpackInvalid("result_item");
  }
  const identity = (candidate as { identity: unknown }).identity;
  if (!msgpackIsText(identity, 0, 256)) {
    throw msgpackInvalid("result_item.identity");
  }
  const fields = (candidate as { fields: unknown }).fields;
  if (!Array.isArray(fields) || fields.length > 1024) {
    throw msgpackInvalid("result_item.fields");
  }
  for (const element of fields) {
    validateTerrascaleSyncV2MsgpackProjectedField(element);
  }
  let aggregateResultItemFieldsBytes = 0;
  for (const aggregateResultItemFieldsBytesItem of fields) {
    const aggregateResultItemFieldsBytesValue = aggregateResultItemFieldsBytesItem.value;
    if (aggregateResultItemFieldsBytesValue instanceof Uint8Array) aggregateResultItemFieldsBytes += aggregateResultItemFieldsBytesValue.length;
  }
  if (aggregateResultItemFieldsBytes > 1048576) {
    throw msgpackInvalid("result_item.fields.aggregateBytes");
  }
}

/** The exact-shape validator for the snapshot structured type. */
export function validateTerrascaleSyncV2MsgpackSnapshot(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null
    || !("view_generation" in candidate)
    || !("authorization_generation" in candidate)
    || !("revision" in candidate)
    || !("items" in candidate)
    || !("digest" in candidate)
    || !("checkpoint" in candidate)
  ) {
    throw msgpackInvalid("snapshot");
  }
  if (Object.keys(candidate).length !== 6) {
    throw msgpackInvalid("snapshot");
  }
  const view_generation = (candidate as { view_generation: unknown }).view_generation;
  if (!msgpackIsU64(view_generation)) {
    throw msgpackInvalid("snapshot.view_generation");
  }
  const authorization_generation = (candidate as { authorization_generation: unknown }).authorization_generation;
  if (!msgpackIsU64(authorization_generation)) {
    throw msgpackInvalid("snapshot.authorization_generation");
  }
  const revision = (candidate as { revision: unknown }).revision;
  if (!msgpackIsU64(revision)) {
    throw msgpackInvalid("snapshot.revision");
  }
  const items = (candidate as { items: unknown }).items;
  if (!Array.isArray(items) || items.length > 10000) {
    throw msgpackInvalid("snapshot.items");
  }
  for (const element of items) {
    validateTerrascaleSyncV2MsgpackResultItem(element);
  }
  const digest = (candidate as { digest: unknown }).digest;
  if (!msgpackIsText(digest, 0, 64)) {
    throw msgpackInvalid("snapshot.digest");
  }
  const checkpoint = (candidate as { checkpoint: unknown }).checkpoint;
  if (!msgpackIsBinary(checkpoint, 1, 4096)) {
    throw msgpackInvalid("snapshot.checkpoint");
  }
}

/** The exact-shape validator for the field_change structured type. */
export function validateTerrascaleSyncV2MsgpackFieldChange(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null
    || !("path" in candidate)
    || !("value" in candidate)
  ) {
    throw msgpackInvalid("field_change");
  }
  if (Object.keys(candidate).length !== 2) {
    throw msgpackInvalid("field_change");
  }
  const path = (candidate as { path: unknown }).path;
  if (!msgpackIsText(path, 1, 256)) {
    throw msgpackInvalid("field_change.path");
  }
  const value = (candidate as { value: unknown }).value;
  if (value !== null) {
    if (!msgpackIsBinary(value, 0, 1048576)) {
      throw msgpackInvalid("field_change.value");
    }
  }
}

/** The exact-shape validator for the result_operation union. */
export function validateTerrascaleSyncV2MsgpackResultOperation(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {
    throw msgpackInvalid("result_operation");
  }
  const kind = (candidate as { kind: unknown }).kind;
  if (kind === "insert") {
    if (Object.keys(candidate).length !== 4) {
      throw msgpackInvalid("result_operation.insert");
    }
    if (!("identity" in candidate)) {
      throw msgpackInvalid("result_operation.insert");
    }
    const identity = (candidate as { identity: unknown }).identity;
    if (!msgpackIsText(identity, 0, 256)) {
      throw msgpackInvalid("result_operation.insert.identity");
    }
    if (!("position" in candidate)) {
      throw msgpackInvalid("result_operation.insert");
    }
    const position = (candidate as { position: unknown }).position;
    if (!msgpackIsU32(position)) {
      throw msgpackInvalid("result_operation.insert.position");
    }
    if (!("fields" in candidate)) {
      throw msgpackInvalid("result_operation.insert");
    }
    const fields = (candidate as { fields: unknown }).fields;
    if (!Array.isArray(fields) || fields.length > 1024) {
      throw msgpackInvalid("result_operation.insert.fields");
    }
    for (const element of fields) {
      validateTerrascaleSyncV2MsgpackProjectedField(element);
    }
    let aggregateResultOperationInsertFieldsBytes = 0;
    for (const aggregateResultOperationInsertFieldsBytesItem of fields) {
      const aggregateResultOperationInsertFieldsBytesValue = aggregateResultOperationInsertFieldsBytesItem.value;
      if (aggregateResultOperationInsertFieldsBytesValue instanceof Uint8Array) aggregateResultOperationInsertFieldsBytes += aggregateResultOperationInsertFieldsBytesValue.length;
    }
    if (aggregateResultOperationInsertFieldsBytes > 1048576) {
      throw msgpackInvalid("result_operation.insert.fields.aggregateBytes");
    }
    return;
  }
  if (kind === "update") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("result_operation.update");
    }
    if (!("identity" in candidate)) {
      throw msgpackInvalid("result_operation.update");
    }
    const identity = (candidate as { identity: unknown }).identity;
    if (!msgpackIsText(identity, 0, 256)) {
      throw msgpackInvalid("result_operation.update.identity");
    }
    if (!("fields" in candidate)) {
      throw msgpackInvalid("result_operation.update");
    }
    const fields = (candidate as { fields: unknown }).fields;
    if (!Array.isArray(fields) || fields.length > 1024) {
      throw msgpackInvalid("result_operation.update.fields");
    }
    for (const element of fields) {
      validateTerrascaleSyncV2MsgpackProjectedField(element);
    }
    let aggregateResultOperationUpdateFieldsBytes = 0;
    for (const aggregateResultOperationUpdateFieldsBytesItem of fields) {
      const aggregateResultOperationUpdateFieldsBytesValue = aggregateResultOperationUpdateFieldsBytesItem.value;
      if (aggregateResultOperationUpdateFieldsBytesValue instanceof Uint8Array) aggregateResultOperationUpdateFieldsBytes += aggregateResultOperationUpdateFieldsBytesValue.length;
    }
    if (aggregateResultOperationUpdateFieldsBytes > 1048576) {
      throw msgpackInvalid("result_operation.update.fields.aggregateBytes");
    }
    return;
  }
  if (kind === "patch") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("result_operation.patch");
    }
    if (!("identity" in candidate)) {
      throw msgpackInvalid("result_operation.patch");
    }
    const identity = (candidate as { identity: unknown }).identity;
    if (!msgpackIsText(identity, 0, 256)) {
      throw msgpackInvalid("result_operation.patch.identity");
    }
    if (!("changes" in candidate)) {
      throw msgpackInvalid("result_operation.patch");
    }
    const changes = (candidate as { changes: unknown }).changes;
    if (!Array.isArray(changes) || changes.length > 1024) {
      throw msgpackInvalid("result_operation.patch.changes");
    }
    for (const element of changes) {
      validateTerrascaleSyncV2MsgpackFieldChange(element);
    }
    let aggregateResultOperationPatchChangesBytes = 0;
    for (const aggregateResultOperationPatchChangesBytesItem of changes) {
      const aggregateResultOperationPatchChangesBytesValue = aggregateResultOperationPatchChangesBytesItem.value;
      if (aggregateResultOperationPatchChangesBytesValue instanceof Uint8Array) aggregateResultOperationPatchChangesBytes += aggregateResultOperationPatchChangesBytesValue.length;
    }
    if (aggregateResultOperationPatchChangesBytes > 1048576) {
      throw msgpackInvalid("result_operation.patch.changes.aggregateBytes");
    }
    return;
  }
  if (kind === "remove") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("result_operation.remove");
    }
    if (!("identity" in candidate)) {
      throw msgpackInvalid("result_operation.remove");
    }
    const identity = (candidate as { identity: unknown }).identity;
    if (!msgpackIsText(identity, 0, 256)) {
      throw msgpackInvalid("result_operation.remove.identity");
    }
    return;
  }
  if (kind === "move") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("result_operation.move");
    }
    if (!("identity" in candidate)) {
      throw msgpackInvalid("result_operation.move");
    }
    const identity = (candidate as { identity: unknown }).identity;
    if (!msgpackIsText(identity, 0, 256)) {
      throw msgpackInvalid("result_operation.move.identity");
    }
    if (!("position" in candidate)) {
      throw msgpackInvalid("result_operation.move");
    }
    const position = (candidate as { position: unknown }).position;
    if (!msgpackIsU32(position)) {
      throw msgpackInvalid("result_operation.move.position");
    }
    return;
  }
  throw msgpackInvalid("result_operation.kind");
}

/** The exact-shape validator for one public message body (untrusted in-memory values too). */
export function validateTerrascaleSyncV2MsgpackMessage(candidate: unknown): void {
  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {
    throw msgpackInvalid("message");
  }
  const kind = (candidate as { kind: unknown }).kind;
  if (kind === "subscribe") {
    if (Object.keys(candidate).length !== 5) {
      throw msgpackInvalid("subscribe");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("subscribe");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("subscribe.subscription");
    }
    if (!("query" in candidate)) {
      throw msgpackInvalid("subscribe");
    }
    const query = (candidate as { query: unknown }).query;
    if (!msgpackIsText(query, 0, 262144)) {
      throw msgpackInvalid("subscribe.query");
    }
    if (!("parameters" in candidate)) {
      throw msgpackInvalid("subscribe");
    }
    const parameters = (candidate as { parameters: unknown }).parameters;
    if (!msgpackIsBinary(parameters, 0, 1048576)) {
      throw msgpackInvalid("subscribe.parameters");
    }
    if (!("checkpoint" in candidate)) {
      throw msgpackInvalid("subscribe");
    }
    const checkpoint = (candidate as { checkpoint: unknown }).checkpoint;
    if (checkpoint !== null) {
      if (!msgpackIsBinary(checkpoint, 1, 4096)) {
        throw msgpackInvalid("subscribe.checkpoint");
      }
    }
    return;
  }
  if (kind === "unsubscribe") {
    if (Object.keys(candidate).length !== 2) {
      throw msgpackInvalid("unsubscribe");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("unsubscribe");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("unsubscribe.subscription");
    }
    return;
  }
  if (kind === "snapshot") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("snapshot");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("snapshot");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("snapshot.subscription");
    }
    if (!("snapshot" in candidate)) {
      throw msgpackInvalid("snapshot");
    }
    const snapshot = (candidate as { snapshot: unknown }).snapshot;
    validateTerrascaleSyncV2MsgpackSnapshot(snapshot);
    return;
  }
  if (kind === "delta") {
    if (Object.keys(candidate).length !== 8) {
      throw msgpackInvalid("delta");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("delta.subscription");
    }
    if (!("view_generation" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const view_generation = (candidate as { view_generation: unknown }).view_generation;
    if (!msgpackIsU64(view_generation)) {
      throw msgpackInvalid("delta.view_generation");
    }
    if (!("from_revision" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const from_revision = (candidate as { from_revision: unknown }).from_revision;
    if (!msgpackIsU64(from_revision)) {
      throw msgpackInvalid("delta.from_revision");
    }
    if (!("to_revision" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const to_revision = (candidate as { to_revision: unknown }).to_revision;
    if (!msgpackIsU64(to_revision)) {
      throw msgpackInvalid("delta.to_revision");
    }
    if (!("operations" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const operations = (candidate as { operations: unknown }).operations;
    if (!Array.isArray(operations) || operations.length > 1024) {
      throw msgpackInvalid("delta.operations");
    }
    for (const element of operations) {
      validateTerrascaleSyncV2MsgpackResultOperation(element);
    }
    if (!("digest" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const digest = (candidate as { digest: unknown }).digest;
    if (!msgpackIsText(digest, 0, 64)) {
      throw msgpackInvalid("delta.digest");
    }
    if (!("checkpoint" in candidate)) {
      throw msgpackInvalid("delta");
    }
    const checkpoint = (candidate as { checkpoint: unknown }).checkpoint;
    if (!msgpackIsBinary(checkpoint, 1, 4096)) {
      throw msgpackInvalid("delta.checkpoint");
    }
    return;
  }
  if (kind === "reset") {
    if (Object.keys(candidate).length !== 4) {
      throw msgpackInvalid("reset");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("reset");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("reset.subscription");
    }
    if (!("reason" in candidate)) {
      throw msgpackInvalid("reset");
    }
    const reason = (candidate as { reason: unknown }).reason;
    if (!msgpackIsResetReason(reason)) {
      throw msgpackInvalid("reset.reason");
    }
    if (!("snapshot" in candidate)) {
      throw msgpackInvalid("reset");
    }
    const snapshot = (candidate as { snapshot: unknown }).snapshot;
    validateTerrascaleSyncV2MsgpackSnapshot(snapshot);
    return;
  }
  if (kind === "heartbeat") {
    if (Object.keys(candidate).length !== 1) {
      throw msgpackInvalid("heartbeat");
    }
    return;
  }
  if (kind === "checkpoint_ack") {
    if (Object.keys(candidate).length !== 3) {
      throw msgpackInvalid("checkpoint_ack");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("checkpoint_ack");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("checkpoint_ack.subscription");
    }
    if (!("checkpoint" in candidate)) {
      throw msgpackInvalid("checkpoint_ack");
    }
    const checkpoint = (candidate as { checkpoint: unknown }).checkpoint;
    if (!msgpackIsBinary(checkpoint, 1, 4096)) {
      throw msgpackInvalid("checkpoint_ack.checkpoint");
    }
    return;
  }
  if (kind === "authorization_changed") {
    if (Object.keys(candidate).length !== 5) {
      throw msgpackInvalid("authorization_changed");
    }
    if (!("subscription" in candidate)) {
      throw msgpackInvalid("authorization_changed");
    }
    const subscription = (candidate as { subscription: unknown }).subscription;
    if (!msgpackIsText(subscription, 0, 128)) {
      throw msgpackInvalid("authorization_changed.subscription");
    }
    if (!("authorization_generation" in candidate)) {
      throw msgpackInvalid("authorization_changed");
    }
    const authorization_generation = (candidate as { authorization_generation: unknown }).authorization_generation;
    if (!msgpackIsU64(authorization_generation)) {
      throw msgpackInvalid("authorization_changed.authorization_generation");
    }
    if (!("removed" in candidate)) {
      throw msgpackInvalid("authorization_changed");
    }
    const removed = (candidate as { removed: unknown }).removed;
    if (!Array.isArray(removed) || removed.length > 10000) {
      throw msgpackInvalid("authorization_changed.removed");
    }
    for (const element of removed) {
      if (!msgpackIsText(element, 0, 256)) {
        throw msgpackInvalid("authorization_changed.removed[]");
      }
    }
    if (!("snapshot" in candidate)) {
      throw msgpackInvalid("authorization_changed");
    }
    const snapshot = (candidate as { snapshot: unknown }).snapshot;
    validateTerrascaleSyncV2MsgpackSnapshot(snapshot);
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
    if (!msgpackIsText(region, 0, 64)) {
      throw msgpackInvalid("region_route_change.region");
    }
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
      if (!msgpackIsText(detail, 0, 256)) {
        throw msgpackInvalid("error.detail");
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

function msgpackEncodeProjectedField(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackProjectedField): void {
  writer.arrayHeader(2);
  writer.text(value.path, 256);
  writer.binary(value.value, 0, 1048576);
}

function msgpackEncodeResultItem(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackResultItem): void {
  writer.arrayHeader(2);
  writer.text(value.identity, 256);
  writer.arrayHeader(value.fields.length);
  for (const element of value.fields) {
    msgpackEncodeProjectedField(writer, element);
  }
}

function msgpackEncodeSnapshot(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackSnapshot): void {
  writer.arrayHeader(6);
  writer.u64(value.view_generation);
  writer.u64(value.authorization_generation);
  writer.u64(value.revision);
  writer.arrayHeader(value.items.length);
  for (const element of value.items) {
    msgpackEncodeResultItem(writer, element);
  }
  writer.text(value.digest, 64);
  writer.binary(value.checkpoint, 1, 4096);
}

function msgpackEncodeFieldChange(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackFieldChange): void {
  writer.arrayHeader(2);
  writer.text(value.path, 256);
  if (value.value === null) {
    writer.nil();
  } else {
    writer.binary(value.value, 0, 1048576);
  }
}

function msgpackEncodeResultOperation(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackResultOperation): void {
  switch (value.kind) {
    case "insert": {
      writer.arrayHeader(4);
      writer.u32(1);
      writer.text(value.identity, 256);
      writer.u32(value.position);
      writer.arrayHeader(value.fields.length);
      for (const element of value.fields) {
        msgpackEncodeProjectedField(writer, element);
      }
      break;
    }
    case "update": {
      writer.arrayHeader(3);
      writer.u32(2);
      writer.text(value.identity, 256);
      writer.arrayHeader(value.fields.length);
      for (const element of value.fields) {
        msgpackEncodeProjectedField(writer, element);
      }
      break;
    }
    case "patch": {
      writer.arrayHeader(3);
      writer.u32(3);
      writer.text(value.identity, 256);
      writer.arrayHeader(value.changes.length);
      for (const element of value.changes) {
        msgpackEncodeFieldChange(writer, element);
      }
      break;
    }
    case "remove": {
      writer.arrayHeader(2);
      writer.u32(4);
      writer.text(value.identity, 256);
      break;
    }
    case "move": {
      writer.arrayHeader(3);
      writer.u32(5);
      writer.text(value.identity, 256);
      writer.u32(value.position);
      break;
    }
  }
}

function msgpackEncodeResetReason(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackResetReasonName): void {
  writer.u32(terrascaleSyncV2MsgpackResetReasonCodes[value]);
}

function msgpackEncodeErrorCode(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackErrorCodeName): void {
  writer.u32(terrascaleSyncV2MsgpackErrorCodeCodes[value]);
}

function msgpackEncodeBackpressureMode(writer: MsgpackWriter, value: TerrascaleSyncV2MsgpackBackpressureModeName): void {
  writer.u32(terrascaleSyncV2MsgpackBackpressureModeCodes[value]);
}

function msgpackEncodeMessageBody(writer: MsgpackWriter, message: TerrascaleSyncV2MsgpackMessage): void {
  switch (message.kind) {
    case "subscribe": {
      writer.arrayHeader(4);
      writer.text(message.subscription, 128);
      writer.text(message.query, 262144);
      writer.binary(message.parameters, 0, 1048576);
      if (message.checkpoint === null) {
        writer.nil();
      } else {
        writer.binary(message.checkpoint, 1, 4096);
      }
      break;
    }
    case "unsubscribe": {
      writer.arrayHeader(1);
      writer.text(message.subscription, 128);
      break;
    }
    case "snapshot": {
      writer.arrayHeader(2);
      writer.text(message.subscription, 128);
      msgpackEncodeSnapshot(writer, message.snapshot);
      break;
    }
    case "delta": {
      writer.arrayHeader(7);
      writer.text(message.subscription, 128);
      writer.u64(message.view_generation);
      writer.u64(message.from_revision);
      writer.u64(message.to_revision);
      writer.arrayHeader(message.operations.length);
      for (const element of message.operations) {
        msgpackEncodeResultOperation(writer, element);
      }
      writer.text(message.digest, 64);
      writer.binary(message.checkpoint, 1, 4096);
      break;
    }
    case "reset": {
      writer.arrayHeader(3);
      writer.text(message.subscription, 128);
      msgpackEncodeResetReason(writer, message.reason);
      msgpackEncodeSnapshot(writer, message.snapshot);
      break;
    }
    case "heartbeat": {
      writer.arrayHeader(0);
      break;
    }
    case "checkpoint_ack": {
      writer.arrayHeader(2);
      writer.text(message.subscription, 128);
      writer.binary(message.checkpoint, 1, 4096);
      break;
    }
    case "authorization_changed": {
      writer.arrayHeader(4);
      writer.text(message.subscription, 128);
      writer.u64(message.authorization_generation);
      writer.arrayHeader(message.removed.length);
      for (const element of message.removed) {
        writer.text(element, 256);
      }
      msgpackEncodeSnapshot(writer, message.snapshot);
      break;
    }
    case "region_route_change": {
      writer.arrayHeader(1);
      writer.text(message.region, 64);
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

function msgpackDecodeProjectedField(reader: MsgpackReader): TerrascaleSyncV2MsgpackProjectedField {
  reader.arrayHeader(2);
  const path = reader.text(256);
  if (msgpackUtf8ByteLength(path) < 1) {
    throw new TerrascaleMsgpackError("invalid_shape", "projected_field.path is below its minimum byte bound");
  }
  const value = reader.binary(0, 1048576);
  return { path, value };
}

function msgpackDecodeResultItem(reader: MsgpackReader): TerrascaleSyncV2MsgpackResultItem {
  reader.arrayHeader(2);
  const identity = reader.text(256);
  const fieldsCount = reader.arrayCount(1024);
  const fields: Array<TerrascaleSyncV2MsgpackProjectedField> = [];
  for (let index = 0; index < fieldsCount; index++) {
    fields.push(msgpackDecodeProjectedField(reader));
  }
  let aggregateResultItemFieldsBytes = 0;
  for (const aggregateResultItemFieldsBytesItem of fields) {
    const aggregateResultItemFieldsBytesValue = aggregateResultItemFieldsBytesItem.value;
    if (aggregateResultItemFieldsBytesValue instanceof Uint8Array) aggregateResultItemFieldsBytes += aggregateResultItemFieldsBytesValue.length;
  }
  if (aggregateResultItemFieldsBytes > 1048576) {
    throw new TerrascaleMsgpackError("limit_binary", "result_item.fields exceeds its aggregate binary byte bound");
  }
  return { identity, fields };
}

function msgpackDecodeSnapshot(reader: MsgpackReader): TerrascaleSyncV2MsgpackSnapshot {
  reader.arrayHeader(6);
  const view_generation = reader.u64();
  const authorization_generation = reader.u64();
  const revision = reader.u64();
  const itemsCount = reader.arrayCount(10000);
  const items: Array<TerrascaleSyncV2MsgpackResultItem> = [];
  for (let index = 0; index < itemsCount; index++) {
    items.push(msgpackDecodeResultItem(reader));
  }
  const digest = reader.text(64);
  const checkpoint = reader.binary(1, 4096);
  return { view_generation, authorization_generation, revision, items, digest, checkpoint };
}

function msgpackDecodeFieldChange(reader: MsgpackReader): TerrascaleSyncV2MsgpackFieldChange {
  reader.arrayHeader(2);
  const path = reader.text(256);
  if (msgpackUtf8ByteLength(path) < 1) {
    throw new TerrascaleMsgpackError("invalid_shape", "field_change.path is below its minimum byte bound");
  }
  const value = reader.optionNil() ? null : reader.binary(0, 1048576);
  return { path, value };
}

function msgpackDecodeResultOperation(reader: MsgpackReader): TerrascaleSyncV2MsgpackResultOperation {
  const count = reader.arrayCount(4);
  const discriminant = reader.u32();
  if (discriminant === 1) {
    if (count !== 4) {
      throw msgpackShape("result_operation.insert");
    }
      const identity = reader.text(256);
      const position = reader.u32();
      const fieldsCount = reader.arrayCount(1024);
      const fields: Array<TerrascaleSyncV2MsgpackProjectedField> = [];
      for (let index = 0; index < fieldsCount; index++) {
        fields.push(msgpackDecodeProjectedField(reader));
      }
      let aggregateResultOperationInsertFieldsBytes = 0;
      for (const aggregateResultOperationInsertFieldsBytesItem of fields) {
        const aggregateResultOperationInsertFieldsBytesValue = aggregateResultOperationInsertFieldsBytesItem.value;
        if (aggregateResultOperationInsertFieldsBytesValue instanceof Uint8Array) aggregateResultOperationInsertFieldsBytes += aggregateResultOperationInsertFieldsBytesValue.length;
      }
      if (aggregateResultOperationInsertFieldsBytes > 1048576) {
        throw new TerrascaleMsgpackError("limit_binary", "result_operation.insert.fields exceeds its aggregate binary byte bound");
      }
    return { kind: "insert", identity, position, fields };
  }
  if (discriminant === 2) {
    if (count !== 3) {
      throw msgpackShape("result_operation.update");
    }
      const identity = reader.text(256);
      const fieldsCount = reader.arrayCount(1024);
      const fields: Array<TerrascaleSyncV2MsgpackProjectedField> = [];
      for (let index = 0; index < fieldsCount; index++) {
        fields.push(msgpackDecodeProjectedField(reader));
      }
      let aggregateResultOperationUpdateFieldsBytes = 0;
      for (const aggregateResultOperationUpdateFieldsBytesItem of fields) {
        const aggregateResultOperationUpdateFieldsBytesValue = aggregateResultOperationUpdateFieldsBytesItem.value;
        if (aggregateResultOperationUpdateFieldsBytesValue instanceof Uint8Array) aggregateResultOperationUpdateFieldsBytes += aggregateResultOperationUpdateFieldsBytesValue.length;
      }
      if (aggregateResultOperationUpdateFieldsBytes > 1048576) {
        throw new TerrascaleMsgpackError("limit_binary", "result_operation.update.fields exceeds its aggregate binary byte bound");
      }
    return { kind: "update", identity, fields };
  }
  if (discriminant === 3) {
    if (count !== 3) {
      throw msgpackShape("result_operation.patch");
    }
      const identity = reader.text(256);
      const changesCount = reader.arrayCount(1024);
      const changes: Array<TerrascaleSyncV2MsgpackFieldChange> = [];
      for (let index = 0; index < changesCount; index++) {
        changes.push(msgpackDecodeFieldChange(reader));
      }
      let aggregateResultOperationPatchChangesBytes = 0;
      for (const aggregateResultOperationPatchChangesBytesItem of changes) {
        const aggregateResultOperationPatchChangesBytesValue = aggregateResultOperationPatchChangesBytesItem.value;
        if (aggregateResultOperationPatchChangesBytesValue instanceof Uint8Array) aggregateResultOperationPatchChangesBytes += aggregateResultOperationPatchChangesBytesValue.length;
      }
      if (aggregateResultOperationPatchChangesBytes > 1048576) {
        throw new TerrascaleMsgpackError("limit_binary", "result_operation.patch.changes exceeds its aggregate binary byte bound");
      }
    return { kind: "patch", identity, changes };
  }
  if (discriminant === 4) {
    if (count !== 2) {
      throw msgpackShape("result_operation.remove");
    }
      const identity = reader.text(256);
    return { kind: "remove", identity };
  }
  if (discriminant === 5) {
    if (count !== 3) {
      throw msgpackShape("result_operation.move");
    }
      const identity = reader.text(256);
      const position = reader.u32();
    return { kind: "move", identity, position };
  }
  throw new TerrascaleMsgpackError("unknown_variant", `result_operation discriminant ${discriminant} is not defined by the manifest`);
}

function msgpackDecodeResetReason(reader: MsgpackReader): TerrascaleSyncV2MsgpackResetReasonName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackResetReasonNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `reset_reason value ${code} is not defined by the manifest`);
  }
  return name;
}

function msgpackDecodeErrorCode(reader: MsgpackReader): TerrascaleSyncV2MsgpackErrorCodeName {
  const code = reader.u32();
  const name = terrascaleSyncV2MsgpackErrorCodeNames[code];
  if (name === undefined) {
    throw new TerrascaleMsgpackError("unknown_enum", `error_code value ${code} is not defined by the manifest`);
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
      reader.arrayHeader(4);
      const subscription = reader.text(128);
      const query = reader.text(262144);
      const parameters = reader.binary(0, 1048576);
      const checkpoint = reader.optionNil() ? null : reader.binary(1, 4096);
      return { kind: "subscribe", subscription, query, parameters, checkpoint };
    }
    case 2: {
      reader.arrayHeader(1);
      const subscription = reader.text(128);
      return { kind: "unsubscribe", subscription };
    }
    case 3: {
      reader.arrayHeader(2);
      const subscription = reader.text(128);
      const snapshot = msgpackDecodeSnapshot(reader);
      return { kind: "snapshot", subscription, snapshot };
    }
    case 4: {
      reader.arrayHeader(7);
      const subscription = reader.text(128);
      const view_generation = reader.u64();
      const from_revision = reader.u64();
      const to_revision = reader.u64();
      const operationsCount = reader.arrayCount(1024);
      const operations: Array<TerrascaleSyncV2MsgpackResultOperation> = [];
      for (let index = 0; index < operationsCount; index++) {
        operations.push(msgpackDecodeResultOperation(reader));
      }
      const digest = reader.text(64);
      const checkpoint = reader.binary(1, 4096);
      return { kind: "delta", subscription, view_generation, from_revision, to_revision, operations, digest, checkpoint };
    }
    case 5: {
      reader.arrayHeader(3);
      const subscription = reader.text(128);
      const reason = msgpackDecodeResetReason(reader);
      const snapshot = msgpackDecodeSnapshot(reader);
      return { kind: "reset", subscription, reason, snapshot };
    }
    case 6: {
      reader.arrayHeader(0);
      return { kind: "heartbeat" };
    }
    case 7: {
      reader.arrayHeader(2);
      const subscription = reader.text(128);
      const checkpoint = reader.binary(1, 4096);
      return { kind: "checkpoint_ack", subscription, checkpoint };
    }
    case 8: {
      reader.arrayHeader(4);
      const subscription = reader.text(128);
      const authorization_generation = reader.u64();
      const removedCount = reader.arrayCount(10000);
      const removed: Array<string> = [];
      for (let index = 0; index < removedCount; index++) {
        removed.push(reader.text(256));
      }
      const snapshot = msgpackDecodeSnapshot(reader);
      return { kind: "authorization_changed", subscription, authorization_generation, removed, snapshot };
    }
    case 9: {
      reader.arrayHeader(1);
      const region = reader.text(64);
      return { kind: "region_route_change", region };
    }
    case 10: {
      reader.arrayHeader(2);
      const code = msgpackDecodeErrorCode(reader);
      const detail = reader.optionNil() ? null : reader.text(256);
      return { kind: "error", code, detail };
    }
    case 11: {
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
    throw new TerrascaleMsgpackError("unsupported_version", `protocol minor ${minor} is not spoken; this build speaks 1`);
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
