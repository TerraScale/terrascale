// The native `terrascale.sync.v2` frame codec: the versioned
// client-channel wire protocol defined by the sync-v2 wire spec
// (`docs/sync/terrascale-sync-v2.md` in TerraBaseV2) and qualified by the
// SE-04/SE-05 golden-vector corpus. PD-2026-08-26-12 D1: this is a
// native-language implementation — no wasm, no C ABI embedding — and
// conformance to the committed corpus is the compatibility guarantee.
//
// The envelope every frame carries, in both directions (all integers
// little-endian):
//
//   u8  version = 2
//   u16 kind code
//   u64 correlation
//   u64 causation
//   u8  idempotency presence (0 | 1)
//   [u64 length + utf-8 key]         (mutation-family frames only)
//   u64 content digest (FNV-1a 64 over the canonical body bytes)
//   u64 body length
//   .. canonical body bytes          (begin with the u16 kind code)
//
// Decode fails closed: unknown versions (including v1 — no wire
// compatibility, PD-2026-08-26-10), unknown kinds, truncation, trailing
// bytes, length disagreement, violated limits, and idempotency-pairing
// violations are all typed rejections. Digest disagreement is not a
// decode error: the frame decodes and `verifySyncV2Frame` reports false
// (the caller's refusal), matching the SE-02 frame discipline.

/** The contract identity of the protocol. */
export const syncV2Contract = "terrascale.sync.v2" as const;

/** The wire version this module speaks; the leading byte of every frame. */
export const syncV2ProtocolVersion = 2 as const;

/** The fixed per-frame envelope overhead in bytes (a heartbeat's size). */
export const syncV2FixedEnvelopeBytes = 38;

/** The largest frame the decoder accepts, rejected before any field parses. */
export const syncV2MaxFrameBytes = 16 * 1024 * 1024;

/** The largest encoded payload one delivery frame may carry. */
export const syncV2MaxPayloadBytes = syncV2MaxFrameBytes - syncV2FixedEnvelopeBytes;

/** The longest identifier string a frame carries (bytes). */
export const syncV2MaxIdentBytes = 256;

/** The longest operation identity or idempotency key (bytes). */
export const syncV2MaxKeyBytes = 128;

/** The longest trigger collection or operation identity (bytes). */
export const syncV2MaxTriggerIdentBytes = 64;

/** The most entries one collection list carries. */
export const syncV2MaxCollections = 256;

/** The most fields one mutation body carries. */
export const syncV2MaxFields = 1024;

/** The longest error detail string (bytes). */
export const syncV2MaxDetailBytes = 256;

/** The maximum number of sparse native checkpoint positions. */
export const syncV2MaxCheckpointEntries = 64;

/** The frame classes and their written bytes-on-wire budget definitions (PD-2026-08-26-13 D1). */
export const syncV2FrameBudgetBytes = {
  session: 4096,
  subscription: 2048,
  acknowledgement: 1024,
  heartbeat: 64,
  delivery: 65_536,
  trigger: 256,
  control: 256,
  mutation: 65_536,
} as const;

/** A frame class: the unit the written budget definitions attach to. */
export type SyncV2FrameClass = keyof typeof syncV2FrameBudgetBytes;

/** The stable error-code names the corpus records (closed vocabulary). */
export type SyncV2ErrorCodeName =
  | "unsupported_version"
  | "malformed_frame"
  | "checkpoint_refused"
  | "scope_refused"
  | "session_closed"
  | "overloaded";

/** The typed, fail-closed rejection vocabulary of the codec. */
export type SyncV2ProtocolErrorCode =
  | "unsupported_version"
  | "malformed_frame"
  | "idempotency_pairing"
  | "invalid_idempotency_key";

/** The typed rejection the codec throws; `code` carries the stable corpus name. */
export class SyncV2ProtocolError extends Error {
  public override readonly name = "SyncV2ProtocolError";
  public readonly code: SyncV2ProtocolErrorCode;

  public constructor(code: SyncV2ProtocolErrorCode, reason: string) {
    super(`terrascale.sync.v2: ${code}: ${reason}`);
    this.code = code;
  }
}

/** One resumption checkpoint: the version-vector end pairs and the storage sequence absorbed. */
export type SyncV2Checkpoint = {
  /** The (peer, end) pairs in canonical ascending peer order. */
  readonly ends: ReadonlyArray<readonly [bigint, bigint]>;
  /** The storage sequence the session has absorbed. */
  readonly storage_sequence: bigint;
};

/** A scalar document value of a mutation body (the serving core's value domain). */
export type SyncV2Value = null | boolean | number | bigint | string;

/** The kind names of the frame vocabulary (stable wire identities). */
export type SyncV2FrameKindName =
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
  | "error"
  | "mutation_request"
  | "mutation_ack";

/** The payload-codec identities: the only byte payloads the protocol carries. */
export type SyncV2PayloadCodecName = "terrabase-native/1" | "terrabase-snapshot/1";

/**
 * One typed frame body. The field names are exactly the structured
 * descriptions the golden corpus records, so `decode(wire)` compares
 * directly against the committed vector descriptions; `payload` rides as
 * lowercase hex and `u64` fields as bigint.
 */
