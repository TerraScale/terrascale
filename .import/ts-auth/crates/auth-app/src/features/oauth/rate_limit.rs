use super::*;
use crate::app::storage_error;

/// Registered clients/users reuse fixed keys. Arbitrary mailbox targets use
/// 65,536 keyed buckets per tenant to cap untrusted cardinality; a collision
/// conservatively shares the budget. CAS applies across service replicas.
pub(super) async fn allow_attempt(
    state: &AppState,
    instance_id: &str,
    subject: &str,
    purpose: &str,
    maximum: u32,
) -> AppResult<bool> {
    #[derive(Serialize, Deserialize)]
    struct Bucket {
        window: i64,
        used: u32,
    }
    let subject = if purpose == "guest-mailbox" {
        let digest = tokens::hmac_sha256(
            &state.options.master_key,
            format!("{instance_id}:{subject}").as_bytes(),
        );
        format!(
            "mailbox-bucket-{}",
            u16::from_be_bytes([digest[0], digest[1]])
        )
    } else {
        subject.to_owned()
    };
    let id = tokens::sha256_hex(&format!("{instance_id}:{purpose}:{subject}"));
    for _ in 0..8 {
        let window = now() / 60;
        let current = state
            .store
            .get_versioned::<Bucket>("oauth_flow_limits", &id)
            .await
            .map_err(|e| storage_error(&e))?;
        if let Some((mut bucket, version)) = current {
            // A replica with a slightly older clock must not reset a newer
            // replica's window and replenish the shared budget.
            if bucket.window < window {
                bucket = Bucket { window, used: 0 };
            }
            if bucket.used >= maximum {
                return Ok(false);
            }
            bucket.used += 1;
            if state
                .store
                .compare_and_put("oauth_flow_limits", &id, &bucket, &version)
                .await
                .map_err(|e| storage_error(&e))?
            {
                return Ok(true);
            }
        } else {
            match state
                .store
                .create("oauth_flow_limits", &id, &Bucket { window, used: 1 })
                .await
            {
                Ok(()) => return Ok(true),
                Err(e) if e.is_conflict() || e.is_precondition() => {}
                Err(e) => return Err(storage_error(&e)),
            }
        }
    }
    Err(AppError::unavailable())
}
