import { createHash } from "node:crypto";
import { describe, expect, test } from "vitest";
import { decryptShapeObject } from "../src/v1/shape-crypto.js";
import { vectors } from "./support/shape-vectors.js";

/** @typedef {Uint8Array<ArrayBuffer>} Bytes */
/**
 * @typedef {object} SealedFrame
 * @property {Bytes} lengthWord
 * @property {Bytes} nonce
 * @property {Bytes} additionalData
 * @property {Bytes} ciphertext
 * @property {Bytes} tag
 * @property {Bytes} wire
 */

const FRAME_BYTES = 1048576;
const LAST_FRAME_FLAG = 0x80000000;
const { subtle } = globalThis.crypto;

/** @param {string} hex @returns {Bytes} */
function fromHex(hex) {
  return Uint8Array.from(hex.match(/../gu) ?? [], byte => Number.parseInt(byte, 16));
}

/** @param {Bytes} bytes */
function toHex(bytes) {
  return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
}

/** @param {Bytes} bytes */
function sha256Hex(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

/** @param {string} value @returns {Bytes} */
function utf8(value) {
  return new TextEncoder().encode(value);
}

/** @param {...Uint8Array} parts @returns {Bytes} */
function concat(...parts) {
  const out = new Uint8Array(parts.reduce((total, part) => total + part.byteLength, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.byteLength;
  }
  return out;
}

/** @param {number} value @returns {Bytes} */
function uint64(value) {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(value));
  return out;
}

/**
 * @param {Bytes} ikm
 * @param {Bytes} salt
 * @param {Bytes} info
 * @returns {Promise<Bytes>}
 */
async function hkdf(ikm, salt, info) {
  const base = await subtle.importKey("raw", ikm.slice(), "HKDF", false, ["deriveBits"]);
  return new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info }, base, 256));
}

/**
 * Reference epoch step from the spec: `K_e = HKDF(K_d, empty salt, "TBS1 epoch" NUL handle NUL E)`.
 * An empty salt is 32 zero bytes.
 * @param {Bytes} projectDataKey
 * @param {string} handle
 * @param {string} epoch
 */
function deriveEpochKey(projectDataKey, handle, epoch) {
  return hkdf(projectDataKey, new Uint8Array(32), utf8(`TBS1 epoch\u0000${handle}\u0000${epoch}`));
}

/**
 * Reference object step: `K_o = HKDF(K_e, salt_obj, "TBS1 object" NUL binding)`.
 * @param {Bytes} epochKey
 * @param {Bytes} salt
 * @param {string} handle
 * @param {string} offset
 */
function deriveObjectKey(epochKey, salt, handle, offset) {
  return hkdf(epochKey, salt, utf8(`TBS1 object\u0000${handle}\u0000${offset}`));
}

/**
 * Splits plaintext into frames: every non-last frame is exactly 1 MiB, and the last frame is shorter.
 * Exact multiples end with an empty last frame.
 * @param {Bytes} plaintext
 * @returns {{ part: Bytes, last: boolean }[]}
 */
function splitFrames(plaintext) {
  const parts = [];
  let offset = 0;
  while (plaintext.byteLength - offset >= FRAME_BYTES) {
    parts.push({ part: plaintext.subarray(offset, offset + FRAME_BYTES), last: false });
    offset += FRAME_BYTES;
  }
  parts.push({ part: plaintext.subarray(offset), last: true });
  return parts;
}

/**
 * Seals one frame and returns its wire bytes plus the parts the vectors check.
 * @param {CryptoKey} objectKey
 * @param {Bytes} header
 * @param {number} index
 * @param {Bytes} part
 * @param {boolean} last
 * @returns {Promise<SealedFrame>}
 */
async function sealFrame(objectKey, header, index, part, last) {
  const lengthWord = new Uint8Array(4);
  new DataView(lengthWord.buffer).setUint32(0, part.byteLength | (last ? LAST_FRAME_FLAG : 0));
  const nonce = concat(new Uint8Array(4), uint64(index));
  const additionalData = concat(header, uint64(index), lengthWord);
  const sealed = new Uint8Array(
    await subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData, tagLength: 128 }, objectKey, part),
  );
  return {
    lengthWord,
    nonce,
    additionalData,
    ciphertext: sealed.slice(0, part.byteLength),
    tag: sealed.slice(part.byteLength),
    wire: concat(lengthWord, sealed),
  };
}