export type SyncV2FrameBody =
  | {
    readonly kind: "session_setup";
    readonly collections: readonly string[];
    readonly resume: SyncV2Checkpoint | null;
  }
  | {
    readonly kind: "session_accepted";
    readonly session: bigint;
    readonly server_checkpoint: SyncV2Checkpoint;
    readonly collections: readonly string[];
  }
  | {
    readonly kind: "subscription_modify";
    readonly add: readonly string[];
    readonly remove: readonly string[];
  }
  | {
    readonly kind: "subscription_changed";
    readonly active: readonly string[];
  }
  | {
    readonly kind: "checkpoint_ack";
    readonly checkpoint: SyncV2Checkpoint;
  }
  | { readonly kind: "heartbeat" }
  | {
    readonly kind: "delta_batch";
    readonly codec: "terrabase-native/1";
    readonly payload: string;
    readonly next: SyncV2Checkpoint;
    readonly within_budget: boolean;
    readonly single_commit_oversize: boolean;
    readonly commits: bigint;
  }
  | {
    readonly kind: "snapshot_answer";
    readonly codec: "terrabase-snapshot/1";
    readonly payload: string;
    readonly checkpoint: SyncV2Checkpoint;
  }
  | {
    readonly kind: "trigger";
    readonly collection: string;
    readonly storage_sequence: bigint;
    readonly operation_id: string;
  }
  | {
    readonly kind: "resync_required";
    readonly floor: SyncV2Checkpoint;
    readonly reason: "behind-retained-floor" | "history-compacted";
  }
  | {
    readonly kind: "region_route_change";
    readonly region: string;
  }
  | {
    readonly kind: "error";
    readonly code: SyncV2ErrorCodeName;
    readonly detail: string | null;
  }
  | {
    readonly kind: "mutation_request";
    readonly collection: string;
    readonly document: string;
    readonly put?: Readonly<Record<string, SyncV2Value>> | undefined;
    readonly delete?: boolean | undefined;
  }
  | {
    readonly kind: "mutation_ack";
    readonly outcome: "applied";
    readonly storage_sequence: bigint;
    readonly operation_id: string;
  }
  | {
    readonly kind: "mutation_ack";
    readonly outcome: "rejected";
    readonly rejection: SyncV2ErrorCodeName;
  };

/** One sealed frame: the envelope identities plus the typed body. */
export type SyncV2Frame = {
  readonly correlation: bigint;
  readonly causation: bigint;
  readonly idempotency: string | undefined;
  readonly digest: bigint;
  readonly body: SyncV2FrameBody;
};

const kindCodes: Readonly<Record<SyncV2FrameKindName, number>> = {
  session_setup: 0x0001,
  session_accepted: 0x0002,
  subscription_modify: 0x0003,
  subscription_changed: 0x0004,
  checkpoint_ack: 0x0005,
  heartbeat: 0x0006,
  delta_batch: 0x0007,
  snapshot_answer: 0x0008,
  trigger: 0x0009,
  resync_required: 0x000a,
  region_route_change: 0x000b,
  error: 0x000c,
  mutation_request: 0x000d,
  mutation_ack: 0x000e,
};

const kindNames: Readonly<Record<number, SyncV2FrameKindName>> = Object.fromEntries(
  Object.entries(kindCodes).map(([name, code]) => [code, name as SyncV2FrameKindName]),
);

const errorCodeCodes: Readonly<Record<SyncV2ErrorCodeName, number>> = {
  unsupported_version: 1,
  malformed_frame: 2,
  checkpoint_refused: 3,
  scope_refused: 4,
  session_closed: 5,
  overloaded: 6,
};

const errorCodeNames: Readonly<Record<number, SyncV2ErrorCodeName>> = Object.fromEntries(
  Object.entries(errorCodeCodes).map(([name, code]) => [code, name as SyncV2ErrorCodeName]),
);

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder("utf-8", { fatal: true });
const fnvOffsetBasis = 0xcbf2_9ce4_8422_2325n;
const fnvPrime = 0x0000_0100_0000_01b3n;
const u64Mask = 0xffff_ffff_ffff_ffffn;
const maxSafeInteger = 9_007_199_254_740_991n;

function malformed(reason: string): SyncV2ProtocolError {
  return new SyncV2ProtocolError("malformed_frame", reason);
}

function byteLength(value: string): number {
  return textEncoder.encode(value).length;
}

/** Compares two strings by Unicode code point, which agrees with UTF-8 byte order. */
function compareCodePoints(left: string, right: string): number {
  const leftPoints = [...left];
  const rightPoints = [...right];
  const count = Math.min(leftPoints.length, rightPoints.length);
  for (let index = 0; index < count; index++) {
    const order = leftPoints[index]!.codePointAt(0)! - rightPoints[index]!.codePointAt(0)!;
    if (order !== 0) {
      return order;
    }
  }
  return leftPoints.length - rightPoints.length;
}

// --- Checkpoint codec ------------------------------------------------------

/** The initial checkpoint: nothing absorbed. */
export function syncV2InitialCheckpoint(): SyncV2Checkpoint {
  return Object.freeze({ ends: Object.freeze([]), storage_sequence: 0n });
}

/** Builds a checkpoint from explicit end pairs, canonicalized (sorted, deduplicated). */
export function syncV2CanonicalCheckpoint(
  ends: ReadonlyArray<readonly [bigint, bigint | number]>,
  storageSequence: bigint,
): SyncV2Checkpoint {
  if (storageSequence < 0n || storageSequence > u64Mask || ends.length > syncV2MaxCheckpointEntries) {
    throw malformed("checkpoint values exceed their unsigned bounds");
  }
  const canonical = [...ends]
    .map((pair) => [pair[0], BigInt(pair[1])] as const)
    .sort((left, right) => (left[0] < right[0] ? -1 : left[0] > right[0] ? 1 : 0));
  const deduplicated: Array<readonly [bigint, bigint]> = [];
  for (const pair of canonical) {
    if (pair[0] < 0n || pair[0] > u64Mask || pair[1] < 0n || pair[1] > u64Mask) {
      throw malformed("checkpoint values exceed their unsigned bounds");
    }
    const last = deduplicated.at(-1);
    if (last === undefined || last[0] !== pair[0]) {
      deduplicated.push(pair);
    }
  }
  return Object.freeze({
    ends: Object.freeze(deduplicated),
    storage_sequence: storageSequence,
  });
}

