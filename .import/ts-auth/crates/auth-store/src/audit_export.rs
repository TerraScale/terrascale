//! Offline verification of audit events and the `snake_case` JSON export format.

use auth_contracts::models::AuditEvent;
use serde::Deserialize;
use serde_json::{Map, Value};
use sha2::{Digest, Sha256};

use crate::{audit_hash, audit_signature, constant_time_equal};

/// Integrity and HMAC authenticity of an exported collection, in input order.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AuditVerificationResult {
    pub event_count: usize,
    pub valid_integrity_count: usize,
    pub valid_authenticity_count: usize,
    pub invalid_integrity_ids: Vec<String>,
    pub invalid_authenticity_ids: Vec<String>,
}

impl AuditVerificationResult {
    #[must_use]
    pub fn is_valid(&self) -> bool {
        self.invalid_integrity_ids.is_empty()
    }

    #[must_use]
    pub fn is_authentic(&self) -> bool {
        self.is_valid() && self.invalid_authenticity_ids.is_empty()
    }
}

/// Verify a JSON array returned by the audit export API without a store connection.
/// Malformed JSON or field types are errors; missing event fields fail integrity checks.
pub fn verify_audit_export_json(
    json: &[u8],
    verification_keys: &[Vec<u8>],
) -> Result<AuditVerificationResult, serde_json::Error> {
    let events: Vec<ExportedAuditEvent> = serde_json::from_slice(json)?;
    Ok(verify_audit_events(
        events.into_iter().map(Into::into),
        verification_keys,
    ))
}

/// Verify typed events against the configured current and retained HMAC keys.
#[must_use]
pub fn verify_audit_events(
    events: impl IntoIterator<Item = AuditEvent>,
    verification_keys: &[Vec<u8>],
) -> AuditVerificationResult {
    let mut result = AuditVerificationResult {
        event_count: 0,
        valid_integrity_count: 0,
        valid_authenticity_count: 0,
        invalid_integrity_ids: Vec::new(),
        invalid_authenticity_ids: Vec::new(),
    };
    for event in events {
        result.event_count += 1;
        let valid_hash = match (
            event.integrity_version,
            event.integrity_hash.as_deref(),
            audit_hash(&event),
        ) {
            (Some(2), Some(hash), Ok(expected)) if constant_time_equal(hash, &expected) => {
                Some(hash)
            }
            _ => None,
        };
        let Some(hash) = valid_hash else {
            result.invalid_integrity_ids.push(event.id.clone());
            result.invalid_authenticity_ids.push(event.id);
            continue;
        };
        result.valid_integrity_count += 1;
        let authentic = event
            .integrity_signature
            .as_deref()
            .is_some_and(|signature| {
                verification_keys.iter().any(|key| {
                    let fingerprint_matches = event
                        .integrity_key_fingerprint
                        .as_deref()
                        .is_none_or(|fingerprint| {
                            constant_time_equal(fingerprint, &format!("{:x}", Sha256::digest(key)))
                        });
                    fingerprint_matches
                        && constant_time_equal(signature, &audit_signature(key, hash))
                })
            });
        if authentic {
            result.valid_authenticity_count += 1;
        } else {
            result.invalid_authenticity_ids.push(event.id);
        }
    }
    result
}

#[derive(Deserialize)]
struct ExportedAuditEvent {
    #[serde(default)]
    id: String,
    #[serde(default)]
    instance_id: String,
    #[serde(default)]
    r#type: String,
    #[serde(default)]
    actor_id: Option<String>,
    #[serde(default)]
    data: Map<String, Value>,
    #[serde(default)]
    created_at: i64,
    #[serde(default)]
    integrity_version: Option<i32>,
    #[serde(default)]
    integrity_hash: Option<String>,
    #[serde(default)]
    integrity_signature: Option<String>,
    #[serde(default)]
    integrity_key_fingerprint: Option<String>,
}

