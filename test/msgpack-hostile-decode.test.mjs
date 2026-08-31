// Hostile-decode conformance for the generated public
// `terrascale.sync.v2.msgpack` protocol (ADR 0025, qualification-gap-sweep
// row 28): truncated, trailing, oversized, deeply nested, unknown-tag,
// unsafe-integer, version-skew, unknown-feature, extra-field, map,
// extension, float32, invalid-UTF-8, unknown-enum, unknown-variant, and
// wrong-length-binary messages must all be rejected with a typed
// TerrascaleMsgpackError BEFORE a public value is constructed.
//
// The hostile bytes are built by hand from raw MessagePack tags (not with
// the SDK's own writer) so the corpus of attacks is independent of the
// encoder under test.
//
// Run (imports the built SDK tree):
//
//   npm run build && node --test test/msgpack-hostile-decode.test.mjs

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import { TerrascaleMsgpackError } from "../dist/msgpack/runtime/bounded-msgpack.js";
import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  terrascaleSyncV2MsgpackManifestSha256,
  terrascaleSyncV2MsgpackMaxMessageBytes,
} from "../dist/msgpack/generated/terrascale-sync-v2-msgpack.js";

const encoder = new TextEncoder();
const goldenHostileFixture = JSON.parse(await readFile(
  new URL("./fixtures/terrascale.sync.v2.msgpack/hostile-vectors.json", import.meta.url),
  "utf8",
));

const bytes = (...parts) => {
  // Assembles raw MessagePack bytes without spreading large parts (the
  // oversized-message case carries an 8 MiB payload).
  const out = [];
  const add = (part) => {
    if (part instanceof Uint8Array) {
      for (const byte of part) {
        out.push(byte);
      }
    } else if (Array.isArray(part)) {
      for (const entry of part) {
        add(entry);
      }
    } else {
      out.push(part);
    }
  };
  for (const part of parts) {
    add(part);
  }
  return Uint8Array.from(out);
};

const u64 = (value) => {
  let rest = BigInt(value);
  const out = [0xcf];
  const little = [];
  for (let index = 0; index < 8; index++) {
    little.push(Number(rest & 0xffn));
    rest >>= 8n;
  }
  little.reverse();
  return [...out, ...little];
};

const i64 = (value) => {
  let rest = BigInt(value) & 0xffff_ffff_ffff_ffffn;
  const little = [];
  for (let index = 0; index < 8; index++) {
    little.push(Number(rest & 0xffn));
    rest >>= 8n;
  }
  little.reverse();
  return [0xd3, ...little];
};

const f64 = (value) => {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, value, false);
  return [0xcb, ...new Uint8Array(view.buffer)];
};

const f32 = (value) => {
  const view = new DataView(new ArrayBuffer(4));
  view.setFloat32(0, value, false);
  return [0xca, ...new Uint8Array(view.buffer)];
};

const str = (text) => {
  const encoded = encoder.encode(text);
  if (encoded.length <= 0x1f) {
    return [0xa0 | encoded.length, ...encoded];
  }
  if (encoded.length <= 0xff) {
    return [0xd9, encoded.length, ...encoded];
  }
  if (encoded.length <= 0xffff) {
    return [0xda, (encoded.length >>> 8) & 0xff, encoded.length & 0xff, ...encoded];
  }
  return [
    0xdb,
    (encoded.length >>> 24) & 0xff,
    (encoded.length >>> 16) & 0xff,
    (encoded.length >>> 8) & 0xff,
    encoded.length & 0xff,
    ...encoded,
  ];
};

const bin32Header = (length) => [
  0xc6,
  (length >>> 24) & 0xff,
  (length >>> 16) & 0xff,
  (length >>> 8) & 0xff,
  length & 0xff,
];

/** A hand-built heartbeat envelope: [major, minor, features, tag, corr, caus, []]. */
const heartbeatEnvelope = (overrides = {}) => {
  const major = overrides.major ?? [0x02];
  const minor = overrides.minor ?? [0x01];
  const features = overrides.features ?? [0x00];
  const tag = overrides.tag ?? [0x06];
  const correlation = overrides.correlation ?? u64(1n);
  const causation = overrides.causation ?? u64(0n);
  const body = overrides.body ?? [0x90];
  return bytes([0x97], major, minor, features, tag, correlation, causation, body);
};