function encodeCheckpointInto(bytes: number[], checkpoint: SyncV2Checkpoint): void {
  bytes.push(2);
  pushU64(bytes, checkpoint.storage_sequence);
  pushU32(bytes, checkpoint.ends.length);
  for (const [peer, end] of checkpoint.ends) {
    pushU64(bytes, peer);
    pushU64(bytes, end);
  }
}

/** The versioned checkpoint byte encoding carried by session bodies (base64 on the host HTTP surface). */
export function syncV2CheckpointToBytes(checkpoint: SyncV2Checkpoint): Uint8Array {
  const bytes: number[] = [];
  encodeCheckpointInto(bytes, checkpoint);
  return new Uint8Array(bytes);
}

/** Decodes a checkpoint, failing closed on unknown versions and truncation. */
export function syncV2CheckpointFromBytes(bytes: Uint8Array): SyncV2Checkpoint {
  if (bytes.length === 0 || bytes[0] !== 2) {
    throw new SyncV2ProtocolError("malformed_frame", "unknown checkpoint encoding version");
  }
  let cursor = 1;
  const take = (count: number): Uint8Array => {
    if (cursor + count > bytes.length) {
      throw malformed("truncated checkpoint encoding");
    }
    const slice = bytes.subarray(cursor, cursor + count);
    cursor += count;
    return slice;
  };
  const storageSequence = bytesToBigInt(take(8));
  const count = Number(bytesToBigInt(take(4)));
  if (count > syncV2MaxCheckpointEntries) {
    throw malformed("checkpoint entry count exceeds bound");
  }
  const ends: Array<readonly [bigint, bigint]> = [];
  let previousPeer: bigint | undefined;
  for (let index = 0; index < count; index++) {
    const peer = bytesToBigInt(take(8));
    const end = bytesToBigInt(take(8));
    if (previousPeer !== undefined && peer <= previousPeer) {
      throw malformed("checkpoint entries are not canonical");
    }
    previousPeer = peer;
    ends.push([peer, end]);
  }
  if (cursor !== bytes.length) {
    throw malformed("trailing checkpoint encoding");
  }
  return Object.freeze({ ends: Object.freeze(ends), storage_sequence: storageSequence });
}

// --- Primitives ------------------------------------------------------------

function pushU16(bytes: number[], value: number): void {
  bytes.push(value & 0xff, (value >>> 8) & 0xff);
}

function pushU32(bytes: number[], value: number): void {
  bytes.push(value & 0xff, (value >>> 8) & 0xff, (value >>> 16) & 0xff, (value >>> 24) & 0xff);
}

function pushU64(bytes: number[], value: bigint): void {
  let rest = value & u64Mask;
  for (let index = 0; index < 8; index++) {
    bytes.push(Number(rest & 0xffn));
    rest >>= 8n;
  }
}

function bytesToBigInt(bytes: Uint8Array): bigint {
  let value = 0n;
  for (let index = bytes.length - 1; index >= 0; index--) {
    value = (value << 8n) | BigInt(bytes[index]!);
  }
  return value;
}

class ByteReader {
  readonly #bytes: Uint8Array;
  #cursor = 0;

  public constructor(bytes: Uint8Array) {
    this.#bytes = bytes;
  }

  public get remaining(): number {
    return this.#bytes.length - this.#cursor;
  }

  public take(count: number, reason: string): Uint8Array {
    if (count > this.remaining) {
      throw malformed(reason);
    }
    const slice = this.#bytes.subarray(this.#cursor, this.#cursor + count);
    this.#cursor += count;
    return slice;
  }

  public u8(reason: string): number {
    return this.take(1, reason)[0]!;
  }

  public u16(reason: string): number {
    return Number(bytesToBigInt(this.take(2, reason)));
  }

  public u32(reason: string): number {
    return Number(bytesToBigInt(this.take(4, reason)));
  }

  public u64(reason: string): bigint {
    return bytesToBigInt(this.take(8, reason));
  }

  public i64(reason: string): bigint {
    const unsigned = this.u64(reason);
    return unsigned >= 1n << 63n ? unsigned - (1n << 64n) : unsigned;
  }

  public f64(reason: string): number {
    const view = new DataView(this.take(8, reason).slice().buffer);
    return view.getFloat64(0, true);
  }

  public string(limit: number, reason: string): string {
    const length = Number(this.u64(reason));
    if (length > limit || this.remaining < length) {
      throw malformed("string exceeds its frame limit");
    }
    try {
      // The fatal decoder keeps the vocabulary closed: bytes that are not
      // utf-8 are a typed malformed_frame rejection, exactly like the
      // idempotency-key path ("keys are utf-8") and the Rust reference.
      return textDecoder.decode(this.take(length, reason));
    } catch {
      throw malformed("string fields are utf-8");
    }
  }

