use auth_contracts::models::SigningKeyRecord;
use auth_security::{Es256Key, secretbox, tokens::random_token};
use auth_store::{AuthStore, StoreError, StoreResult};
use sha2::{Digest, Sha256};
use std::{collections::HashMap, sync::Arc};
use tokio::sync::Mutex;

/// Durable per-instance ES256 keys; every lookup rereads TerraBase so replicas see rotation.
pub struct KeyRing {
    store: Arc<AuthStore>,
    master_key: Vec<u8>,
    creation_gates: Mutex<HashMap<String, Arc<Mutex<()>>>>,
}

impl KeyRing {
    pub fn new(store: Arc<AuthStore>, master_key: Vec<u8>) -> Self {
        Self {
            store,
            master_key,
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
        let record = signing_key_record(id, instance_id, &generated, created_at, &self.master_key)?;
        self.store.create_signing_key(&record).await?;
        Ok(generated)
    }

    pub async fn get_active(&self, instance_id: &str) -> StoreResult<Vec<Es256Key>> {
        let active = self.active_records(instance_id).await?;
        if !active.is_empty() {
            return active
                .iter()
                .map(|record| restore(record, &self.master_key))
                .collect();
        }
        let gate = self.gate_for(instance_id).await;
        let _guard = gate.lock().await;
        let active = self.active_records(instance_id).await?;
        if !active.is_empty() {
            return active
                .iter()
                .map(|record| restore(record, &self.master_key))
                .collect();
        }

        let id = format!(
            "skey_{:x}",
            Sha256::digest(format!("initial:{instance_id}").as_bytes())
        );
        let generated = Es256Key::generate(id.clone());
        let record = signing_key_record(
            id.clone(),
            instance_id,
            &generated,
            now_unix_seconds(),
            &self.master_key,
        )?;
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
                Ok(vec![restore(&winner, &self.master_key)?])
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

fn signing_key_record(
    id: String,
    instance_id: &str,
    key: &Es256Key,
    created_at: i64,
    master_key: &[u8],
) -> StoreResult<SigningKeyRecord> {
    let secret_box = secretbox::encrypt(master_key, &key.export_secret()).map_err(|error| {
        StoreError::InvalidConfiguration(format!("Could not encrypt signing key: {error}"))
    })?;
    Ok(SigningKeyRecord {
        id,
        instance_id: instance_id.to_owned(),
        algorithm: "ES256".into(),
        secret_box,
        active: true,
        created_at,
        retired_at: None,
    })
}

fn restore(record: &SigningKeyRecord, master_key: &[u8]) -> StoreResult<Es256Key> {
    if record.algorithm != "ES256" || record.id.is_empty() {
        return Err(StoreError::InvalidConfiguration(
            "Unsupported persisted signing key".into(),
        ));
    }
    let secret = secretbox::decrypt(master_key, &record.secret_box).map_err(|error| {
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
    use base64::{Engine, engine::general_purpose::STANDARD};

    #[test]
    fn signing_key_records_encrypt_private_material_and_restore_with_master_key() {
        let master_key = [7_u8; 32];
        let key = Es256Key::generate("skey_test");
        let raw_base64 = STANDARD.encode(key.export_secret());
        let mut record =
            signing_key_record("skey_test".into(), "ins_test", &key, 1, &master_key).unwrap();

        assert!(record.secret_box.starts_with("v1:"));
        assert!(!record.secret_box.contains(raw_base64.as_str()));
        assert!(
            !serde_json::to_string(&record)
                .unwrap()
                .contains(raw_base64.as_str())
        );
        let restored = restore(&record, &master_key).unwrap();
        assert_eq!(restored.public_jwk(), key.public_jwk());
        assert!(restore(&record, &[8_u8; 32]).is_err());

        record.algorithm = "HS256".into();
        assert!(restore(&record, &master_key).is_err());
        record.algorithm = "ES256".into();
        record.secret_box = secretbox::encrypt(&master_key, &[0_u8; 96]).unwrap();
        assert!(restore(&record, &master_key).is_err());
        record.secret_box = "v1:AA==".into();
        assert!(restore(&record, &master_key).is_err());
    }
}
