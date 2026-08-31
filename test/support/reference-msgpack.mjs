// A deliberately small, test-only MessagePack writer used as an independent
// oracle for committed protocol bytes. It does not import the SDK runtime,
// generated codecs, or the protocol manifest.

const textEncoder = new TextEncoder();

export function encodeReferenceMessagePack(value) {
  const bytes = [];
  encode(value, bytes);
  return Uint8Array.from(bytes);
}

function encode(value, bytes) {
  if (value === null) {
    bytes.push(0xc0);
    return;
  }
  if (value instanceof Uint8Array) {
    binary(value, bytes);
    return;
  }
  if (Array.isArray(value)) {
    arrayHeader(value.length, bytes);
    for (const element of value) encode(element, bytes);
    return;
  }
  if (typeof value === "string") {
    string(value, bytes);
    return;
  }
  if (typeof value === "number" && Number.isSafeInteger(value) && value >= 0) {
    unsigned(BigInt(value), bytes);
    return;
  }
  if (typeof value === "bigint" && value >= 0n && value <= 0xffff_ffff_ffff_ffffn) {
    unsigned(value, bytes);
    return;
  }
  throw new TypeError(`the independent MessagePack oracle does not support ${String(value)}`);
}

function unsigned(value, bytes) {
  if (value <= 0x7fn) {
    bytes.push(Number(value));
  } else if (value <= 0xffn) {
    bytes.push(0xcc, Number(value));
  } else if (value <= 0xffffn) {
    bytes.push(0xcd, Number(value >> 8n), Number(value & 0xffn));
  } else if (value <= 0xffff_ffffn) {
    bytes.push(0xce);
    pushBigEndian(value, 4, bytes);
  } else {
    bytes.push(0xcf);
    pushBigEndian(value, 8, bytes);
  }
}

function string(value, bytes) {
  const encoded = textEncoder.encode(value);
  if (encoded.length <= 31) {
    bytes.push(0xa0 | encoded.length);
  } else if (encoded.length <= 0xff) {
    bytes.push(0xd9, encoded.length);
  } else if (encoded.length <= 0xffff) {
    bytes.push(0xda, encoded.length >>> 8, encoded.length & 0xff);
  } else {
    bytes.push(0xdb);
    pushBigEndian(BigInt(encoded.length), 4, bytes);
  }
  bytes.push(...encoded);
}

function binary(value, bytes) {
  if (value.length <= 0xff) {
    bytes.push(0xc4, value.length);
  } else if (value.length <= 0xffff) {
    bytes.push(0xc5, value.length >>> 8, value.length & 0xff);
  } else {
    bytes.push(0xc6);
    pushBigEndian(BigInt(value.length), 4, bytes);
  }
  bytes.push(...value);
}

function arrayHeader(length, bytes) {
  if (length <= 15) {
    bytes.push(0x90 | length);
  } else if (length <= 0xffff) {
    bytes.push(0xdc, length >>> 8, length & 0xff);
  } else {
    bytes.push(0xdd);
    pushBigEndian(BigInt(length), 4, bytes);
  }
}

function pushBigEndian(value, width, bytes) {
  for (let shift = (width - 1) * 8; shift >= 0; shift -= 8) {
    bytes.push(Number((value >> BigInt(shift)) & 0xffn));
  }
}
