use std::time::{Duration, SystemTime, UNIX_EPOCH};

use terrascale::webhooks::{verify_webhook_at, WebhookHeaders};

const SECRET: &str = "whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
const BODY: &[u8] = b"{\"type\":\"user.signed_up\",\"data\":{\"user_id\":\"01JDUSER\"}}";

fn headers(signature: &str) -> WebhookHeaders {
    WebhookHeaders {
        id: "msg_01JDWEBHOOK0001".to_string(),
        timestamp: "1765363200".to_string(),
        signature: signature.to_string(),
    }
}

fn clock() -> SystemTime {
    UNIX_EPOCH + Duration::from_secs(1765363200)
}

#[test]
fn verify_webhook_accepts_signed_delivery() {
    let event = verify_webhook_at(
        SECRET,
        &headers("v1,myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg="),
        BODY,
        clock(),
    )
    .expect("signed delivery rejected");
    assert_eq!(event["type"], "user.signed_up");
    assert_eq!(event["data"]["user_id"], "01JDUSER");
}

#[test]
fn verify_webhook_rejects_tampered_body() {
    let tampered = b"{\"type\":\"user.signed_up\",\"data\":{\"user_id\":\"01JDATTACKER\"}}";
    let error = verify_webhook_at(
        SECRET,
        &headers("v1,myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg="),
        tampered,
        clock(),
    )
    .expect_err("tampered body accepted");
    assert_eq!(error.code, "signature_mismatch");
}
