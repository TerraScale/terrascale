//! Durable export service composition shared by member HTTP handlers.

use super::*;

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
        Ok(Self { root, factory, destination, cursor_key, active_jobs: Arc::default() })
    }

    /// Opens encrypted local member dependencies. Production must use `new`
    /// with its admitted provider; this constructor is only for local hosting.
    ///
    /// # Errors
    /// Refuses inaccessible private keys and durable storage.
    pub fn local(root: PathBuf, region: RegionCode) -> Result<Self, ExportError> {
        struct LocalFactory(PathBuf);
        impl ExportArtifactFactory for LocalFactory {
            fn artifacts(&self, database_id: &str) -> Result<Arc<dyn ArtifactPort>, ExportError> {
                crate::export_local::local_artifacts(&self.0, database_id, "local")
            }
        }
        let key = crate::export_local::local_cursor_key(&root)?;
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
            Some(Arc::new(FileExportStore::open(self.root.join(identity))?)),
            Some(self.factory.artifacts(database_id)?),
            *blake3::keyed_hash(&self.cursor_key, database_id.as_bytes()).as_bytes(),
        )?;
        service.active_jobs = self.active_jobs.clone();
        Ok(Arc::new(service))
    }

    pub(crate) fn recover_pending(
        &self,
        database_id: &str,
        service: &ExportService,
    ) -> Result<(), ExportError> {
        let identity = blake3::hash(database_id.as_bytes()).to_hex().to_string();
        let store = FileExportStore::open(self.root.join(identity))?;
        for entry in
            fs::read_dir(&store.root).map_err(|error| ExportError::Io(error.to_string()))?
        {
            let entry = entry.map_err(|error| ExportError::Io(error.to_string()))?;
            let path = entry.path();
            if path.extension().is_none_or(|extension| extension != "json") {
                continue;
            }
            let Some(job) = path.file_stem().and_then(|name| name.to_str()) else {
                continue;
            };
            let Some(state) = store.load(job)? else {
                continue;
            };
            if !matches!(state.status, JobStatus::Accepted | JobStatus::Running) {
                continue;
            }
            if let Some(active) = ActiveJob::acquire(self.active_jobs.clone(), job)? {
                // Recovery runs on one blocking worker, keeping origin work
                // bounded regardless of the number of interrupted jobs.
                let _active = active;
                let _ = service.clone_for_worker().run(job);
            }
        }
        Ok(())
    }

    pub(crate) fn destination(&self) -> ExportDestination {
        self.destination.clone()
    }
}