  public strings(reason: string): string[] {
    const count = this.u32(reason);
    if (count > syncV2MaxCollections) {
      throw malformed("too many collection entries");
    }
    const list: string[] = [];
    for (let index = 0; index < count; index++) {
      list.push(this.string(syncV2MaxIdentBytes, reason));
    }
    return list;
  }

  public checkpoint(): SyncV2Checkpoint {
    const length = Number(this.u64("truncated frame body"));
    if (this.remaining < length) {
      throw malformed("truncated frame body");
    }
    const bytes = this.take(length, "truncated frame body");
    try {
      return syncV2CheckpointFromBytes(bytes);
    } catch (error) {
      if (error instanceof SyncV2ProtocolError && error.code === "malformed_frame") {
        throw malformed("the checkpoint does not decode");
      }
      throw error;
    }
  }

  public payload(reason: string): Uint8Array {
    const length = Number(this.u64(reason));
    if (length > syncV2MaxPayloadBytes) {
      throw malformed("payload exceeds the frame bound");
    }
    return this.take(length, reason);
  }

  public value(reason: string): SyncV2Value {
    const tag = this.u8(reason);
    if (tag === 0) {
      return null;
    }
    if (tag === 1) {
      return this.u8(reason) !== 0;
    }
    if (tag === 2) {
      const raw = this.i64(reason);
      return raw >= -maxSafeInteger && raw <= maxSafeInteger ? Number(raw) : raw;
    }
    if (tag === 3) {
      return this.f64(reason);
    }
    if (tag === 4) {
      return this.string(syncV2MaxIdentBytes, reason);
    }
    throw malformed("unknown value tag");
  }

  public end(): void {
    if (this.remaining !== 0) {
      throw malformed("trailing bytes in the frame body");
    }
  }
}

// --- Body encode ------------------------------------------------------------

function encodeStringInto(bytes: number[], value: string, limit: number): void {
  const encoded = textEncoder.encode(value);
  if (encoded.length > limit) {
    throw malformed("string exceeds its frame limit");
  }
  pushU64(bytes, BigInt(encoded.length));
  for (const byte of encoded) {
    bytes.push(byte);
  }
}

function encodeStringsInto(bytes: number[], list: readonly string[]): void {
  if (list.length > syncV2MaxCollections) {
    throw malformed("too many collection entries");
  }
  pushU32(bytes, list.length);
  for (const entry of list) {
    encodeStringInto(bytes, entry, syncV2MaxIdentBytes);
  }
}

function encodeCheckpointFieldInto(bytes: number[], checkpoint: SyncV2Checkpoint): void {
  const encoded = syncV2CheckpointToBytes(checkpoint);
  pushU64(bytes, BigInt(encoded.length));
  for (const byte of encoded) {
    bytes.push(byte);
  }
}

function encodeValueInto(bytes: number[], value: SyncV2Value): void {
  if (value === null) {
    bytes.push(0);
    return;
  }
  if (typeof value === "boolean") {
    bytes.push(1, value ? 1 : 0);
    return;
  }
  if (typeof value === "bigint") {
    bytes.push(2);
    pushU64(bytes, value);
    return;
  }
  if (typeof value === "number") {
    if (Number.isInteger(value) && Math.abs(value) <= Number(maxSafeInteger)) {
      bytes.push(2);
      pushU64(bytes, BigInt(value));
      return;
    }
    bytes.push(3);
    const view = new DataView(new ArrayBuffer(8));
    view.setFloat64(0, value, true);
    for (const byte of new Uint8Array(view.buffer)) {
      bytes.push(byte);
    }
    return;
  }
  bytes.push(4);
  encodeStringInto(bytes, value, syncV2MaxIdentBytes);
}

function encodePayloadInto(bytes: number[], payloadHex: string): void {
  const payload = decodeHexBytes(payloadHex);
  if (payload.length > syncV2MaxPayloadBytes) {
    throw malformed("payload exceeds the frame bound");
  }
  pushU64(bytes, BigInt(payload.length));
  for (const byte of payload) {
    bytes.push(byte);
  }
}

/** Strict per-character hex values: parseInt's prefix/partial parsing must not leak into the codec. */
const hexDigitValues: ReadonlyArray<number | undefined> = (() => {
  const table: Array<number | undefined> = new Array(128).fill(undefined);
  for (let digit = 0; digit < 10; digit++) {
    table[48 + digit] = digit;
  }
  for (let letter = 0; letter < 6; letter++) {
    table[97 + letter] = 10 + letter;
    table[65 + letter] = 10 + letter;
  }
  return table;
})();

function decodeHexBytes(hex: string): Uint8Array {
  if (hex.length % 2 !== 0) {
    throw malformed("payload hex has an odd length");
  }
  const bytes = new Uint8Array(hex.length / 2);
  for (let index = 0; index < bytes.length; index++) {
    const high = hexDigitValues[hex.charCodeAt(index * 2)] ?? 128;
    const low = hexDigitValues[hex.charCodeAt(index * 2 + 1)] ?? 128;
    if (high > 15 || low > 15) {
      throw malformed("payload hex carries a non-hex digit");
    }
    bytes[index] = (high << 4) | low;
  }
  return bytes;
}

function encodeHexBytes(bytes: Uint8Array): string {
  let hex = "";
  for (const byte of bytes) {
    hex += byte.toString(16).padStart(2, "0");
  }
  return hex;
}

