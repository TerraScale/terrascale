//! Durable, scope-bound encrypted signing keys. Plaintext never enters documents.
use super::*;
use base64::Engine as _;
use terrabase::encryption::{
    ArtifactCipher, Compression, DatabaseKeyLease, DatabaseKeyVersion, EncryptionContext,
};

pub(super) const COLLECTION: &str = "__terrascale_webhook_secrets";

/// Refreshable production authorization for a database key version.
pub trait WebhookKeyLeases: Send + Sync + 'static {
    /// Resolves a current or historical version only in an admitted region.
    ///
    /// # Errors
    /// Returns an unavailable authorization or unavailable key version.
    fn lease(
        &self,
        scope: &TenantScope,
        region: &str,
        version: Option<DatabaseKeyVersion>,
    ) -> Result<DatabaseKeyLease, SecretError>;
}

/// Signing secrets encrypted under provider-authorized database keys.
/// The supplied lease provider enforces current regional placement on every access.
pub struct EngineWebhookSecrets {
    storage: EngineWebhookStorage,
    keys: Arc<dyn WebhookKeyLeases>,
    writes: Mutex<()>,
}

impl EngineWebhookSecrets {
    /// Binds encrypted secrets to the same durable database as webhook metadata.
    #[must_use]
    pub fn from_host(
        host: Arc<terrabase_tbep::member_host::MemberHost>,
        keys: Arc<dyn WebhookKeyLeases>,
    ) -> Self {
        Self { storage: EngineWebhookStorage::from_host(host), keys, writes: Mutex::new(()) }
    }

    fn cipher(
        &self,
        scope: &TenantScope,
        id: &str,
        lease: &DatabaseKeyLease,
    ) -> Result<ArtifactCipher, SecretError> {
        webhook_engine_scope(&self.storage.engine, scope).map_err(|_| SecretError::Unavailable)?;
        let metadata = lease.metadata();
        let context = EncryptionContext::new_with_project(
            metadata.scope().environment(),
            &scope.tenant,
            &scope.project,
            &scope.database,
            "webhook-secret",
            id,
            metadata.version().to_text(),
            1,
            Compression::None,
        )
        .map_err(|_| SecretError::Unavailable)?;
        ArtifactCipher::from_database_key_lease(lease, context, 0)
            .map_err(|_| SecretError::Unavailable)
    }
}

impl WebhookSecrets for EngineWebhookSecrets {
    fn provision(&self, scope: &TenantScope, id: &str, region: &str) -> Result<(), SecretError> {
        let _lock = self.writes.lock().map_err(|_| SecretError::Unavailable)?;
        let lease = self.keys.lease(scope, region, None)?;
        let artifact = terrabase::identity::process_generator().generate().to_text();
        let mut cipher = self.cipher(scope, &format!("{id}-{artifact}"), &lease)?;
        if self
            .storage
            .read_document(scope, COLLECTION, id)
            .map_err(|_| SecretError::Unavailable)?
            .is_some()
        {
            return Err(SecretError::Unavailable);
        }
        let mut secret = [0_u8; 32];
        aws_lc_rs::rand::fill(&mut secret).map_err(|_| SecretError::Unavailable)?;
        let encrypted = cipher.seal(&secret);
        secret.fill(0);
        let encrypted = encrypted.map_err(|_| SecretError::Unavailable)?;
        let value = MetadataValue::Object(BTreeMap::from([
            ("artifact".into(), MetadataValue::String(artifact)),
            ("version".into(), MetadataValue::String(lease.metadata().version().to_text())),
            (
                "ciphertext".into(),
                MetadataValue::String(base64::engine::general_purpose::STANDARD.encode(encrypted)),
            ),
        ]));
        self.storage
            .write_document(scope, COLLECTION, id, value, ExpectedVersion::New)
            .map_err(|_| SecretError::Unavailable)
    }

    fn destroy(&self, scope: &TenantScope, id: &str, region: &str) -> Result<(), SecretError> {
        let _lock = self.writes.lock().map_err(|_| SecretError::Unavailable)?;
        let lease = self.keys.lease(scope, region, None)?;
        let _cipher = self.cipher(scope, id, &lease)?;
        let document = self
            .storage
            .read_document(scope, COLLECTION, id)
            .map_err(|_| SecretError::Unavailable)?
            .ok_or(SecretError::NotFound)?;
        // Retain a tombstone so endpoint identities can never reuse an AEAD nonce.
        self.storage
            .write_document(
                scope,
                COLLECTION,
                id,
                MetadataValue::Null,
                ExpectedVersion::Exact(document.metadata().version()),
            )
            .map_err(|_| SecretError::Unavailable)
    }

    fn get(&self, scope: &TenantScope, id: &str, region: &str) -> Result<Vec<u8>, SecretError> {
        let document = self
            .storage
            .read_document(scope, COLLECTION, id)
            .map_err(|_| SecretError::Unavailable)?
            .ok_or(SecretError::NotFound)?;
        let MetadataValue::Object(fields) = document.value() else {
            return Err(SecretError::NotFound);
        };
        let Some(MetadataValue::String(version)) = fields.get("version") else {
            return Err(SecretError::Unavailable);
        };
        let Some(MetadataValue::String(ciphertext)) = fields.get("ciphertext") else {
            return Err(SecretError::Unavailable);
        };
        let version =
            DatabaseKeyVersion::from_text(version).map_err(|_| SecretError::Unavailable)?;
        let lease = self.keys.lease(scope, region, Some(version))?;
        let Some(MetadataValue::String(artifact)) = fields.get("artifact") else {
            return Err(SecretError::Unavailable);
        };
        let ciphertext = base64::engine::general_purpose::STANDARD
            .decode(ciphertext)
            .map_err(|_| SecretError::Unavailable)?;
        self.cipher(scope, &format!("{id}-{artifact}"), &lease)?
            .open(&ciphertext)
            .map_err(|_| SecretError::Unavailable)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::os::unix::fs::{MetadataExt, PermissionsExt};
    use std::os::unix::net::UnixListener;
    use terrabase::encryption::{KeyPurpose, KeyScope, KeyVersionMetadata, OciVaultKeyDelivery};
    use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};

