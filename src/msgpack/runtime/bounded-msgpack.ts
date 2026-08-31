// The bounded MessagePack core for the generated public `terrascale.sync.v2`
// protocol (ADR 0025). This module is the support library the generated
// codecs in `src/msgpack/generated/` build on; it is deliberately minimal and
// dependency-free. The public protocol manifest is the sole authority for
// message tags, field positions, and per-message bounds; this file knows
// nothing about any specific message.
//
// ADR 0025 security posture, implemented here:
//
//   - one whole binary WebSocket message is one complete MessagePack value
//     with explicit whole-message, string, binary, array, map, extension,
//     and depth limits — the permissive library defaults are never the
//     protocol limits;
//   - `scanMessagePack` performs an allocation-bounded structural walk over
//     the raw bytes (it builds no values) before any value decode starts;
//   - `MsgpackReader` is strict: exact array lengths, declared integer
//     ranges, fatal UTF-8, no float32, no maps, no extensions, no trailing
//     bytes, and no unsafe integers (a float where an exact integer type is
//     declared fails closed, as does any value outside its declared range);
//   - every rejection is one typed `TerrascaleMsgpackError`, so a hostile
//     message never yields a partially constructed public value.
//
// The protocol itself uses indexed arrays only (no maps, no extension
// types, no typeless or contractless modes), which is why maps and
// extensions fail closed at the scan.

/** The stable typed rejection vocabulary of the bounded MessagePack core. */
export type TerrascaleMsgpackErrorCode =
  | "limit_message"
  | "limit_string"
  | "limit_binary"
  | "limit_array"
  | "map_forbidden"
  | "extension_forbidden"
  | "float32_forbidden"
  | "depth_exceeded"
  | "truncated"
  | "trailing_bytes"
  | "invalid_type"
  | "invalid_shape"
  | "invalid_utf8"
  | "unsafe_integer"
  | "unsupported_version"
  | "unknown_feature"
  | "unknown_message_tag"
  | "unknown_enum"
  | "unknown_variant";

/** The one typed error the generated codecs throw on untrusted input. */
export class TerrascaleMsgpackError extends Error {
  public override readonly name = "TerrascaleMsgpackError";
  public readonly code: TerrascaleMsgpackErrorCode;

  public constructor(code: TerrascaleMsgpackErrorCode, reason: string) {
    super(`terrascale.msgpack: ${code}: ${reason}`);
    this.code = code;
  }
}

function fail(code: TerrascaleMsgpackErrorCode, reason: string): never {
  throw new TerrascaleMsgpackError(code, reason);
}

/** The scan-side limits, generated from the protocol manifest. */
export type TerrascaleMsgpackScanLimits = {
  /** The largest whole message accepted, checked before any byte parses. */
  readonly maxMessageBytes: number;
  /** The maximum nesting depth of arrays (the manifest protocol ceiling). */
  readonly maxDepth: number;
  /** The largest single string (bytes) anywhere in one message. */
  readonly maxStringBytes: number;
  /** The largest single binary blob (bytes) anywhere in one message. */
  readonly maxBinaryBytes: number;
  /** The largest element count of one array anywhere in one message. */
  readonly maxArrayItems: number;
  /** Map entries allowed; the indexed-array protocols declare 0 (forbidden). */
  readonly maxMapEntries: number;
  /** Whether MessagePack extension types may appear at all. */
  readonly extensionsAllowed: boolean;
  /** Whether float32 may appear at all (f64-only protocols forbid it). */
  readonly float32Allowed: boolean;
};

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder("utf-8", { fatal: true });

const u64Max = 0xffff_ffff_ffff_ffffn;
const i64Min = -(2n ** 63n);
const i64Max = 2n ** 63n - 1n;

/** Whether a string is Unicode scalar values only (no lone surrogates). */
export function msgpackTextIsScalar(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (next < 0xdc00 || next > 0xdfff) {
        return false;
      }
      index++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}

/** The UTF-8 encoded byte length of a string. */
export function msgpackUtf8ByteLength(value: string): number {
  return textEncoder.encode(value).length;
}