impl From<ExportedAuditEvent> for AuditEvent {
    fn from(event: ExportedAuditEvent) -> Self {
        Self {
            id: event.id,
            instance_id: event.instance_id,
            r#type: event.r#type,
            actor_id: event.actor_id,
            data: event.data,
            created_at: event.created_at,
            integrity_version: event.integrity_version,
            integrity_hash: event.integrity_hash,
            integrity_signature: event.integrity_signature,
            integrity_key_fingerprint: event.integrity_key_fingerprint,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn exported_event(id: &str, key: &[u8]) -> Value {
        let mut event = AuditEvent {
            id: id.into(),
            instance_id: "ins_a".into(),
            r#type: "user.updated".into(),
            actor_id: Some("usr_operator".into()),
            data: serde_json::from_value(json!({"field":"name"})).unwrap(),
            created_at: 1_700_000_000,
            integrity_version: Some(2),
            integrity_hash: None,
            integrity_signature: None,
            integrity_key_fingerprint: None,
        };
        let hash = audit_hash(&event).unwrap();
        event.integrity_signature = Some(audit_signature(key, &hash));
        event.integrity_key_fingerprint = Some(format!("{:x}", Sha256::digest(key)));
        event.integrity_hash = Some(hash);
        json!({
            "id":event.id,
            "instance_id":event.instance_id,
            "type":event.r#type,
            "actor_id":event.actor_id,
            "data":event.data,
            "created_at":event.created_at,
            "integrity_version":event.integrity_version,
            "integrity_hash":event.integrity_hash,
            "integrity_signature":event.integrity_signature,
            "integrity_key_fingerprint":event.integrity_key_fingerprint,
        })
    }

    #[test]
    fn verifies_export_across_key_rotation_and_rejects_wrong_key() {
        let old = b"old-key".to_vec();
        let current = b"current-key".to_vec();
        let json = serde_json::to_vec(&json!([
            exported_event("old", &old),
            exported_event("current", &current),
        ]))
        .unwrap();
        let result = verify_audit_export_json(&json, &[current.clone(), old]).unwrap();
        assert_eq!(result.event_count, 2);
        assert_eq!(result.valid_integrity_count, 2);
        assert_eq!(result.valid_authenticity_count, 2);
        assert!(result.is_authentic());

        let wrong = verify_audit_export_json(&json, &[b"wrong-key".to_vec()]).unwrap();
        assert!(wrong.is_valid());
        assert!(!wrong.is_authentic());
        assert_eq!(wrong.invalid_authenticity_ids, ["old", "current"]);
    }

    #[test]
    fn separates_hash_tampering_from_fingerprint_tampering() {
        let key = b"signing-key".to_vec();
        let mut events = json!([
            exported_event("hash", &key),
            exported_event("fingerprint", &key)
        ]);
        events[0]["type"] = json!("user.deleted");
        events[1]["integrity_key_fingerprint"] = json!("0".repeat(64));
        let result =
            verify_audit_export_json(&serde_json::to_vec(&events).unwrap(), &[key]).unwrap();
        assert_eq!(result.valid_integrity_count, 1);
        assert_eq!(result.valid_authenticity_count, 0);
        assert_eq!(result.invalid_integrity_ids, ["hash"]);
        assert_eq!(result.invalid_authenticity_ids, ["hash", "fingerprint"]);
    }

    #[test]
    fn rejects_malformed_export_and_marks_missing_fields_invalid() {
        let key = b"signing-key".to_vec();
        assert!(verify_audit_export_json(br#"{"data":[]}"#, std::slice::from_ref(&key)).is_err());
        assert!(verify_audit_export_json(br#"[{"id":123}]"#, std::slice::from_ref(&key)).is_err());
        let result = verify_audit_export_json(br#"[{"id":"missing"}]"#, &[key]).unwrap();
        assert_eq!(result.invalid_integrity_ids, ["missing"]);
        assert_eq!(result.invalid_authenticity_ids, ["missing"]);
    }
}