function validateBody(body: SyncV2FrameBody): void {
  switch (body.kind) {
    case "session_setup":
    case "session_accepted":
    case "subscription_modify":
    case "subscription_changed": {
      for (const list of
        body.kind === "session_setup"
          ? [body.collections]
          : body.kind === "session_accepted"
            ? [body.collections]
            : body.kind === "subscription_modify"
              ? [body.add, body.remove]
              : [body.active]) {
        if (list.length > syncV2MaxCollections) {
          throw malformed("too many collection entries");
        }
        for (const entry of list) {
          if (byteLength(entry) > syncV2MaxIdentBytes) {
            throw malformed("string exceeds its frame limit");
          }
        }
      }
      if (body.kind === "session_setup" && body.resume !== null) {
        checkpointRoundTrip(body.resume, "the resume checkpoint does not round-trip");
      }
      if (body.kind === "session_accepted") {
        checkpointRoundTrip(body.server_checkpoint, "the server checkpoint does not round-trip");
      }
      return;
    }
    case "checkpoint_ack":
      checkpointRoundTrip(body.checkpoint, "the checkpoint does not round-trip");
      return;
    case "heartbeat":
      return;
    case "delta_batch": {
      if (body.codec !== "terrabase-native/1") {
        throw malformed("a delta batch carries the terrabase-native/1 codec only");
      }
      if (decodeHexBytes(body.payload).length > syncV2MaxPayloadBytes) {
        throw malformed("payload exceeds the frame bound");
      }
      if (body.single_commit_oversize && body.commits !== 1n) {
        throw malformed("the oversize flag requires exactly one commit");
      }
      checkpointRoundTrip(body.next, "the next checkpoint does not round-trip");
      return;
    }
    case "snapshot_answer": {
      if (body.codec !== "terrabase-snapshot/1") {
        throw malformed("a snapshot answer carries the terrabase-snapshot/1 codec only");
      }
      if (decodeHexBytes(body.payload).length > syncV2MaxPayloadBytes) {
        throw malformed("payload exceeds the frame bound");
      }
      checkpointRoundTrip(body.checkpoint, "the snapshot checkpoint does not round-trip");
      return;
    }
    case "trigger":
      if (byteLength(body.collection) > syncV2MaxTriggerIdentBytes
        || byteLength(body.operation_id) > syncV2MaxTriggerIdentBytes) {
        throw malformed("trigger identifiers are bounded to 64 bytes");
      }
      return;
    case "resync_required":
      checkpointRoundTrip(body.floor, "the floor checkpoint does not round-trip");
      return;
    case "region_route_change":
      if (byteLength(body.region) > syncV2MaxIdentBytes) {
        throw malformed("string exceeds its frame limit");
      }
      return;
    case "error":
      if (body.detail !== null && byteLength(body.detail) > syncV2MaxDetailBytes) {
        throw malformed("the error detail is bounded");
      }
      return;
    case "mutation_request": {
      if (byteLength(body.collection) > syncV2MaxIdentBytes
        || byteLength(body.document) > syncV2MaxIdentBytes) {
        throw malformed("string exceeds its frame limit");
      }
      if (body.put !== undefined) {
        const keys = Object.keys(body.put);
        if (keys.length > syncV2MaxFields) {
          throw malformed("too many mutation fields");
        }
        for (const key of keys) {
          if (byteLength(key) > syncV2MaxIdentBytes) {
            throw malformed("string exceeds its frame limit");
          }
          const value = body.put[key];
          if (typeof value === "string" && byteLength(value) > syncV2MaxIdentBytes) {
            throw malformed("string exceeds its frame limit");
          }
        }
      }
      return;
    }
    case "mutation_ack":
      if (body.outcome === "applied" && byteLength(body.operation_id) > syncV2MaxKeyBytes) {
        throw malformed("string exceeds its frame limit");
      }
      return;
  }
}

function checkpointRoundTrip(checkpoint: SyncV2Checkpoint, reason: string): void {
  try {
    const decoded = syncV2CheckpointFromBytes(syncV2CheckpointToBytes(checkpoint));
    if (
      decoded.storage_sequence !== checkpoint.storage_sequence
      || decoded.ends.length !== checkpoint.ends.length
      || decoded.ends.some((pair, index) => {
        const expected = checkpoint.ends[index];
        return expected === undefined || pair[0] !== expected[0] || pair[1] !== expected[1];
      })
    ) {
      throw malformed(reason);
    }
  } catch (error) {
    if (error instanceof SyncV2ProtocolError) {
      throw malformed(reason);
    }
    throw error;
  }
}