/**
 * Builds a complete TBS1 object from fixed keys and salt, following the spec's sealing steps.
 * @param {object} options
 * @param {Bytes} options.epochKey
 * @param {string} options.handle
 * @param {string} options.offset
 * @param {string} options.epoch
 * @param {string} options.keyId
 * @param {Bytes} options.salt
 * @param {Bytes} options.plaintext
 */
async function sealObject({ epochKey, handle, offset, epoch, keyId, salt, plaintext }) {
  const header = concat(utf8("TBS1"), uint64(Number(epoch)), utf8(keyId), salt);
  const objectKey = await subtle.importKey(
    "raw",
    (await deriveObjectKey(epochKey, salt, handle, offset)).slice(),
    "AES-GCM",
    false,
    ["encrypt"],
  );
  const frames = [];
  for (const [index, { part, last }] of splitFrames(plaintext).entries()) {
    frames.push(await sealFrame(objectKey, header, index, part, last));
  }
  return { header, objectKey, frames, object: concat(header, ...frames.map(frame => frame.wire)) };
}

/**
 * @param {SealedFrame[]} frames
 * @param {number} index
 * @returns {SealedFrame}
 */
function frameAt(frames, index) {
  const frame = frames[index];
  if (!frame) throw new Error(`missing sealed frame ${index}`);
  return frame;
}

/** @param {any} vector */
function requestFor(vector) {
  return {
    handle: vector.handle,
    offset: vector.request_offset,
    epoch: vector.epoch,
    keyId: vector.key_id,
    key: fromHex(vector.epoch_key_hex),
  };
}

describe("TBS1 positive vectors", () => {
  for (const vector of vectors("tbs1_vectors")) {
    test(`${vector.id}: intermediates and exact object bytes`, async () => {
      const projectDataKey = fromHex(vector.project_data_key_hex);
      const epochKey = fromHex(vector.epoch_key_hex);
      const salt = fromHex(vector.object_salt_hex);
      expect(toHex(await deriveEpochKey(projectDataKey, vector.handle, vector.epoch))).toBe(vector.epoch_key_hex);
      expect(toHex(await deriveObjectKey(epochKey, salt, vector.handle, vector.request_offset))).toBe(vector.object_key_hex);
      expect(vector.binding_hex).toBe(toHex(utf8(`${vector.handle}\u0000${vector.request_offset}`)));

      const plaintext = utf8(vector.plaintext_utf8);
      const sealed = await sealObject({
        epochKey,
        handle: vector.handle,
        offset: vector.request_offset,
        epoch: vector.epoch,
        keyId: vector.key_id,
        salt,
        plaintext,
      });
      expect(toHex(sealed.header)).toBe(vector.header_hex);
      expect(toHex(sealed.object)).toBe(vector.tbs1_hex);
      expect(sealed.object.byteLength).toBe(vector.ciphertext_size);
      expect(sha256Hex(sealed.object)).toBe(vector.ciphertext_sha256);
      for (const [index, frame] of vector.frames.entries()) {
        const sealedFrame = frameAt(sealed.frames, index);
        expect(frame.index).toBe(String(index));
        expect(toHex(sealedFrame.lengthWord)).toBe(frame.length_word_hex);
        expect(toHex(sealedFrame.nonce)).toBe(frame.nonce_hex);
        expect(toHex(sealedFrame.additionalData)).toBe(frame.aad_hex);
        expect(toHex(sealedFrame.tag)).toBe(frame.tag_hex);
      }

      const decrypted = await decryptShapeObject(fromHex(vector.tbs1_hex), requestFor(vector));
      expect(new TextDecoder().decode(decrypted)).toBe(vector.plaintext_utf8);
    });
  }
});

describe("TBS1 rejection vectors", () => {
  for (const vector of vectors("tbs1_rejection_vectors")) {
    test(`${vector.id} is rejected without plaintext`, async () => {
      expect(vector.expected_result).toBe("reject_without_applying");
      await expect(decryptShapeObject(fromHex(vector.tbs1_hex), requestFor(vector))).rejects.toThrow();
    });
  }
});

