const MAGIC = "TBS1";
const HEADER_BYTES = 70;
const KEY_ID_BYTES = 26;
const FRAME_BYTES = 1048576;
const MAX_PLAINTEXT_BYTES = 8388608;
const TAG_BYTES = 16;
const LAST_FRAME_FLAG = 0x80000000;
const LENGTH_MASK = 0x7fffffff;
const MAX_UINT64 = 2n ** 64n - 1n;

/**
 * @typedef {object} ShapeObjectRequest
 * @property {string} handle The shape handle this client requested, e.g. `h1-…`.
 * @property {string} offset Canonical offset of the object's URL, e.g. `2` or `snap.0.0`.
 * @property {string} epoch Decimal epoch the client expects in the header.
 * @property {string} keyId The 26-character project data key id the client expects in the header.
 * @property {Uint8Array} key Raw 32-byte epoch key `K_e`.
 */

/**
 * Verifies and decrypts one TBS1 sealed shape object. The plaintext is returned only when the
 * header, every frame tag and the framing rules pass. Any failure throws and yields no bytes.
 *
 * @param {Uint8Array} input Exact TBS1 object bytes.
 * @param {ShapeObjectRequest} request
 * @returns {Promise<Uint8Array>}
 */
export async function decryptShapeObject(input, { handle, offset, epoch, keyId, key: keyInput }) {
  if (input.byteLength > HEADER_BYTES + 9 * (4 + TAG_BYTES) + MAX_PLAINTEXT_BYTES)
    throw new TypeError("TBS1 object exceeds the maximum sealed size");
  // WebCrypto needs ArrayBuffer-backed views; copying keeps the caller's (possibly shared) buffer out of it.
  const bytes = Uint8Array.from(input);
  const key = Uint8Array.from(keyInput);
  if (key.byteLength !== 32) throw new TypeError("TBS1 epoch key must be 32 bytes");
  if (typeof keyId !== "string" || !/^[\x00-\x7f]{26}$/u.test(keyId))
    throw new TypeError("TBS1 key id must be 26 ASCII characters");
  if (bytes.byteLength < HEADER_BYTES) throw new TypeError("TBS1 header is short");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (new TextDecoder().decode(bytes.subarray(0, 4)) !== MAGIC) throw new TypeError("TBS1 magic mismatch");
  if (view.getBigUint64(4) !== parseEpoch(epoch)) throw new TypeError("TBS1 epoch mismatch");
  if (new TextDecoder().decode(bytes.subarray(12, 12 + KEY_ID_BYTES)) !== keyId) throw new TypeError("TBS1 key id mismatch");

  const header = bytes.subarray(0, HEADER_BYTES);
  const { subtle } = globalThis.crypto;
  const epochKey = await subtle.importKey("raw", key, "HKDF", false, ["deriveKey"]);
  const objectKey = await subtle.deriveKey(
    {
      name: "HKDF",
      hash: "SHA-256",
      salt: bytes.slice(38, HEADER_BYTES),
      info: new TextEncoder().encode(`TBS1 object\u0000${handle}\u0000${offset}`),
    },
    epochKey,
    { name: "AES-GCM", length: 256 },
    false,
    ["decrypt"],
  );

  const plaintextParts = [];
  let plaintextTotal = 0;
  let position = HEADER_BYTES;
  for (let index = 0; ; index += 1) {
    if (position + 4 > bytes.byteLength) throw new TypeError("TBS1 object ends before its last frame");
    const lengthWord = view.getUint32(position);
    const last = (lengthWord & LAST_FRAME_FLAG) !== 0;
    const length = lengthWord & LENGTH_MASK;
    if (last ? length >= FRAME_BYTES : length !== FRAME_BYTES) throw new TypeError("TBS1 frame length is invalid");
    plaintextTotal += length;
    if (plaintextTotal > MAX_PLAINTEXT_BYTES) throw new TypeError("TBS1 plaintext exceeds 8 MiB");
    const bodyStart = position + 4;
    const frameEnd = bodyStart + length + TAG_BYTES;
    if (frameEnd > bytes.byteLength) throw new TypeError("TBS1 object is truncated");
    const nonce = concat(new Uint8Array(4), uint64(index));
    const additionalData = concat(header, uint64(index), bytes.subarray(position, bodyStart));
    try {
      const plaintext = await subtle.decrypt(
        { name: "AES-GCM", iv: nonce, additionalData, tagLength: 128 },
        objectKey,
        bytes.subarray(bodyStart, frameEnd),
      );
      plaintextParts.push(new Uint8Array(plaintext));
    } catch {
      throw new TypeError("TBS1 frame failed authentication");
    }
    position = frameEnd;
    if (last) break;
  }
  if (position !== bytes.byteLength) throw new TypeError("TBS1 object has bytes after its last frame");
  return concat(...plaintextParts);
}

/**
 * @param {string} value
 * @returns {bigint}
 */
function parseEpoch(value) {
  if (!/^(0|[1-9][0-9]*)$/u.test(value)) throw new TypeError("TBS1 epoch must be a canonical decimal");
  const epoch = BigInt(value);
  if (epoch > MAX_UINT64) throw new TypeError("TBS1 epoch exceeds uint64");
  return epoch;
}

/**
 * @param {number} value
 * @returns {Uint8Array}
 */
function uint64(value) {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(value));
  return out;
}

/**
 * @param {...Uint8Array} parts
 * @returns {Uint8Array<ArrayBuffer>}
 */
function concat(...parts) {
  const out = new Uint8Array(parts.reduce((total, part) => total + part.byteLength, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.byteLength;
  }
  return out;
}
