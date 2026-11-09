use std::time::{Duration, SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::STANDARD, Engine as _};
use ring::hmac;
use serde_json::Value;

use crate::error::{Error, ErrorKind};

pub struct WebhookHeaders {
    pub id: String,
    pub timestamp: String,
    pub signature: String,
}

const TOLERANCE: Duration = Duration::from_secs(300);

pub fn verify_webhook(
    secret: &str,
    headers: &WebhookHeaders,
    raw_body: &[u8],
) -> Result<Value, Error> {
    verify_webhook_at(secret, headers, raw_body, SystemTime::now())
}

pub fn verify_webhook_at(
    secret: &str,
    headers: &WebhookHeaders,
    raw_body: &[u8],
    now: SystemTime,
) -> Result<Value, Error> {
    if headers.id.is_empty() || headers.timestamp.is_empty() || headers.signature.is_empty() {
        return Err(webhook_error(
            "missing_headers",
            "The delivery must carry webhook-id, webhook-timestamp and webhook-signature.",
        ));
    }
    let timestamp: u64 = headers.timestamp.parse().map_err(|_| {
        webhook_error(
            "invalid_timestamp",
            "webhook-timestamp must be unix seconds.",
        )
    })?;
    let now_seconds = now
        .duration_since(UNIX_EPOCH)
        .map_err(|_| {
            webhook_error(
                "invalid_timestamp",
                "The verification clock is before the unix epoch.",
            )
        })?
        .as_secs();
    if now_seconds.abs_diff(timestamp) > TOLERANCE.as_secs() {
        return Err(webhook_error(
            "timestamp_out_of_tolerance",
            "webhook-timestamp is more than 300 seconds from now.",
        ));
    }
    let payload = secret.strip_prefix("whsec_").ok_or_else(|| {
        invalid_secret(
            "The secret must be the whsec_ value answered once at registration or rotation.",
        )
    })?;
    if payload.is_empty() {
        return Err(invalid_secret(
            "The secret's whsec_ payload must not be empty.",
        ));
    }
    let key_bytes = STANDARD
        .decode(payload)
        .map_err(|_| invalid_secret("The secret's whsec_ payload must be base64."))?;
    let key = hmac::Key::new(hmac::HMAC_SHA256, &key_bytes);
    let mut signed =
        Vec::with_capacity(headers.id.len() + headers.timestamp.len() + raw_body.len() + 2);
    signed.extend_from_slice(headers.id.as_bytes());
    signed.push(b'.');
    signed.extend_from_slice(headers.timestamp.as_bytes());
    signed.push(b'.');
    signed.extend_from_slice(raw_body);
    for entry in headers.signature.split(' ') {
        let Some(value) = entry.strip_prefix("v1,") else {
            continue;
        };
        let Ok(candidate) = STANDARD.decode(value) else {
            continue;
        };
        if hmac::verify(&key, &signed, &candidate).is_ok() {
            return serde_json::from_slice::<Value>(raw_body).map_err(|_| {
                webhook_error("invalid_body", "The delivery body is not valid JSON.")
            });
        }
    }
    Err(webhook_error(
        "signature_mismatch",
        "No v1 signature entry matched the delivery.",
    ))
}

fn invalid_secret(message: &str) -> Error {
    webhook_error("invalid_secret", message)
}

fn webhook_error(code: &str, message: &str) -> Error {
    Error::new(ErrorKind::Validation, code, message)
}
