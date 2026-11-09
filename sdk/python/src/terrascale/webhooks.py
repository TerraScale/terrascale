from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import json
import time
from collections.abc import Callable, Mapping
from typing import Any

from .errors import WebhookVerificationError

__all__ = ["verify_webhook"]

_TOLERANCE_SECONDS = 300


def verify_webhook(
    secret: str,
    headers: Mapping[str, str],
    raw_body: str | bytes,
    *,
    now: Callable[[], int] | None = None,
) -> dict[str, Any]:
    clock = now or (lambda: int(time.time()))
    lookup = {name.lower(): value for name, value in headers.items()}
    missing = [name for name in ("webhook-id", "webhook-timestamp", "webhook-signature") if not lookup.get(name)]
    if missing:
        raise WebhookVerificationError(
            "missing_headers",
            f"The delivery must carry webhook-id, webhook-timestamp and webhook-signature: missing {', '.join(missing)}.",
        )
    delivery_id = lookup["webhook-id"]
    timestamp = lookup["webhook-timestamp"]
    signature = lookup["webhook-signature"]
    if not timestamp.isdigit():
        raise WebhookVerificationError("invalid_timestamp", "webhook-timestamp must be unix seconds.")
    unix_seconds = int(timestamp)
    if abs(clock() - unix_seconds) > _TOLERANCE_SECONDS:
        raise WebhookVerificationError(
            "timestamp_out_of_tolerance",
            f"webhook-timestamp {timestamp} is more than {_TOLERANCE_SECONDS} seconds from now.",
        )
    payload = secret.removeprefix("whsec_")
    if payload == secret or payload == "":
        raise WebhookVerificationError(
            "invalid_secret", "The secret must be the whsec_ value answered once at registration or rotation."
        )
    try:
        key = base64.b64decode(payload, validate=True)
    except (binascii.Error, ValueError):
        raise WebhookVerificationError("invalid_secret", "The secret's whsec_ payload must be base64.") from None
    if not key:
        raise WebhookVerificationError("invalid_secret", "The secret's whsec_ payload must not be empty.")
    body = raw_body.encode("utf-8") if isinstance(raw_body, str) else raw_body
    signed = b".".join((delivery_id.encode("utf-8"), timestamp.encode("ascii"), body))
    digest = hmac.new(key, signed, hashlib.sha256).digest()
    expected = base64.b64encode(digest).decode("ascii")
    for entry in signature.split(" "):
        value = entry.removeprefix("v1,")
        if value == entry:
            continue
        if hmac.compare_digest(expected, value):
            try:
                event = json.loads(body)
            except json.JSONDecodeError:
                raise WebhookVerificationError("invalid_body", "The delivery body is not valid JSON.") from None
            if not isinstance(event, dict):
                raise WebhookVerificationError("invalid_body", "The delivery body must be a JSON event object.")
            return event
    raise WebhookVerificationError("signature_mismatch", "No v1 signature entry matched the delivery.")