    struct SocketKeys {
        provider: OciVaultKeyDelivery,
        metadata: KeyVersionMetadata,
    }
    impl WebhookKeyLeases for SocketKeys {
        fn lease(
            &self,
            scope: &TenantScope,
            region: &str,
            version: Option<DatabaseKeyVersion>,
        ) -> Result<DatabaseKeyLease, SecretError> {
            if region != "GRU"
                || scope.tenant != self.metadata.scope().tenant().to_text()
                || scope.project != self.metadata.scope().project().expect("project").to_text()
                || scope.database != self.metadata.scope().database().to_text()
                || version.is_some_and(|version| version != self.metadata.version())
            {
                return Err(SecretError::Unavailable);
            }
            self.provider
                .lease(&self.metadata, terrabase::regions::RegionCode::GRU)
                .map_err(|_| SecretError::Unavailable)
        }
    }

    #[test]
    fn encrypted_secrets_survive_restart_and_deny_placement_then_delete() {
        let directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .expect("crates")
            .parent()
            .expect("workspace")
            .join("target")
            .join(format!("wk-{}", terrabase::identity::process_generator().generate().to_text()));
        std::fs::create_dir_all(&directory).expect("directory");
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700))
            .expect("permissions");
        let socket = directory.join("keys.sock");
        let listener = UnixListener::bind(&socket).expect("socket");
        std::fs::set_permissions(&socket, std::fs::Permissions::from_mode(0o600))
            .expect("socket permissions");
        let provider = OciVaultKeyDelivery::new(
            socket.clone(),
            std::fs::metadata(&socket).expect("metadata").uid(),
        )
        .expect("provider");
        let engine_root = directory.join("database");
        let open = || {
            Arc::new(
                Engine::open(
                    terrabase::EngineConfig::durable(engine_root.clone()).expect("config"),
                )
                .expect("engine"),
            )
        };
        let engine = open();
        let owner = engine.database_owner();
        let scope = TenantScope::new(
            owner.tenant().to_text(),
            owner.project().to_text(),
            owner.database().to_text(),
        )
        .expect("scope");
        let metadata = KeyVersionMetadata::new(
            KeyScope::new_with_project(
                "production",
                TenantUlid::from_text(&scope.tenant).expect("tenant"),
                ProjectUlid::from_text(&scope.project).expect("project"),
                DatabaseUlid::from_text(&scope.database).expect("database"),
                KeyPurpose::Data,
            )
            .expect("key scope"),
            DatabaseKeyVersion::generate(),
        );
        let version = metadata.version().to_text();
        let server = std::thread::spawn(move || {
            for _ in 0..4 {
                let (mut connection, _) = listener.accept().expect("connection");
                connection
                    .set_read_timeout(Some(std::time::Duration::from_secs(5)))
                    .expect("timeout");
                let mut length = [0_u8; 4];
                connection.read_exact(&mut length).expect("length");
                let length = u32::from_be_bytes(length) as usize;
                assert!(length < 2048);
                let mut request = vec![0; length];
                connection.read_exact(&mut request).expect("request");
                assert_eq!(&request[..5], b"TBKL1");
                let mut response = 68_u32.to_be_bytes().to_vec();
                response.extend_from_slice(b"TBKL1");
                response.push(0);
                response.extend_from_slice(&60_u32.to_be_bytes());
                response.extend_from_slice(version.as_bytes());
                response.extend_from_slice(&[17; 32]);
                connection.write_all(&response).expect("response");
            }
        });
        let keys: Arc<dyn WebhookKeyLeases> = Arc::new(SocketKeys { provider, metadata });
        let secrets = EngineWebhookSecrets {
            storage: EngineWebhookStorage::new(Arc::clone(&engine)),
            keys: Arc::clone(&keys),
            writes: Mutex::new(()),
        };
        secrets.provision(&scope, "endpoint", "GRU").expect("provision");
        let plaintext = secrets.get(&scope, "endpoint", "GRU").expect("read");
        assert_eq!(plaintext.len(), 32);
        assert_eq!(secrets.get(&scope, "endpoint", "FRA"), Err(SecretError::Unavailable));
        let durable = engine
            .webhook_metadata_load(
                &webhook_engine_scope(&engine, &scope).expect("scope"),
                COLLECTION,
                "endpoint",
            )
            .expect("load")
            .expect("row");
        assert!(!durable.windows(32).any(|bytes| bytes == plaintext));
        drop(secrets);
        let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
        engine.shutdown().expect("shutdown");
        let engine = open();
        let secrets = EngineWebhookSecrets {
            storage: EngineWebhookStorage::new(Arc::clone(&engine)),
            keys,
            writes: Mutex::new(()),
        };
        assert_eq!(secrets.get(&scope, "endpoint", "GRU").expect("reopen secret"), plaintext);
        secrets.destroy(&scope, "endpoint", "GRU").expect("destroy");
        assert_eq!(secrets.get(&scope, "endpoint", "GRU"), Err(SecretError::NotFound));
        drop(secrets);
        let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole reopened engine") };
        engine.shutdown().expect("close");
        server.join().expect("adapter thread");
        std::fs::remove_dir_all(directory).expect("cleanup");
    }
}