/** The canonical body encoding: the digest input and the wire body. */
export function syncV2BodyToBytes(body: SyncV2FrameBody): Uint8Array {
  validateBody(body);
  const bytes: number[] = [];
  pushU16(bytes, kindCodes[body.kind]);
  switch (body.kind) {
    case "session_setup": {
      encodeStringsInto(bytes, body.collections);
      if (body.resume === null) {
        bytes.push(0);
      } else {
        bytes.push(1);
        encodeCheckpointFieldInto(bytes, body.resume);
      }
      break;
    }
    case "session_accepted": {
      pushU64(bytes, body.session);
      encodeCheckpointFieldInto(bytes, body.server_checkpoint);
      encodeStringsInto(bytes, body.collections);
      break;
    }
    case "subscription_modify": {
      encodeStringsInto(bytes, body.add);
      encodeStringsInto(bytes, body.remove);
      break;
    }
    case "subscription_changed": {
      encodeStringsInto(bytes, body.active);
      break;
    }
    case "checkpoint_ack": {
      encodeCheckpointFieldInto(bytes, body.checkpoint);
      break;
    }
    case "heartbeat": {
      break;
    }
    case "delta_batch": {
      bytes.push(3);
      encodePayloadInto(bytes, body.payload);
      encodeCheckpointFieldInto(bytes, body.next);
      bytes.push(body.within_budget ? 1 : 0);
      bytes.push(body.single_commit_oversize ? 1 : 0);
      pushU64(bytes, body.commits);
      break;
    }
    case "snapshot_answer": {
      bytes.push(4);
      encodePayloadInto(bytes, body.payload);
      encodeCheckpointFieldInto(bytes, body.checkpoint);
      break;
    }
    case "trigger": {
      encodeStringInto(bytes, body.collection, syncV2MaxTriggerIdentBytes);
      pushU64(bytes, body.storage_sequence);
      encodeStringInto(bytes, body.operation_id, syncV2MaxTriggerIdentBytes);
      break;
    }
    case "resync_required": {
      encodeCheckpointFieldInto(bytes, body.floor);
      bytes.push(body.reason === "behind-retained-floor" ? 1 : 2);
      break;
    }
    case "region_route_change": {
      encodeStringInto(bytes, body.region, syncV2MaxIdentBytes);
      break;
    }
    case "error": {
      bytes.push(errorCodeCodes[body.code]);
      if (body.detail === null) {
        bytes.push(0);
      } else {
        bytes.push(1);
        encodeStringInto(bytes, body.detail, syncV2MaxDetailBytes);
      }
      break;
    }
    case "mutation_request": {
      encodeStringInto(bytes, body.collection, syncV2MaxIdentBytes);
      encodeStringInto(bytes, body.document, syncV2MaxIdentBytes);
      if (body.put !== undefined) {
        bytes.push(1);
        const keys = [...Object.keys(body.put)].sort(compareCodePoints);
        pushU32(bytes, keys.length);
        for (const key of keys) {
          encodeStringInto(bytes, key, syncV2MaxIdentBytes);
          encodeValueInto(bytes, body.put[key]!);
        }
      } else {
        bytes.push(2);
      }
      break;
    }
    case "mutation_ack": {
      if (body.outcome === "applied") {
        bytes.push(1);
        pushU64(bytes, body.storage_sequence);
        encodeStringInto(bytes, body.operation_id, syncV2MaxKeyBytes);
      } else {
        bytes.push(2);
        bytes.push(errorCodeCodes[body.rejection]);
      }
      break;
    }
  }
  return new Uint8Array(bytes);
}

// --- Body decode ------------------------------------------------------------

/** Decodes a canonical body, failing closed on every structural problem. */
export function syncV2BodyFromBytes(bytes: Uint8Array): SyncV2FrameBody {
  const reader = new ByteReader(bytes);
  const kindCode = reader.u16("truncated frame body");
  const kind = kindNames[kindCode];
  if (kind === undefined) {
    throw malformed("unknown frame kind");
  }
  const truncated = "truncated frame body";
  let body: SyncV2FrameBody;
  switch (kind) {
    case "session_setup": {
      const collections = reader.strings(truncated);
      const resumeFlag = reader.u8(truncated);
      if (resumeFlag > 1) {
        throw malformed("unknown resume flag");
      }
      body = {
        kind,
        collections,
        resume: resumeFlag === 1 ? reader.checkpoint() : null,
      };
      break;
    }
    case "session_accepted": {
      const session = reader.u64(truncated);
      const server = reader.checkpoint();
      const collections = reader.strings(truncated);
      body = { kind, session, server_checkpoint: server, collections };
      break;
    }
    case "subscription_modify": {
      const add = reader.strings(truncated);
      const remove = reader.strings(truncated);
      body = { kind, add, remove };
      break;
    }
    case "subscription_changed": {
      body = { kind, active: reader.strings(truncated) };
      break;
    }
    case "checkpoint_ack": {
      body = { kind, checkpoint: reader.checkpoint() };
      break;
    }
    case "heartbeat": {
      body = { kind };
      break;
    }
    case "delta_batch": {
      const codecTag = reader.u8(truncated);
      if (codecTag !== 3) {
        throw malformed(codecTag === 4 ? "a delta batch carries the terrabase-native/1 codec only" : "unknown payload codec");
      }
      const payload = reader.payload(truncated);
      const next = reader.checkpoint();
      const withinBudget = reader.u8(truncated) !== 0;
      const singleCommitOversize = reader.u8(truncated) !== 0;
      const commits = reader.u64(truncated);
      body = {
        kind,
        codec: "terrabase-native/1",
        payload: encodeHexBytes(payload),
        next,
        within_budget: withinBudget,
        single_commit_oversize: singleCommitOversize,
        commits,
      };
      break;
    }
    case "snapshot_answer": {
      const codecTag = reader.u8(truncated);
      if (codecTag !== 4) {
        throw malformed(codecTag === 3 ? "a snapshot answer carries the terrabase-snapshot/1 codec only" : "unknown payload codec");
      }
      const payload = reader.payload(truncated);
      const checkpoint = reader.checkpoint();
      body = { kind, codec: "terrabase-snapshot/1", payload: encodeHexBytes(payload), checkpoint };
      break;
    }
    case "trigger": {
      const collection = reader.string(syncV2MaxTriggerIdentBytes, truncated);
      const storageSequence = reader.u64(truncated);
      const operationId = reader.string(syncV2MaxTriggerIdentBytes, truncated);
      body = { kind, collection, storage_sequence: storageSequence, operation_id: operationId };
      break;
    }
    case "resync_required": {
      const floor = reader.checkpoint();
      const reasonTag = reader.u8(truncated);
      if (reasonTag !== 1 && reasonTag !== 2) {
        throw malformed("unknown resync reason");
      }
      body = { kind, floor, reason: reasonTag === 1 ? "behind-retained-floor" : "history-compacted" };
      break;
    }
    case "region_route_change": {
      body = { kind, region: reader.string(syncV2MaxIdentBytes, truncated) };
      break;
    }
    case "error": {
      const codeTag = reader.u8(truncated);
      const code = errorCodeNames[codeTag];
      if (code === undefined) {
        throw malformed("unknown error code");
      }
      const detailFlag = reader.u8(truncated);
      if (detailFlag > 1) {
        throw malformed("unknown detail flag");
      }
      body = { kind, code, detail: detailFlag === 1 ? reader.string(syncV2MaxDetailBytes, truncated) : null };
      break;
    }
    case "mutation_request": {
      const collection = reader.string(syncV2MaxIdentBytes, truncated);
      const document = reader.string(syncV2MaxIdentBytes, truncated);
      const writeTag = reader.u8(truncated);
      if (writeTag === 2) {
        body = { kind, collection, document, delete: true };
        break;
      }
      if (writeTag !== 1) {
        throw malformed("unknown write tag");
      }
      const count = reader.u32(truncated);
      if (count > syncV2MaxFields) {
        throw malformed("too many mutation fields");
      }
      const put: Record<string, SyncV2Value> = {};
      for (let index = 0; index < count; index++) {
        const key = reader.string(syncV2MaxIdentBytes, truncated);
        put[key] = reader.value(truncated);
      }
      body = { kind, collection, document, put };
      break;
    }
    case "mutation_ack": {
      const outcomeTag = reader.u8(truncated);
      if (outcomeTag === 1) {
        const storageSequence = reader.u64(truncated);
        const operationId = reader.string(syncV2MaxKeyBytes, truncated);
        body = { kind, outcome: "applied", storage_sequence: storageSequence, operation_id: operationId };
        break;
      }
      if (outcomeTag !== 2) {
        throw malformed("unknown outcome tag");
      }
      const reasonTag = reader.u8(truncated);
      const rejection = errorCodeNames[reasonTag];
      if (rejection === undefined) {
        throw malformed("unknown error code");
      }
      body = { kind, outcome: "rejected", rejection };
      break;
    }
  }
  reader.end();
  validateBody(body);
  return body;
}

