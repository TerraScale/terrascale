"""Canonical ULID entity IDs: 26 uppercase Crockford base32 characters."""

from __future__ import annotations

import os
import re
import threading
import time

__all__ = ["new_id", "is_valid_id", "validate_id"]

_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
_PATTERN = re.compile(r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
_lock = threading.Lock()
_last_ms = -1
_last_rand = 0


def _encode(value: int, length: int) -> str:
    out = []
    for _ in range(length):
        out.append(_ALPHABET[value & 31])
        value >>= 5
    return "".join(reversed(out))


def new_id() -> str:
    """Return a new ULID, monotonic within a millisecond and thread-safe."""
    global _last_ms, _last_rand
    with _lock:
        ms = time.time_ns() // 1_000_000
        if ms <= _last_ms:
            ms = _last_ms
            rand = _last_rand + 1
            if rand >= 1 << 80:
                # The random part overflowed: borrow the next millisecond.
                ms += 1
                rand = int.from_bytes(os.urandom(10), "big")
        else:
            rand = int.from_bytes(os.urandom(10), "big")
        _last_ms, _last_rand = ms, rand
    return _encode(ms, 10) + _encode(rand, 16)


def is_valid_id(value: object) -> bool:
    """Report whether value is a canonical ULID."""
    return isinstance(value, str) and _PATTERN.fullmatch(value) is not None


def validate_id(value: object, what: str = "id") -> str:
    """Return value when it is a canonical ULID, else raise ValueError."""
    if not is_valid_id(value):
        raise ValueError(f"{what} must be a canonical ULID: 26 uppercase Crockford base32 characters")
    return value  # type: ignore[return-value]
