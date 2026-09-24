use auth_contracts::models::SigningKeyRecord;
use auth_security::Es256Key;
use auth_security::tokens::random_token;
use auth_store::{AuthStore, StoreError, StoreResult};
use base64::{Engine, engine::general_purpose::STANDARD};
use sha2::{Digest, Sha256};
use std::{collections::HashMap, sync::Arc};
use tokio::sync::Mutex;

/// Durable per-instance ES256 keys; every lookup rereads TerraBase so replicas see rotation.
pub struct KeyRing {
    store: Arc<AuthStore>,
    creation_gates: Mutex<HashMap<String, Arc<Mutex<()>>>>,
}

impl KeyRing {
    pub fn new(store: Arc<AuthStore>) -> Self {
        Self {
            store,
            creation_gates: Mutex::new(HashMap::new()),
        }
    }

    async fn gate_for(&self, instance_id: &str) -> Arc<Mutex<()>> {
        let mut gates = self.creation_gates.lock().await;
        gates
            .entry(instance_id.to_owned())
            .or_insert_with(|| Arc::new(Mutex::new(())))
            .clone()
    }

    pub async fn get_current(&self, instance_id: &str) -> StoreResult<Es256Key> {
        self.get_active(instance_id)
            .await?
            .into_iter()
            .next()
            .ok_or_else(|| {
                StoreError::InvalidConfiguration(
                    "No active signing key remains for instance".into(),
                )
            })
    }

    /// Adds a new current signing key while preserving older active keys for
    /// verification. Retirement is a separate operation so existing JWTs live
    /// until their caller-defined expiry.
    pub async fn rotate(&self, instance_id: &str) -> StoreResult<Es256Key> {
        let gate = self.gate_for(instance_id).await;
        let _guard = gate.lock().await;
        let active = self.active_records(instance_id).await?;
        let created_at = now_unix_seconds().max(
            active
                .first()
                .map_or(0, |key| key.created_at.saturating_add(1)),
        );
        let id = format!("skey_{}", random_token(40));
        let generated = Es256Key::generate(id.clone());
        let record = SigningKeyRecord {
            id,
            instance_id: instance_id.to_owned(),
            algorithm: "ES256".into(),
            secret_base64: STANDARD.encode(generated.export_secret()),
            active: true,
            created_at,
            retired_at: None,
        };
        self.store.create_signing_key(&record).await?;
        Ok(generated)
    }

    pub async fn get_active(&self, instance_id: &str) -> StoreResult<Vec<Es256Key>> {
        let active = self.active_records(instance_id).await?;
        if !active.is_empty() {
            return active.iter().map(restore).collect();
        }
        let gate = self.gate_for(instance_id).await;
        let _guard = gate.lock().await;
        let active = self.active_records(instance_id).await?;
        if !active.is_empty() {
            return active.iter().map(restore).collect();
        }

        let id = format!(
            "skey_{:x}",
            Sha256::digest(format!("initial:{instance_id}").as_bytes())
        );
        let generated = Es256Key::generate(id.clone());
        let record = SigningKeyRecord {
            id: id.clone(),
            instance_id: instance_id.to_owned(),
            algorithm: "ES256".into(),
            secret_base64: STANDARD.encode(generated.export_secret()),
            active: true,
            created_at: now_unix_seconds(),
            retired_at: None,
        };
        match self.store.create_signing_key(&record).await {
            Ok(_) => Ok(vec![generated]),
            Err(error) if error.is_conflict() => {
                let winner = self
                    .store
                    .get_signing_key(&id)
                    .await?
                    .filter(|item| item.active && item.instance_id == instance_id)
                    .ok_or_else(|| {
                        StoreError::InvalidConfiguration(
                            "No active signing key remains for instance".into(),
                        )
                    })?;
                Ok(vec![restore(&winner)?])
            }
            Err(error) => Err(error),
        }
    }

    async fn active_records(&self, instance_id: &str) -> StoreResult<Vec<SigningKeyRecord>> {
        let mut records = self.store.list_signing_keys(instance_id).await?;
        // Fail closed if the query transport returns a record for another instance.
        if records.iter().any(|item| item.instance_id != instance_id) {
            return Err(StoreError::InvalidConfiguration(
                "Signing key query returned another instance".into(),
            ));
        }
        records.retain(|item| item.active);
        records.sort_by(|a, b| {
            b.created_at
                .cmp(&a.created_at)
                .then_with(|| b.id.cmp(&a.id))
        });
        Ok(records)
    }
}

fn restore(record: &SigningKeyRecord) -> StoreResult<Es256Key> {
    if record.algorithm != "ES256" || record.id.is_empty() {
        return Err(StoreError::InvalidConfiguration(
            "Unsupported persisted signing key".into(),
        ));
    }
    let secret = STANDARD.decode(&record.secret_base64).map_err(|error| {
        StoreError::InvalidConfiguration(format!("Invalid persisted signing key: {error}"))
    })?;
    Es256Key::from_secret(&record.id, &secret).map_err(|error| {
        StoreError::InvalidConfiguration(format!("Invalid persisted signing key: {error}"))
    })
}

fn now_unix_seconds() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn restored_keys_reject_unsupported_algorithm_and_corrupt_material() {
        let key = Es256Key::generate("skey_test");
        let mut record = SigningKeyRecord {
            id: "skey_test".into(),
            instance_id: "ins_test".into(),
            algorithm: "ES256".into(),
            secret_base64: STANDARD.encode(key.export_secret()),
            active: true,
            created_at: 1,
            retired_at: None,
        };
        assert!(restore(&record).is_ok());
        record.algorithm = "HS256".into();
        assert!(restore(&record).is_err());
        record.algorithm = "ES256".into();
        record.secret_base64 = STANDARD.encode([0_u8; 96]);
        assert!(restore(&record).is_err());
    }
}