// --- Frame seal / encode / decode -------------------------------------------

function fnv1a64(bytes: Uint8Array): bigint {
  let hash = fnvOffsetBasis;
  for (const byte of bytes) {
    hash ^= BigInt(byte);
    hash = (hash * fnvPrime) & u64Mask;
  }
  return hash;
}

function validateIdempotencyKey(key: string): void {
  if (key.length === 0 || key.trim().length === 0) {
    throw new SyncV2ProtocolError("invalid_idempotency_key", "the key is empty");
  }
  if (byteLength(key) > syncV2MaxKeyBytes) {
    throw new SyncV2ProtocolError("invalid_idempotency_key", "the key exceeds its bound");
  }
  for (const char of key) {
    const code = char.codePointAt(0)!;
    if (code < 0x20 || code === 0x7f) {
      throw new SyncV2ProtocolError("invalid_idempotency_key", "the key carries control characters");
    }
  }
}

function isMutationBody(body: SyncV2FrameBody): boolean {
  return body.kind === "mutation_request" || body.kind === "mutation_ack";
}

/** Refuses an envelope identity the u64 wire field cannot carry without wrapping. */
function validateU64Identity(name: string, value: bigint): void {
  if (value < 0n || value > u64Mask) {
    throw malformed(`${name} does not fit the u64 envelope field`);
  }
}

/**
 * Seals a body into a frame: validates the body, enforces the idempotency
 * pairing (a key exactly on the mutation-family frames), and computes the
 * content digest over the canonical body encoding.
 */
export function sealSyncV2Frame(
  body: SyncV2FrameBody,
  correlation: bigint,
  causation: bigint,
  idempotency?: string | undefined,
): SyncV2Frame {
  if (isMutationBody(body) !== (idempotency !== undefined)) {
    throw new SyncV2ProtocolError(
      "idempotency_pairing",
      isMutationBody(body)
        ? "a mutation frame requires its idempotency key"
        : "only mutation frames carry an idempotency key",
    );
  }
  if (idempotency !== undefined) {
    validateIdempotencyKey(idempotency);
  }
  // Refused here rather than masked in pushU64: a correlation or causation
  // outside u64 would silently wrap onto the wire as different bytes.
  validateU64Identity("the correlation", correlation);
  validateU64Identity("the causation", causation);
  const bodyBytes = syncV2BodyToBytes(body);
  return Object.freeze({
    correlation,
    causation,
    idempotency,
    digest: fnv1a64(bodyBytes),
    body,
  });
}

