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
        let mut cipher = self.cipher(scope, &artifact, &lease)?;
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
        let value = DocumentValue::Object(BTreeMap::from([
            ("artifact".into(), DocumentValue::String(artifact)),
            ("version".into(), DocumentValue::String(lease.metadata().version().to_text())),
            (
                "ciphertext".into(),
                DocumentValue::String(base64::engine::general_purpose::STANDARD.encode(encrypted)),
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
                DocumentValue::Null,
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
        let DocumentValue::Object(fields) = document.value() else {
            return Err(SecretError::NotFound);
        };
        let Some(DocumentValue::String(version)) = fields.get("version") else {
            return Err(SecretError::Unavailable);
        };
        let Some(DocumentValue::String(ciphertext)) = fields.get("ciphertext") else {
            return Err(SecretError::Unavailable);
        };
        let version =
            DatabaseKeyVersion::from_text(version).map_err(|_| SecretError::Unavailable)?;
        let lease = self.keys.lease(scope, region, Some(version))?;
        let Some(DocumentValue::String(artifact)) = fields.get("artifact") else {
            return Err(SecretError::Unavailable);
        };
        let ciphertext = base64::engine::general_purpose::STANDARD
            .decode(ciphertext)
            .map_err(|_| SecretError::Unavailable)?;
        self.cipher(scope, artifact, &lease)?
            .open(&ciphertext)
            .map_err(|_| SecretError::Unavailable)
    }
}
