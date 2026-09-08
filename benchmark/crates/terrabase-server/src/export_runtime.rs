//! Durable export service composition shared by member HTTP handlers.

use super::{
    ActiveJob, Arc, ArtifactPort, BTreeSet, ExportDestination, ExportError, ExportService,
    ExportSource, ExportStore, FileExportStore, JobStatus, Mutex, PathBuf, RegionCode,
};
use crate::export_store::SqliteExportStore;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};

/// Resolves an encrypted artifact provider for one database incarnation.
pub trait ExportArtifactFactory: Send + Sync + 'static {
    /// Resolves a provider with its encryption context bound to the database.
    ///
    /// # Errors
    /// Refuses unavailable keys, providers, or durable destinations.
    fn artifacts(&self, database_id: &str) -> Result<Arc<dyn ArtifactPort>, ExportError>;
}

/// Member-owned export dependencies. Customer requests cannot choose paths,
/// providers, encryption keys, or placement locks.
pub struct ExportRuntime {
    root: PathBuf,
    factory: Arc<dyn ExportArtifactFactory>,
    destination: ExportDestination,
    cursor_key: [u8; 32],
    active_jobs: Arc<Mutex<BTreeSet<String>>>,
    stop_requested: Arc<AtomicBool>,
}

impl ExportRuntime {
    /// Constructs the serving composition from durable, explicit dependencies.
    ///
    /// # Errors
    /// Refuses invalid destinations, zero cursor keys, and inaccessible roots.
    pub fn new(
        root: PathBuf,
        factory: Arc<dyn ExportArtifactFactory>,
        destination: ExportDestination,
        cursor_key: [u8; 32],
    ) -> Result<Self, ExportError> {
        destination.validate()?;
        if cursor_key == [0; 32] {
            return Err(ExportError::EncryptionUnavailable);
        }
        FileExportStore::open(&root)?;
        Ok(Self {
            root,
            factory,
            destination,
            cursor_key,
            active_jobs: Arc::default(),
            stop_requested: Arc::default(),
        })
    }

    /// Opens encrypted local member dependencies. Production must use `new`
    /// with its admitted provider; this constructor is only for local hosting.
    ///
    /// # Errors
    /// Refuses inaccessible private keys and durable storage.
    pub fn local(root: &Path, region: RegionCode) -> Result<Self, ExportError> {
        struct LocalFactory(PathBuf);
        impl ExportArtifactFactory for LocalFactory {
            fn artifacts(&self, database_id: &str) -> Result<Arc<dyn ArtifactPort>, ExportError> {
                crate::export_local::local_artifacts(&self.0, database_id, "local")
            }
        }
        let key = crate::export_local::local_cursor_key(root)?;
        let artifacts = root.join("artifacts");
        Self::new(
            root.join("jobs"),
            Arc::new(LocalFactory(artifacts.clone())),
            ExportDestination::File { root: artifacts, region },
            key,
        )
    }

    pub(crate) fn service(
        &self,
        database_id: &str,
        source: Arc<dyn ExportSource>,
    ) -> Result<Arc<ExportService>, ExportError> {
        // Hashing the incarnation supplies a safe directory component and
        // keeps old jobs inaccessible after delete/recreate of the same name.
        let identity = blake3::hash(database_id.as_bytes()).to_hex().to_string();
        let mut service = ExportService::new(
            Some(source),
            Some(Arc::new(SqliteExportStore::open(self.root.join(identity))?)),
            Some(self.factory.artifacts(database_id)?),
            *blake3::keyed_hash(&self.cursor_key, database_id.as_bytes()).as_bytes(),
        )?;
        service.active_jobs = self.active_jobs.clone();
        service.stop_requested = self.stop_requested.clone();
        Ok(Arc::new(service))
    }

    pub(crate) fn discover(
        &self,
        database_id: &str,
        after: Option<&str>,
        limit: usize,
    ) -> Result<Vec<super::ExportStatus>, ExportError> {
        let identity = blake3::hash(database_id.as_bytes()).to_hex().to_string();
        SqliteExportStore::discover(&self.root.join(identity), after, limit)
    }