/** The versioned wire encoding of a sealed frame. */
export function encodeSyncV2Frame(frame: SyncV2Frame): Uint8Array {
  validateU64Identity("the correlation", frame.correlation);
  validateU64Identity("the causation", frame.causation);
  const keyBytes = frame.idempotency === undefined ? new Uint8Array(0) : textEncoder.encode(frame.idempotency);
  const bodyBytes = syncV2BodyToBytes(frame.body);
  // Encode-side symmetry with the decoder's bounds: a frame this module
  // emits must be one this module accepts. version(1) + kind(2) +
  // correlation(8) + causation(8) + presence(1) + digest(8) + body
  // length(8) = 36, plus the key length field when a key rides.
  const envelopeBytes = 36 + (frame.idempotency === undefined ? 0 : 8 + keyBytes.length);
  if (bodyBytes.length > syncV2MaxPayloadBytes) {
    throw malformed("body length exceeds the frame bound");
  }
  if (envelopeBytes + bodyBytes.length > syncV2MaxFrameBytes) {
    throw malformed("the frame exceeds the encode bound");
  }
  const digest = fnv1a64(bodyBytes);
  const bytes: number[] = [];
  bytes.push(syncV2ProtocolVersion);
  pushU16(bytes, kindCodes[frame.body.kind]);
  pushU64(bytes, frame.correlation);
  pushU64(bytes, frame.causation);
  if (frame.idempotency === undefined) {
    bytes.push(0);
  } else {
    bytes.push(1);
    pushU64(bytes, BigInt(keyBytes.length));
    for (const byte of keyBytes) {
      bytes.push(byte);
    }
  }
  pushU64(bytes, digest);
  pushU64(bytes, BigInt(bodyBytes.length));
  for (const byte of bodyBytes) {
    bytes.push(byte);
  }
  return new Uint8Array(bytes);
}

/**
 * Decodes a frame, failing closed on unknown versions (including v1),
 * unknown kinds, truncation, trailing bytes, length disagreement, limit
 * violations, and idempotency-pairing violations. Digest disagreement is
 * not a decode error — `verifySyncV2Frame` reports it.
 */
export function decodeSyncV2Frame(bytes: Uint8Array): SyncV2Frame {
  if (bytes.length > syncV2MaxFrameBytes) {
    throw malformed("the frame exceeds the decode bound");
  }
  if (bytes[0] !== syncV2ProtocolVersion) {
    throw new SyncV2ProtocolError("unsupported_version", `the wire version ${bytes[0] ?? 0} is not spoken`);
  }
  if (bytes.length < 20) {
    throw malformed("truncated frame encoding");
  }
  const reader = new ByteReader(bytes);
  void reader.take(1, "truncated frame encoding");
  const kindCode = reader.u16("truncated frame encoding");
  const correlation = reader.u64("truncated frame encoding");
  const causation = reader.u64("truncated frame encoding");
  const presence = reader.u8("truncated frame encoding");
  let idempotency: string | undefined;
  if (presence === 1) {
    const keyLength = Number(reader.u64("truncated frame encoding"));
    if (keyLength > syncV2MaxKeyBytes) {
      throw malformed("key length disagrees with the bytes");
    }
    const keyBytes = reader.take(keyLength, "truncated frame encoding");
    let key: string;
    try {
      key = textDecoder.decode(keyBytes);
    } catch {
      throw malformed("keys are utf-8");
    }
    validateIdempotencyKey(key);
    idempotency = key;
  } else if (presence !== 0) {
    throw malformed("unknown idempotency presence");
  }
  const digest = reader.u64("truncated frame encoding");
  const bodyLength = Number(reader.u64("truncated frame encoding"));
  if (bodyLength > syncV2MaxPayloadBytes) {
    throw malformed("body length exceeds the frame bound");
  }
  const bodyBytes = reader.take(bodyLength, "truncated frame encoding");
  if (reader.remaining !== 0) {
    throw malformed("trailing bytes after the frame body");
  }
  if (kindNames[kindCode] === undefined) {
    throw malformed("unknown frame kind");
  }
  const body = syncV2BodyFromBytes(bodyBytes);
  if (kindCodes[body.kind] !== kindCode) {
    throw malformed("the body kind disagrees with the envelope kind");
  }
  if (isMutationBody(body) !== (idempotency !== undefined)) {
    throw new SyncV2ProtocolError(
      "idempotency_pairing",
      isMutationBody(body)
        ? "a mutation frame requires its idempotency key"
        : "only mutation frames carry an idempotency key",
    );
  }
  return Object.freeze({ correlation, causation, idempotency, digest, body });
}

/** Whether a frame's sealed digest still matches its body. */
export function verifySyncV2Frame(frame: SyncV2Frame): boolean {
  const bodyBytes = syncV2BodyToBytes(frame.body);
  return fnv1a64(bodyBytes) === frame.digest;
}

/** The frame class whose written budget governs a frame kind. */
export function syncV2FrameClassOf(kind: SyncV2FrameKindName): SyncV2FrameClass {
  switch (kind) {
    case "session_setup":
    case "session_accepted":
      return "session";
    case "subscription_modify":
    case "subscription_changed":
      return "subscription";
    case "checkpoint_ack":
    case "mutation_ack":
      return "acknowledgement";
    case "heartbeat":
      return "heartbeat";
    case "delta_batch":
    case "snapshot_answer":
      return "delivery";
    case "trigger":
      return "trigger";
    case "resync_required":
    case "region_route_change":
    case "error":
      return "control";
    case "mutation_request":
      return "mutation";
  }
}

/** The payload byte length a frame carries (0 for payload-less classes). */
export function syncV2PayloadLengthOf(body: SyncV2FrameBody): number {
  return body.kind === "delta_batch" || body.kind === "snapshot_answer"
    ? body.payload.length / 2
    : 0;
}

/** The payload bytes of a delivery frame (the wrapped-codec decision keeps them opaque). */
export function syncV2PayloadBytesOf(body: SyncV2FrameBody): Uint8Array {
  return body.kind === "delta_batch" || body.kind === "snapshot_answer"
    ? decodeHexBytes(body.payload)
    : new Uint8Array(0);
}