/** Strict per-character hex decode (no prefix or partial parsing). */
export function msgpackHexToBytes(hex: string): Uint8Array {
  if (hex.length % 2 !== 0) {
    fail("invalid_type", "hex carries an odd length");
  }
  const digit = (code: number): number => {
    if (code >= 48 && code <= 57) {
      return code - 48;
    }
    if (code >= 97 && code <= 102) {
      return code - 87;
    }
    if (code >= 65 && code <= 70) {
      return code - 55;
    }
    return -1;
  };
  const bytes = new Uint8Array(hex.length / 2);
  for (let index = 0; index < bytes.length; index++) {
    const highValue = digit(hex.charCodeAt(index * 2));
    const lowValue = digit(hex.charCodeAt(index * 2 + 1));
    if (highValue < 0 || lowValue < 0) {
      fail("invalid_type", "hex carries a non-hex digit");
    }
    bytes[index] = (highValue << 4) | lowValue;
  }
  return bytes;
}

/** Lowercase hex encoding of bytes. */
export function msgpackBytesToHex(bytes: Uint8Array): string {
  let hex = "";
  for (const byte of bytes) {
    hex += byte.toString(16).padStart(2, "0");
  }
  return hex;
}

// --- Structural scan ---------------------------------------------------------

/**
 * The allocation-bounded structural scan over raw untrusted bytes: walks one
 * complete MessagePack value building nothing, enforcing every scan limit.
 * Throws `TerrascaleMsgpackError` on any violation; a value that passes is
 * exactly one complete value with no trailing bytes.
 */
export function scanMessagePack(bytes: Uint8Array, limits: TerrascaleMsgpackScanLimits): void {
  if (bytes.length > limits.maxMessageBytes) {
    fail("limit_message", `the message is ${bytes.length} bytes; the bound is ${limits.maxMessageBytes}`);
  }
  let cursor = 0;
  let depth = 0;

  const take = (count: number): void => {
    if (cursor + count > bytes.length) {
      fail("truncated", "the message ends inside a value");
    }
    cursor += count;
  };

  const readLength = (width: number): number => {
    if (cursor + width > bytes.length) {
      fail("truncated", "the message ends inside a length prefix");
    }
    let length = 0;
    for (let index = 0; index < width; index++) {
      length = length * 256 + bytes[cursor + index]!;
    }
    cursor += width;
    return length;
  };

  const scanValue = (): void => {
    if (cursor >= bytes.length) {
      fail("truncated", "the message ends before a value");
    }
    const tag = bytes[cursor]!;
    if (tag <= 0x7f || tag >= 0xe0) {
      cursor += 1;
      return;
    }
    if (tag === 0xc0 || tag === 0xc2 || tag === 0xc3) {
      cursor += 1;
      return;
    }
    if (tag === 0xca) {
      if (!limits.float32Allowed) {
        fail("float32_forbidden", "float32 is not part of this protocol");
      }
      take(5);
      return;
    }
    if (tag === 0xcb) {
      take(9);
      return;
    }
    if (tag >= 0xc4 && tag <= 0xc6) {
      cursor += 1;
      const length = readLength(tag - 0xc4 === 0 ? 1 : tag - 0xc4 === 1 ? 2 : 4);
      if (length > limits.maxBinaryBytes) {
        fail("limit_binary", `a binary field declares ${length} bytes; the bound is ${limits.maxBinaryBytes}`);
      }
      take(length);
      return;
    }
    if (tag >= 0xc7 && tag <= 0xc9) {
      if (!limits.extensionsAllowed) {
        fail("extension_forbidden", "extension types are not part of this protocol");
      }
      cursor += 1;
      const headerLength = tag - 0xc7 === 0 ? 1 : tag - 0xc7 === 1 ? 2 : 4;
      const length = readLength(headerLength);
      take(1 + length);
      return;
    }
    if (tag >= 0xcc && tag <= 0xcf) {
      take(1 + (1 << (tag - 0xcc)));
      return;
    }
    if (tag >= 0xd0 && tag <= 0xd3) {
      take(1 + (1 << (tag - 0xd0)));
      return;
    }
    if (tag >= 0xd4 && tag <= 0xd8) {
      if (!limits.extensionsAllowed) {
        fail("extension_forbidden", "extension types are not part of this protocol");
      }
      take(1 + 1 + (1 << (tag - 0xd4)));
      return;
    }
    if ((tag >= 0xa0 && tag <= 0xbf) || (tag >= 0xd9 && tag <= 0xdb)) {
      let length: number;
      if (tag <= 0xbf) {
        length = tag - 0xa0;
        cursor += 1;
      } else {
        cursor += 1;
        length = readLength(tag === 0xd9 ? 1 : tag === 0xda ? 2 : 4);
      }
      if (length > limits.maxStringBytes) {
        fail("limit_string", `a string declares ${length} bytes; the bound is ${limits.maxStringBytes}`);
      }
      take(length);
      return;
    }
    if ((tag >= 0x90 && tag <= 0x9f) || tag === 0xdc || tag === 0xdd) {
      let count: number;
      if (tag <= 0x9f) {
        count = tag - 0x90;
        cursor += 1;
      } else {
        cursor += 1;
        count = readLength(tag === 0xdc ? 2 : 4);
      }
      if (count > limits.maxArrayItems) {
        fail("limit_array", `an array declares ${count} items; the bound is ${limits.maxArrayItems}`);
      }
      depth += 1;
      if (depth > limits.maxDepth) {
        fail("depth_exceeded", `nesting exceeds the depth bound of ${limits.maxDepth}`);
      }
      for (let index = 0; index < count; index++) {
        scanValue();
      }
      depth -= 1;
      return;
    }
    if ((tag >= 0x80 && tag <= 0x8f) || tag === 0xde || tag === 0xdf) {
      if (limits.maxMapEntries <= 0) {
        fail("map_forbidden", "maps are not part of this protocol");
      }
      let count: number;
      if (tag <= 0x8f) {
        count = tag - 0x80;
        cursor += 1;
      } else {
        cursor += 1;
        count = readLength(tag === 0xde ? 2 : 4);
      }
      if (count > limits.maxMapEntries) {
        fail("limit_array", `a map declares ${count} entries; the bound is ${limits.maxMapEntries}`);
      }
      depth += 1;
      if (depth > limits.maxDepth) {
        fail("depth_exceeded", `nesting exceeds the depth bound of ${limits.maxDepth}`);
      }
      for (let index = 0; index < count * 2; index++) {
        scanValue();
      }
      depth -= 1;
      return;
    }
    fail("invalid_type", `byte 0x${tag.toString(16).padStart(2, "0")} is never a MessagePack tag`);
  };

  scanValue();
  if (cursor !== bytes.length) {
    fail("trailing_bytes", `the message carries ${bytes.length - cursor} trailing bytes`);
  }
}

