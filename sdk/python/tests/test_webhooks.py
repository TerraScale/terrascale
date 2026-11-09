from __future__ import annotations

import pytest

from terrascale import WebhookVerificationError, verify_webhook

SECRET = "whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
BODY = '{"type":"user.signed_up","data":{"user_id":"01JDUSER"}}'
HEADERS = {
    "webhook-id": "msg_01JDWEBHOOK0001",
    "webhook-timestamp": "1765363200",
    "webhook-signature": "v1,myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg=",
}


def test_verify_webhook_accepts_signed_delivery() -> None:
    event = verify_webhook(SECRET, HEADERS, BODY, now=lambda: 1765363200)
    assert event == {"type": "user.signed_up", "data": {"user_id": "01JDUSER"}}


def test_verify_webhook_rejects_tampered_body() -> None:
    with pytest.raises(WebhookVerificationError) as caught:
        verify_webhook(SECRET, HEADERS, BODY.replace("01JDUSER", "01JDATTACKER"), now=lambda: 1765363200)
    assert caught.value.code == "signature_mismatch"