const expectRejection = (code, wire, note) => {
  let captured;
  try {
    captured = decodeTerrascaleSyncV2MsgpackEnvelope(wire);
  } catch (error) {
    assert.ok(error instanceof TerrascaleMsgpackError, `${note}: expected TerrascaleMsgpackError, got ${error}`);
    assert.equal(error.code, code, `${note}: expected code ${code}, got ${error.code} (${error.message})`);
    return;
  }
  assert.fail(`${note}: decode returned a public value: ${JSON.stringify(captured, (key, value) => (typeof value === "bigint" ? value.toString() : value))}`);
};

describe("msgpack hostile decode", () => {
  test("committed independent vectors pin every typed hostile boundary", () => {
    const expectedCodes = [
      "limit_message",
      "limit_string",
      "limit_binary",
      "limit_array",
      "map_forbidden",
      "extension_forbidden",
      "float32_forbidden",
      "depth_exceeded",
      "truncated",
      "trailing_bytes",
      "invalid_type",
      "invalid_shape",
      "invalid_utf8",
      "unsafe_integer",
      "unsupported_version",
      "unknown_feature",
      "unknown_message_tag",
      "unknown_enum",
      "unknown_variant",
    ];
    assert.equal(goldenHostileFixture.protocol, "terrascale.sync.v2.msgpack");
    assert.equal(goldenHostileFixture.version, "2.1");
    assert.equal(goldenHostileFixture.manifestSha256, terrascaleSyncV2MsgpackManifestSha256);
    assert.deepEqual(
      goldenHostileFixture.vectors.map(vector => vector.code).sort(),
      [...expectedCodes].sort(),
    );
    for (const vector of goldenHostileFixture.vectors) {
      const wire = vector.hex === undefined
        ? new Uint8Array(vector.repeat.count).fill(vector.repeat.byte)
        : Uint8Array.from(Buffer.from(vector.hex, "hex"));
      expectRejection(vector.code, wire, `golden vector: ${vector.name}`);
    }
  });

  test("a hand-built canonical heartbeat decodes (positive control)", () => {
    const decoded = decodeTerrascaleSyncV2MsgpackEnvelope(heartbeatEnvelope());
    assert.equal(decoded.message.kind, "heartbeat");
    assert.equal(decoded.correlation, 1n);
    assert.equal(decoded.causation, 0n);
    assert.equal(decoded.features, 0);
  });

  test("non-canonical integer encodings still decode (cross-producer tolerance)", () => {
    // major/minor/features/tag as uint16, correlation as uint8-padded value:
    // semantic readers must agree on the value regardless of encoding width.
    const decoded = decodeTerrascaleSyncV2MsgpackEnvelope(
      heartbeatEnvelope({
        major: [0xcd, 0x00, 0x02],
        minor: [0xcc, 0x01],
        features: [0xce, 0x00, 0x00, 0x00, 0x00],
        tag: [0xcd, 0x00, 0x06],
        correlation: [0xcc, 0x2a],
      }),
    );
    assert.equal(decoded.message.kind, "heartbeat");
    assert.equal(decoded.correlation, 42n);
  });

  // --- truncation -------------------------------------------------------------

  test("every strict prefix of a valid message is rejected", () => {
    const envelope = encodeTerrascaleSyncV2MsgpackEnvelope({
      features: 0,
      correlation: 7n,
      causation: 3n,
      message: {
        kind: "subscribe",
        subscription: "orders",
        query: "from orders",
        parameters: encoder.encode("open"),
        checkpoint: encoder.encode("checkpoint"),
      },
    });
    assert.ok(envelope.length > 8);
    for (let length = 0; length < envelope.length; length++) {
      const prefix = envelope.subarray(0, length);
      let threw = null;
      try {
        decodeTerrascaleSyncV2MsgpackEnvelope(prefix);
      } catch (error) {
        threw = error;
      }
      assert.ok(threw instanceof TerrascaleMsgpackError, `prefix of ${length} bytes decoded`);
    }
  });

  test("a truncated variable-length body list is rejected", () => {
    const body = bytes([0x94], str("view"), str("from docs"), [0xc4, 0x03, 0x01]);
    expectRejection("truncated", heartbeatEnvelope({ tag: [0x01], body }), "truncated subscription body");
  });

  // --- trailing bytes -----------------------------------------------------------

  test("trailing bytes after a complete message are rejected", () => {
    expectRejection("trailing_bytes", bytes(heartbeatEnvelope(), [0xc0]), "one trailing nil");
    expectRejection("trailing_bytes", bytes(heartbeatEnvelope(), [0x01]), "one trailing fixint");
  });

  // --- oversized ----------------------------------------------------------------

  test("a whole message above the 8 MiB ceiling is rejected before any parse", () => {
    const stringBytes = terrascaleSyncV2MsgpackMaxMessageBytes + 1 - 24;
    const giant = bytes(
      [0x97, 0x02, 0x00, 0x00, 0x06],
      u64(1n),
      u64(0n),
      [0x91],
      [0xdb, (stringBytes >>> 24) & 0xff, (stringBytes >>> 16) & 0xff, (stringBytes >>> 8) & 0xff, stringBytes & 0xff],
      new Uint8Array(stringBytes),
    );
    assert.ok(giant.length > terrascaleSyncV2MsgpackMaxMessageBytes);
    expectRejection("limit_message", giant, "whole message above the ceiling");
  });

  test("a binary field declaring more than its 1 MiB bound is rejected", () => {
    const body = bytes([0x94], str("view"), str("from docs"), bin32Header(1048577));
    expectRejection(
      "limit_binary",
      heartbeatEnvelope({ tag: [0x01], body }),
      "declared parameters above the binary bound",
    );
  });

  test("a string field above its per-message bound is rejected", () => {
    const body = bytes([0x91], str("r".repeat(65)));
    expectRejection(
      "limit_string",
      heartbeatEnvelope({ tag: [0x09], body }),
      "region above its 64-byte bound",
    );
  });

  test("a list field above its per-message item bound is rejected", () => {
    const body = bytes([0x94], str("view"), u64(2n), [0xdc, 0x27, 0x11]);
    expectRejection(
      "limit_array",
      heartbeatEnvelope({ tag: [0x08], body }),
      "10001 authorization removals",
    );
  });

  test("a projected field list above its bound is rejected", () => {
    const fields = [];
    for (let index = 0; index < 1025; index++) {
      fields.push([0x92], str("p"), [0xc4, 0x00]);
    }
    const body = bytes(
      [0x92], str("view"),
      [0x96], u64(1n), u64(1n), u64(1n),
      [0x91], [0x92], str("document"), [0xdc, 0x04, 0x01], ...fields,
      str("a".repeat(64)), [0xc4, 0x01, 0x01],
    );
    expectRejection(
      "limit_array",
      heartbeatEnvelope({ tag: [0x03], body }),
      "1025 projected fields",
    );
  });

  test("the aggregate projected-value bound is enforced by the raw decoder", () => {
    const valueLength = 524_289;
    const body = bytes(
      [0x92], str("view"),
      [0x96], u64(1n), u64(1n), u64(1n),
      [0x91], [0x92], str("document"), [0x92],
      [0x92], str("first"), bin32Header(valueLength), new Uint8Array(valueLength),
      [0x92], str("second"), bin32Header(valueLength), new Uint8Array(valueLength),
      str("a".repeat(64)), [0xc4, 0x01, 0x00],
    );
    expectRejection(
      "limit_binary",
      heartbeatEnvelope({ tag: [0x03], body }),
      "projected values above their aggregate binary bound",
    );
  });

  test("an empty projected path is rejected by the raw decoder", () => {
    const body = bytes(
      [0x92], str("view"),
      [0x96], u64(1n), u64(1n), u64(1n),
      [0x91], [0x92], str("document"), [0x91],
      [0x92], str(""), [0xc4, 0x00],
      str("a".repeat(64)), [0xc4, 0x01, 0x00],
    );
    expectRejection(
      "invalid_shape",
      heartbeatEnvelope({ tag: [0x03], body }),
      "empty projected path",
    );
  });

  // --- deep nesting --------------------------------------------------------------

  test("nesting beyond the depth ceiling is rejected by the structural scan", () => {
    const depth = 100;
    const nested = [0x91];
    for (let index = 0; index < depth; index++) {
      nested.push(0x91);
    }
    nested.push(0xc0);
    expectRejection(
      "depth_exceeded",
      heartbeatEnvelope({ tag: [0x06], body: bytes(nested) }),
      "100 nested arrays",
    );
  });

  // --- unknown message tags --------------------------------------------------------

  test("unknown message tags are rejected", () => {
    expectRejection("unknown_message_tag", heartbeatEnvelope({ tag: [0x0c] }), "tag 12 (one past the manifest)");
    expectRejection("unknown_message_tag", heartbeatEnvelope({ tag: [0xcd, 0x4f, 0xf1] }), "tag 0x4ff1");
    expectRejection("unknown_message_tag", heartbeatEnvelope({ tag: [0xcc, 0x63] }), "tag 99");
  });

  // --- unsafe integers ---------------------------------------------------------------

  test("a float where the version integer is declared is rejected", () => {
    expectRejection("unsafe_integer", heartbeatEnvelope({ major: f64(2) }), "major as float64 2.0");
  });

  test("a float where a u64 envelope identity is declared is rejected", () => {
    expectRejection("unsafe_integer", heartbeatEnvelope({ correlation: f64(1) }), "correlation as float64");
  });

  test("a negative int64 where a u64 is declared is rejected", () => {
    expectRejection("unsafe_integer", heartbeatEnvelope({ correlation: i64(-1n) }), "negative correlation");
  });

  test("a u32 field above 2^32 is rejected", () => {
    expectRejection(
      "unsafe_integer",
      heartbeatEnvelope({ tag: [0xcf, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06] }),
      "tag encoded as uint64 2^32+6",
    );
    expectRejection("unsafe_integer", heartbeatEnvelope({ features: f64(0) }), "features as float64 0.0");
  });

  test("a float where a u64 counter is declared is rejected", () => {
    const body = bytes(
      [0x97], str("view"), f64(1), u64(1n), u64(2n),
      [0x90], str("a".repeat(64)), [0xc4, 0x01, 0x01],
    );
    expectRejection("unsafe_integer", heartbeatEnvelope({ tag: [0x04], body }), "view generation as float64");
  });

  // --- version skew ---------------------------------------------------------------------

  test("version skew is rejected before any body field is read", () => {
    for (const major of [0, 1, 3]) {
      expectRejection("unsupported_version", heartbeatEnvelope({ major: [major] }), `major ${major}`);
    }
    // 0xff is the negative fixint -1: not a u32 at all, so it fails as an
    // unsafe integer — still fail-closed before any body field is read.
    expectRejection("unsafe_integer", heartbeatEnvelope({ major: [0xff] }), "major -1 (0xff)");
    expectRejection("unsupported_version", heartbeatEnvelope({ minor: [0x00] }), "withdrawn pre-launch minor 0");
    expectRejection("unsupported_version", heartbeatEnvelope({ minor: [0x02] }), "future minor 2");
    expectRejection("unsafe_integer", heartbeatEnvelope({ major: f64(3) }), "skewed major as a float");
  });

  // --- unknown features -------------------------------------------------------------------

  test("unknown required feature bits are rejected", () => {
    expectRejection("unknown_feature", heartbeatEnvelope({ features: [0x01] }), "feature bit 0");
    expectRejection(
      "unknown_feature",
      heartbeatEnvelope({ features: [0xce, 0x80, 0x00, 0x00, 0x00] }),
      "feature bit 31",
    );
  });

  // --- exact shape: extra and duplicate semantic fields -------------------------------------

  test("an envelope with extra elements is rejected", () => {
    expectRejection(
      "invalid_shape",
      bytes([0x98], [0x02], [0x00], [0x00], [0x06], u64(1n), u64(0n), [0x90], [0x90]),
      "eight-element envelope",
    );
  });

  test("a body with an extra element is rejected", () => {
    expectRejection(
      "invalid_shape",
      heartbeatEnvelope({ tag: [0x06], body: [0x91, 0xc0] }),
      "heartbeat body with one element",
    );
    const body = bytes([0x98], str("view"), u64(1n), u64(1n), u64(2n), [0x90], str("a".repeat(64)), [0xc4, 0x01, 0x01], [0xc2]);
    expectRejection(
      "invalid_shape",
      heartbeatEnvelope({ tag: [0x04], body }),
      "delta body with an extra field",
    );
  });

  test("a union with the wrong arity for its discriminant is rejected", () => {
    const bodyFor = (operation) => bytes(
      [0x97], str("view"), u64(1n), u64(1n), u64(2n),
      [0x91], operation, str("a".repeat(64)), [0xc4, 0x01, 0x01],
    );
    const shortValue = bytes([0x91], [0x01]);
    expectRejection(
      "invalid_shape",
      heartbeatEnvelope({ tag: [0x04], body: bodyFor(shortValue) }),
      "insert operation missing its fields",
    );
    // ...and an extra element breaches the union's bounded array first.
    const longValue = bytes([0x95], [0x01], str("document"), [0x00], [0x90], [0xc0]);
    expectRejection(
      "limit_array",
      heartbeatEnvelope({ tag: [0x04], body: bodyFor(longValue) }),
      "insert operation with an extra element",
    );
  });

  // --- forbidden structures ---------------------------------------------------------------

  test("a map anywhere in the message is rejected", () => {
    expectRejection(
      "map_forbidden",
      heartbeatEnvelope({ tag: [0x06], body: [0x81, 0x01, 0x02] }),
      "fixmap in the body",
    );
    expectRejection(
      "map_forbidden",
      heartbeatEnvelope({ tag: [0x06], body: [0xde, 0x00, 0x01, 0x01, 0x02] }),
      "map16 in the body",
    );
  });

  test("extension types are rejected anywhere", () => {
    expectRejection(
      "extension_forbidden",
      heartbeatEnvelope({ tag: [0x06], body: [0xd4, 0x00, 0x00] }),
      "fixext1 in the body",
    );
    expectRejection(
      "extension_forbidden",
      heartbeatEnvelope({ tag: [0x06], body: [0xc7, 0x01, 0x01, 0x5a] }),
      "ext8 in the body",
    );
  });

  test("float32 is rejected where float64 is the declared width", () => {
    expectRejection(
      "float32_forbidden",
      heartbeatEnvelope({ body: bytes([0x91], f32(1.5)) }),
      "float32 anywhere in a frame",
    );
  });

  test("invalid UTF-8 text is rejected", () => {
    const body = bytes([0x91], [0xa1, 0xff]);
    expectRejection(
      "invalid_utf8",
      heartbeatEnvelope({ tag: [0x09], body }),
      "region with byte 0xff",
    );
  });

  // --- closed vocabularies ------------------------------------------------------------------

  test("unknown enum values are rejected", () => {
    // error message: [code, detail]
    const body = bytes([0x92], [0xcc, 99], [0xc0]);
    expectRejection("unknown_enum", heartbeatEnvelope({ tag: [0x0a], body }), "error code 99");
    const resetBody = bytes(
      [0x93], str("view"), [0xcc, 99],
      [0x96], u64(1n), u64(1n), u64(1n), [0x90], str("a".repeat(64)), [0xc4, 0x01, 0x01],
    );
    expectRejection(
      "unknown_enum",
      heartbeatEnvelope({ tag: [0x05], body: resetBody }),
      "reset reason 99",
    );
  });

  test("unknown union discriminants are rejected", () => {
    const operation = bytes([0x91], [0x07]);
    const body = bytes(
      [0x97], str("view"), u64(1n), u64(1n), u64(2n),
      [0x91], operation, str("a".repeat(64)), [0xc4, 0x01, 0x01],
    );
    expectRejection(
      "unknown_variant",
      heartbeatEnvelope({ tag: [0x04], body }),
      "result operation discriminant 7",
    );
  });

  test("a bounded binary field below its minimum length is rejected", () => {
    const body = bytes([0x92], str("view"), [0xc4, 0x00]);
    expectRejection(
      "limit_binary",
      heartbeatEnvelope({ tag: [0x07], body }),
      "empty checkpoint",
    );
  });

  test("the never-valid tag byte 0xc1 is rejected", () => {
    expectRejection("invalid_type", bytes([0x97, 0xc1]), "reserved byte in the envelope");
  });
});