// --- Strict value reader -----------------------------------------------------

/**
 * The strict value reader the generated decoders use. It only ever walks
 * bytes the structural scan already accepted, and every accessor is exact:
 * arrays must match their declared length, integers their declared range,
 * strings their declared bound, and text must be UTF-8.
 */
export class MsgpackReader {
  readonly #bytes: Uint8Array;
  #cursor = 0;

  public constructor(bytes: Uint8Array) {
    this.#bytes = bytes;
  }

  public get remaining(): number {
    return this.#bytes.length - this.#cursor;
  }

  /** Fails closed unless the input is fully consumed. */
  public end(): void {
    if (this.remaining !== 0) {
      fail("trailing_bytes", `${this.remaining} bytes remain after the complete value`);
    }
  }

  #tag(): number {
    if (this.#cursor >= this.#bytes.length) {
      fail("truncated", "the message ends before a value");
    }
    const tag = this.#bytes[this.#cursor]!;
    this.#cursor += 1;
    return tag;
  }

  #take(count: number): Uint8Array {
    if (this.#cursor + count > this.#bytes.length) {
      fail("truncated", "the message ends inside a value");
    }
    const slice = this.#bytes.subarray(this.#cursor, this.#cursor + count);
    this.#cursor += count;
    return slice;
  }

  #length(width: number): number {
    const bytes = this.#take(width);
    let length = 0;
    for (const byte of bytes) {
      length = length * 256 + byte;
    }
    return length;
  }

  /** Reads a raw integer in every integer encoding; rejects floats outright. */
  #readIntegerCode(): number | bigint {
    const tag = this.#tag();
    if (tag <= 0x7f) {
      return tag;
    }
    if (tag >= 0xe0) {
      return tag - 0x100;
    }
    if (tag === 0xcc) {
      return this.#length(1);
    }
    if (tag === 0xcd) {
      return this.#length(2);
    }
    if (tag === 0xce) {
      return this.#length(4);
    }
    if (tag === 0xcf) {
      return bytesToBigInt(this.#take(8));
    }
    if (tag === 0xd0) {
      return signExtend(this.#length(1), 8);
    }
    if (tag === 0xd1) {
      return signExtend(this.#length(2), 16);
    }
    if (tag === 0xd2) {
      return signExtend(this.#length(4), 32);
    }
    if (tag === 0xd3) {
      const raw = bytesToBigInt(this.#take(8));
      return raw >= 1n << 63n ? raw - (1n << 64n) : raw;
    }
    if (tag === 0xca || tag === 0xcb) {
      fail("unsafe_integer", "a float appears where an exact integer is declared");
    }
    fail("invalid_type", `byte 0x${tag.toString(16).padStart(2, "0")} is not an integer`);
  }

  /** Requires an exact array length (indexed contracts: never a range). */
  public arrayHeader(expected: number): void {
    const tag = this.#tag();
    let count: number;
    if (tag >= 0x90 && tag <= 0x9f) {
      count = tag - 0x90;
    } else if (tag === 0xdc) {
      count = this.#length(2);
    } else if (tag === 0xdd) {
      count = this.#length(4);
    } else {
      fail("invalid_shape", "an array is declared at this position");
    }
    if (count !== expected) {
      fail("invalid_shape", `an array of exactly ${expected} elements is declared; ${count} arrived`);
    }
  }

  /**
   * Reads a variable-length array header for a manifest-declared list bound.
   * Exact-length contracts use `arrayHeader`; this is the bounded-variable
   * form for manifest list fields.
   */
  public arrayCount(maxItems: number): number {
    const tag = this.#tag();
    let count: number;
    if (tag >= 0x90 && tag <= 0x9f) {
      count = tag - 0x90;
    } else if (tag === 0xdc) {
      count = this.#length(2);
    } else if (tag === 0xdd) {
      count = this.#length(4);
    } else {
      fail("invalid_shape", "an array is declared at this position");
    }
    if (count > maxItems) {
      fail("limit_array", `the array bound at this position is ${maxItems} items; ${count} arrived`);
    }
    return count;
  }

  public nil(): null {
    const tag = this.#tag();
    if (tag !== 0xc0) {
      fail("invalid_type", "nil is declared at this position");
    }
    return null;
  }

  /** Consumes and reports a nil in an optional position. */
  public optionNil(): boolean {
    if (this.#cursor < this.#bytes.length && this.#bytes[this.#cursor] === 0xc0) {
      this.#cursor += 1;
      return true;
    }
    return false;
  }

  public bool(): boolean {
    const tag = this.#tag();
    if (tag === 0xc2) {
      return false;
    }
    if (tag === 0xc3) {
      return true;
    }
    fail("invalid_type", "a boolean is declared at this position");
  }

  /** A u32 value: any integer encoding whose value fits 0 .. 2^32-1. */
  public u32(): number {
    const value = this.#readIntegerCode();
    if (typeof value === "bigint") {
      if (value < 0n || value > 0xffff_ffffn) {
        fail("unsafe_integer", `a u32 is declared; ${value} does not fit`);
      }
      return Number(value);
    }
    if (!Number.isInteger(value) || value < 0 || value > 0xffff_ffff) {
      fail("unsafe_integer", `a u32 is declared; ${value} does not fit`);
    }
    return value;
  }

  /** A u64 value: decoded as bigint (useBigInt64 semantics), never a float. */
  public u64(): bigint {
    const value = this.#readIntegerCode();
    const asBigint = typeof value === "bigint" ? value : BigInt(value);
    if (asBigint < 0n || asBigint > u64Max) {
      fail("unsafe_integer", `a u64 is declared; ${asBigint} does not fit`);
    }
    return asBigint;
  }

  /** An i64 value: decoded as bigint, never a float. */
  public i64(): bigint {
    const value = this.#readIntegerCode();
    const asBigint = typeof value === "bigint" ? value : BigInt(value);
    if (asBigint < i64Min || asBigint > i64Max) {
      fail("unsafe_integer", `an i64 is declared; ${asBigint} does not fit`);
    }
    return asBigint;
  }

  public f64(): number {
    const tag = this.#tag();
    if (tag !== 0xcb) {
      fail("invalid_type", "a float64 is declared at this position");
    }
    const view = new DataView(this.#take(8).slice().buffer);
    return view.getFloat64(0, false);
  }

  public text(maxBytes: number): string {
    const tag = this.#tag();
    let length: number;
    if (tag >= 0xa0 && tag <= 0xbf) {
      length = tag - 0xa0;
    } else if (tag === 0xd9) {
      length = this.#length(1);
    } else if (tag === 0xda) {
      length = this.#length(2);
    } else if (tag === 0xdb) {
      length = this.#length(4);
    } else {
      fail("invalid_type", "a string is declared at this position");
    }
    if (length > maxBytes) {
      fail("limit_string", `the string bound at this position is ${maxBytes} bytes; ${length} arrived`);
    }
    const bytes = this.#take(length);
    try {
      return textDecoder.decode(bytes);
    } catch {
      fail("invalid_utf8", "strings are UTF-8");
    }
  }

  public binary(minBytes: number, maxBytes: number): Uint8Array {
    const tag = this.#tag();
    let length: number;
    if (tag === 0xc4) {
      length = this.#length(1);
    } else if (tag === 0xc5) {
      length = this.#length(2);
    } else if (tag === 0xc6) {
      length = this.#length(4);
    } else {
      fail("invalid_type", "a binary field is declared at this position");
    }
    if (length < minBytes || length > maxBytes) {
      fail("limit_binary", `the binary bound at this position is ${minBytes}..${maxBytes} bytes; ${length} arrived`);
    }
    return this.#take(length).slice();
  }
}

function bytesToBigInt(bytes: Uint8Array): bigint {
  let value = 0n;
  for (const byte of bytes) {
    value = (value << 8n) | BigInt(byte);
  }
  return value;
}

function signExtend(value: number, bits: number): number {
  return value >= 1 << (bits - 1) ? value - (1 << bits) : value;
}

// --- Canonical writer --------------------------------------------------------

/**
 * The canonical writer the generated encoders use: smallest integer format,
 * fixstr where it fits, bin8/16/32, fixarray where it fits. Output is
 * deterministic for equal public values.
 */
export class MsgpackWriter {
  readonly #bytes: number[] = [];

  public get length(): number {
    return this.#bytes.length;
  }

  public finish(maxMessageBytes: number): Uint8Array {
    if (this.#bytes.length > maxMessageBytes) {
      fail("limit_message", `the encoded message is ${this.#bytes.length} bytes; the bound is ${maxMessageBytes}`);
    }
    return new Uint8Array(this.#bytes);
  }

  public arrayHeader(count: number): void {
    if (count <= 0x0f) {
      this.#bytes.push(0x90 | count);
    } else if (count <= 0xffff) {
      this.#bytes.push(0xdc, (count >>> 8) & 0xff, count & 0xff);
    } else {
      this.#bytes.push(0xdd, (count >>> 24) & 0xff, (count >>> 16) & 0xff, (count >>> 8) & 0xff, count & 0xff);
    }
  }

  public nil(): void {
    this.#bytes.push(0xc0);
  }

  public bool(value: boolean): void {
    this.#bytes.push(value ? 0xc3 : 0xc2);
  }

  public u32(value: number): void {
    if (!Number.isInteger(value) || value < 0 || value > 0xffff_ffff) {
      fail("unsafe_integer", `a u32 is declared; ${value} does not fit`);
    }
    this.#uint(BigInt(value));
  }

  public u64(value: bigint): void {
    if (value < 0n || value > u64Max) {
      fail("unsafe_integer", `a u64 is declared; ${value} does not fit`);
    }
    this.#uint(value);
  }

  public i64(value: bigint): void {
    if (value < i64Min || value > i64Max) {
      fail("unsafe_integer", `an i64 is declared; ${value} does not fit`);
    }
    if (value >= 0n) {
      this.#uint(value);
      return;
    }
    if (value >= -32n) {
      this.#bytes.push(Number(value & 0xffn));
    } else if (value >= -128n) {
      this.#bytes.push(0xd0, Number(value & 0xffn));
    } else if (value >= -32768n) {
      const masked = Number(value & 0xffffn);
      this.#bytes.push(0xd1, (masked >>> 8) & 0xff, masked & 0xff);
    } else if (value >= -2147483648n) {
      const masked = Number(value & 0xffffffffn);
      this.#bytes.push(0xd2, (masked >>> 24) & 0xff, (masked >>> 16) & 0xff, (masked >>> 8) & 0xff, masked & 0xff);
    } else {
      // Two's-complement 64-bit bytes, big-endian, after the int64 marker.
      let rest = value & u64Max;
      const bytes: number[] = [];
      for (let index = 0; index < 8; index++) {
        bytes.push(Number(rest & 0xffn));
        rest >>= 8n;
      }
      bytes.reverse();
      this.#bytes.push(0xd3, ...bytes);
    }
  }

  #uint(value: bigint): void {
    if (value <= 0x7fn) {
      this.#bytes.push(Number(value));
    } else if (value <= 0xffn) {
      this.#bytes.push(0xcc, Number(value));
    } else if (value <= 0xffffn) {
      const narrow = Number(value);
      this.#bytes.push(0xcd, (narrow >>> 8) & 0xff, narrow & 0xff);
    } else if (value <= 0xffffffffn) {
      const narrow = Number(value);
      this.#bytes.push(0xce, (narrow >>> 24) & 0xff, (narrow >>> 16) & 0xff, (narrow >>> 8) & 0xff, narrow & 0xff);
    } else {
      let rest = value;
      const bytes: number[] = [];
      for (let index = 0; index < 8; index++) {
        bytes.push(Number(rest & 0xffn));
        rest >>= 8n;
      }
      bytes.reverse();
      this.#bytes.push(0xcf, ...bytes);
    }
  }

  public f64(value: number): void {
    // Every float64 passes through symmetrically with the decoder; the
    // manifest declares the type, and the exact-shape validator enforces it.
    const view = new DataView(new ArrayBuffer(8));
    view.setFloat64(0, value, false);
    this.#bytes.push(0xcb, ...new Uint8Array(view.buffer));
  }

  public text(value: string, maxBytes: number): void {
    if (!msgpackTextIsScalar(value)) {
      fail("invalid_type", "strings are Unicode scalar values");
    }
    const encoded = textEncoder.encode(value);
    if (encoded.length > maxBytes) {
      fail("limit_string", `the string bound at this position is ${maxBytes} bytes; ${encoded.length} arrived`);
    }
    const length = encoded.length;
    if (length <= 0x1f) {
      this.#bytes.push(0xa0 | length);
    } else if (length <= 0xff) {
      this.#bytes.push(0xd9, length);
    } else if (length <= 0xffff) {
      this.#bytes.push(0xda, (length >>> 8) & 0xff, length & 0xff);
    } else {
      this.#bytes.push(0xdb, (length >>> 24) & 0xff, (length >>> 16) & 0xff, (length >>> 8) & 0xff, length & 0xff);
    }
    for (const byte of encoded) {
      this.#bytes.push(byte);
    }
  }

  public binary(value: Uint8Array, minBytes: number, maxBytes: number): void {
    if (value.length < minBytes || value.length > maxBytes) {
      fail("limit_binary", `the binary bound at this position is ${minBytes}..${maxBytes} bytes; ${value.length} arrived`);
    }
    const length = value.length;
    if (length <= 0xff) {
      this.#bytes.push(0xc4, length);
    } else if (length <= 0xffff) {
      this.#bytes.push(0xc5, (length >>> 8) & 0xff, length & 0xff);
    } else {
      this.#bytes.push(0xc6, (length >>> 24) & 0xff, (length >>> 16) & 0xff, (length >>> 8) & 0xff, length & 0xff);
    }
    for (const byte of value) {
      this.#bytes.push(byte);
    }
  }
}
