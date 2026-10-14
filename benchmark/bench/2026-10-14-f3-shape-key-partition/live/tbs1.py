"""Strict TBS1 client decoder (docs/terrabase/shape-reads.md section 12), written from the specification.

open_object(data, handle, offset, epoch, key_id, epoch_key) returns the plaintext, or None for any rejection.
Self-check: python tbs1.py <shape-vectors.json> opens every tbs1_vectors entry and refuses each with a wrong epoch key.
"""
import hashlib, hmac, struct, sys

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

FRAME, MAX_PLAIN, HEADER, TAG, LAST = 1 << 20, 8 << 20, 70, 16, 1 << 31


def hkdf(ikm, salt, info):
    prk = hmac.new(salt or bytes(32), ikm, hashlib.sha256).digest()
    return hmac.new(prk, info + b"\x01", hashlib.sha256).digest()


def epoch_key(data_key, handle, epoch):
    return hkdf(data_key, b"", b"TBS1 epoch\x00" + handle.encode() + b"\x00" + str(epoch).encode())


def open_object(data, handle, offset, epoch, key_id, k_e):
    if len(data) < HEADER or data[:4] != b"TBS1" or struct.unpack(">Q", data[4:12])[0] != epoch or data[12:38] != key_id.encode():
        return None
    header, salt = data[:HEADER], data[38:HEADER]
    k_o = hkdf(k_e, salt, b"TBS1 object\x00" + handle.encode() + b"\x00" + offset.encode())
    aead, out, pos, i = AESGCM(k_o), bytearray(), HEADER, 0
    while True:
        if pos + 4 > len(data):
            return None
        word = data[pos:pos + 4]
        n, last = struct.unpack(">I", word)[0] & (LAST - 1), struct.unpack(">I", word)[0] & LAST
        if n > FRAME or (not last and n != FRAME) or (last and n == FRAME) or len(out) + n > MAX_PLAIN or pos + 4 + n + TAG > len(data):
            return None
        index = struct.pack(">Q", i)
        try:
            out += aead.decrypt(bytes(4) + index, data[pos + 4:pos + 4 + n + TAG], header + index + word)
        except InvalidTag:
            return None
        pos, i = pos + 4 + n + TAG, i + 1
        if last:
            return bytes(out) if pos == len(data) else None


if __name__ == "__main__":
    import json
    vectors = json.load(open(sys.argv[1]))["tbs1_vectors"]
    for v in vectors:
        data, k_d = bytes.fromhex(v["tbs1_hex"]), bytes.fromhex(v["project_data_key_hex"])
        k_e = epoch_key(k_d, v["handle"], int(v["epoch"]))
        assert k_e.hex() == v["epoch_key_hex"], v["id"]
        plain = open_object(data, v["handle"], v["request_offset"], int(v["epoch"]), v["key_id"], k_e)
        assert plain is not None and plain.decode() == v["plaintext_utf8"], v["id"]
        assert open_object(data, v["handle"], v["request_offset"], int(v["epoch"]), v["key_id"], epoch_key(k_d, v["handle"], int(v["epoch"]) + 1)) is None
        assert open_object(data, v["handle"], v["request_offset"] + "0", int(v["epoch"]), v["key_id"], k_e) is None
        print("ok", v["id"])