    pub(crate) async fn recover_pending(
        &self,
        database_id: &str,
        service: &ExportService,
    ) -> Result<(), ExportError> {
        let identity = blake3::hash(database_id.as_bytes()).to_hex().to_string();
        let store = SqliteExportStore::open(self.root.join(identity))?;
        let mut after: Option<String> = None;
        loop {
            if self.stop_requested.load(Ordering::Acquire) {
                return Ok(());
            }
            let jobs = store.list(after.as_deref(), 50)?;
            if jobs.is_empty() {
                break;
            }
            for job in &jobs {
                if self.stop_requested.load(Ordering::Acquire) {
                    return Ok(());
                }
                let Some(state) = store.load(job)? else {
                    continue;
                };
                if !matches!(state.status, JobStatus::Accepted | JobStatus::Running) {
                    continue;
                }
                if let Some(active) = ActiveJob::acquire(self.active_jobs.clone(), job)? {
                    // One recovery worker processes interrupted jobs in bounded
                    // indexed pages without delaying admitted data service.
                    let _active = active;
                    // Restart recovery must use the same bounded provider
                    // retry policy as newly admitted work. Without this, a
                    // transient outage during startup leaves the job accepted
                    // with no worker or requeue path.
                    let _ = service.clone_for_worker().run_with_retries(job).await;
                }
            }
            after = jobs.last().cloned();
        }
        Ok(())
    }

    /// Pauses process-owned workers at their next durable page boundary.
    /// Accepted jobs retain their identity and resume on member restart.
    pub fn stop(&self) {
        self.stop_requested.store(true, Ordering::Release);
    }

    /// Whether this owner has fenced continued export work for process shutdown.
    /// This does not prove that an in-flight page has finished persisting.
    #[must_use]
    pub fn is_stopping(&self) -> bool {
        self.stop_requested.load(Ordering::Acquire)
    }

    pub(crate) fn destination(&self) -> ExportDestination {
        self.destination.clone()
    }
}

impl ExportRuntime {
    /// Authenticated, confidential pagination token: global registry scan keys
    /// can belong to other scopes and must never appear in a readable cursor.
    pub(crate) fn seal_discovery_cursor(
        &self,
        scope: &[u8],
        plaintext: &[u8],
    ) -> Result<String, ExportError> {
        use aws_lc_rs::{
            aead,
            rand::{SecureRandom as _, SystemRandom},
        };
        use base64::Engine as _;
        let derived =
            blake3::derive_key("TerraScale operation discovery cursor v1", &self.cursor_key);
        let key = aead::LessSafeKey::new(
            aead::UnboundKey::new(&aead::AES_256_GCM, &derived)
                .map_err(|_| ExportError::EncryptionUnavailable)?,
        );
        let mut nonce = [0u8; 12];
        SystemRandom::new().fill(&mut nonce).map_err(|_| ExportError::EncryptionUnavailable)?;
        let mut output = plaintext.to_vec();
        key.seal_in_place_append_tag(
            aead::Nonce::assume_unique_for_key(nonce),
            aead::Aad::from(scope),
            &mut output,
        )
        .map_err(|_| ExportError::EncryptionUnavailable)?;
        let mut token = nonce.to_vec();
        token.extend(output);
        Ok(base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(token))
    }

    pub(crate) fn open_discovery_cursor(
        &self,
        scope: &[u8],
        token: &str,
    ) -> Result<Vec<u8>, ExportError> {
        use aws_lc_rs::aead;
        use base64::Engine as _;
        let invalid = || ExportError::InvalidRequest("invalid operation cursor");
        if token.len() > 2048 {
            return Err(invalid());
        }
        let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .decode(token)
            .map_err(|_| invalid())?;
        let nonce: [u8; 12] =
            bytes.get(..12).ok_or_else(invalid)?.try_into().map_err(|_| invalid())?;
        let derived =
            blake3::derive_key("TerraScale operation discovery cursor v1", &self.cursor_key);
        let key = aead::LessSafeKey::new(
            aead::UnboundKey::new(&aead::AES_256_GCM, &derived)
                .map_err(|_| ExportError::EncryptionUnavailable)?,
        );
        let mut ciphertext = bytes[12..].to_vec();
        key.open_in_place(
            aead::Nonce::assume_unique_for_key(nonce),
            aead::Aad::from(scope),
            &mut ciphertext,
        )
        .map(|value| value.to_vec())
        .map_err(|_| invalid())
    }
}
