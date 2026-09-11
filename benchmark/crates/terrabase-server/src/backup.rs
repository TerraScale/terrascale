//! Litestream backup streams (S-06, D-29): one stream child per database
//! file, artifact-readiness, and restore-to-a-new-database including
//! point-in-time restore. Backups are files, never replication, never the
//! hot path (D-29); restore always lands as a new database.
//!
//! Readiness is judged from replica artifacts (the snapshot file inside a
//! generation directory) after polling child liveness — a stream that spawns
//! happily and ships nothing is not ready, and a dead child is restarted only
//! within a bounded budget.
//!
//! Object-storage lifecycle (segments older than 24 h to Infrequent
//! Access, older than 7 days to Archive, same-region bucket) is
//! orchestrator-owned bucket configuration per D-29; this fleet only
//! writes the streams.

use std::collections::BTreeMap;
use std::io;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use terrabase::encryption::{DatabaseKeyVersion, KeyProviderIdentity};
use terrabase::regions::RegionCode;

/// A bounded number of child restarts.  A repeatedly failing backup must not
/// become an unbounded process-spawn loop (or hide a broken deployment).
const MAX_RESTARTS: u8 = 3;

/// The database file Litestream streams, inside one engine location.
#[must_use]
pub fn database_file(location: &Path) -> PathBuf {
    terrabase::engine::database_file(location)
}

/// One attached stream: the child and where its artifacts land.
struct Stream {
    child: Child,
    spec: Arc<StreamSpec>,
    restarts: u8,
}

/// An attach operation that has reserved a registry slot but has not yet
/// spawned a child.  Keeping this state in the registry closes the duplicate
/// attach race without holding a mutex across filesystem or process I/O.
enum StreamSlot {
    Starting(Arc<StreamSpec>),
    Running(Stream),
    Restarting(Arc<StreamSpec>, u8),
    Failed(Arc<StreamSpec>, u8),
}

#[derive(Clone)]
struct StreamSpec {
    file: PathBuf,
    replica_dir: PathBuf,
    replica_url: String,
    destination_region: RegionCode,
    endpoint: Option<String>,
    retention: String,
    encryption: BackupEncryptionConfig,
}

/// The encryption metadata required for every backup stream.
///
/// Litestream remains the transport process; TerraBase's encryption seam
/// carries the provider and key-version identity into the generated stream
/// configuration so the eventual provider adapter can authorize the backup
/// key.  No key material is placed in the configuration or logs.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BackupEncryptionConfig {
    /// The service-managed key provider identity.
    pub provider: KeyProviderIdentity,
    /// The database key version used for this backup artifact family.
    pub key_version: DatabaseKeyVersion,
}

impl BackupEncryptionConfig {
    /// Constructs mandatory backup encryption metadata.
    #[must_use]
    pub const fn new(provider: KeyProviderIdentity, key_version: DatabaseKeyVersion) -> Self {
        Self { provider, key_version }
    }
}

/// Where one database's backup artifacts are written.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum BackupDestination {
    /// A local filesystem replica used by development and CI.
    File {
        /// Root under which the database-specific replica directory is made.
        root: PathBuf,
        /// Canonical placement Region owning this destination.
        region: RegionCode,
    },
    /// An OCI S3-compatible Object Storage bucket in one canonical Region.
    ObjectStorage {
        /// S3-compatible endpoint URL supplied by the provider adapter.
        endpoint: String,
        /// Object Storage bucket.
        bucket: String,
        /// Database-independent prefix in the bucket.
        prefix: String,
        /// Canonical placement Region owning this destination.
        region: RegionCode,
    },
}

impl BackupDestination {
    fn region(&self) -> RegionCode {
        match self {
            Self::File { region, .. } | Self::ObjectStorage { region, .. } => *region,
        }
    }

    fn replica(&self, database: &str) -> (PathBuf, String, Option<String>) {
        match self {
            Self::File { root, .. } => {
                let dir = root.join(database);
                (dir.clone(), format!("file://{}", dir.display()), None)
            }
            Self::ObjectStorage { endpoint, bucket, prefix, .. } => {
                let path = if prefix.is_empty() {
                    database.to_owned()
                } else {
                    format!("{prefix}/{database}")
                };
                (PathBuf::new(), format!("s3://{bucket}/{path}"), Some(endpoint.clone()))
            }
        }
    }

    fn valid(&self) -> bool {
        match self {
            Self::File { root, .. } => !root.as_os_str().is_empty(),
            Self::ObjectStorage { endpoint, bucket, prefix, .. } => {
                valid_https_endpoint(endpoint)
                    && !bucket.is_empty()
                    && !prefix.contains("..")
                    && !prefix.chars().any(char::is_control)
            }
        }
    }
}

/// Accept only an HTTPS endpoint with a syntactically valid authority.
///
/// The endpoint is passed into generated Litestream configuration, so this
/// validation must reject credentials and delimiters that could alter the
/// destination or inject configuration lines.  A full URL dependency is not
/// needed here: the endpoint's scheme, authority, and delimiter rules are
/// intentionally small and explicit.
fn valid_https_endpoint(endpoint: &str) -> bool {
    if !endpoint.starts_with("https://")
        || endpoint.chars().any(|character| character.is_control() || character.is_whitespace())
    {
        return false;
    }

    let remainder = &endpoint["https://".len()..];
    if remainder.contains('?') || remainder.contains('#') {
        return false;
    }
    let authority_end = remainder.find(['/', '?', '#']).unwrap_or(remainder.len());
    valid_authority(&remainder[..authority_end])
}

fn valid_authority(authority: &str) -> bool {
    if authority.is_empty() || authority.contains('@') {
        return false;
    }

    if let Some(host_and_port) = authority.strip_prefix('[') {
        let Some(close) = host_and_port.find(']') else {
            return false;
        };
        let host = &host_and_port[..close];
        let suffix = &host_and_port[close + 1..];
        if host.is_empty()
            || !host
                .chars()
                .all(|character| character.is_ascii_hexdigit() || matches!(character, ':' | '.'))
        {
            return false;
        }
        valid_port(suffix)
    } else {
        let (host, port) = authority.find(':').map_or((authority, ""), |separator| {
            (&authority[..separator], &authority[separator..])
        });
        host_is_valid(host) && valid_port(port)
    }
}

fn host_is_valid(host: &str) -> bool {
    !host.is_empty()
        && host.split('.').all(|label| {
            !label.is_empty()
                && !label.starts_with('-')
                && !label.ends_with('-')
                && label
                    .chars()
                    .all(|character| character.is_ascii_alphanumeric() || character == '-')
        })
}

fn valid_port(port: &str) -> bool {
    port.is_empty()
        || (port.starts_with(':')
            && port.len() > 1
            && port[1..].chars().all(|c| c.is_ascii_digit()))
}

/// The complete, validated input needed to attach one stream.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupAttachConfig {
    /// The live `SQLite` location being streamed.
    pub location: PathBuf,
    /// Same-Region backup destination.
    pub destination: BackupDestination,
    /// Litestream retention value, for example `168h`.
    pub retention: String,
    /// Mandatory per-database backup encryption metadata.
    pub encryption: BackupEncryptionConfig,
}

impl BackupAttachConfig {
    /// Builds the complete stream input; encryption is intentionally a
    /// required argument rather than an optional setting.
    #[must_use]
    pub const fn new(
        location: PathBuf,
        destination: BackupDestination,
        retention: String,
        encryption: BackupEncryptionConfig,
    ) -> Self {
        Self { location, destination, retention, encryption }
    }
}

/// One existing database stream from the durable member catalog.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupCatalogEntry {
    /// Stable database name.
    pub database: String,
    /// Region and encrypted destination/stream configuration.
    pub config: BackupAttachConfig,
    /// Region where the database is placed.  The destination must match it.
    pub placement_region: RegionCode,
}

/// The per-member fleet of Litestream streams, one per hosted database.
pub struct BackupStreams {
    litestream: PathBuf,
    streams: Mutex<BTreeMap<String, StreamSlot>>,
    /// The billing plan per database (S-06 plan plumbing, D-25): the
    /// retention window each stream is configured with and each `?at=`
    /// gate is judged against. The billing lane owns this map post-M1
    /// (it calls [`Self::set_plan`]); until then every database reads
    /// the [`DEFAULT_PLAN`] default and unknown plans fail closed to it.
    plans: Mutex<BTreeMap<String, String>>,
}

/// The plan every database runs under until the billing lane says
/// otherwise: the shortest retention window, so nothing over-promises
/// durability (D-25).
pub const DEFAULT_PLAN: &str = "free";

impl BackupStreams {
    /// Starts a fleet that shells out to the given Litestream binary.
    #[must_use]
    pub fn new(litestream: PathBuf) -> Self {
        Self {
            litestream,
            streams: Mutex::new(BTreeMap::new()),
            plans: Mutex::new(BTreeMap::new()),
        }
    }

    /// The member's fleet: the packaged Litestream when present, else a
    /// fleet that refuses every operation (backups off, never degraded).
    #[must_use]
    pub fn open_member() -> Self {
        Self::new(Self::discover().unwrap_or_default())
    }

    /// Opens a fleet and immediately re-attaches the explicit durable
    /// catalog.  This is the startup seam for a Member composition; callers
    /// that need to report an attach refusal can fail startup on the returned
    /// error before exposing readiness.
    ///
    /// # Errors
    ///
    /// Returns the catalog entry validation, duplicate-stream, filesystem, or spawn
    /// error. Streams started by this call are detached if a later entry fails.
    pub fn open_from_catalog<I>(litestream: PathBuf, catalog: I) -> io::Result<Self>
    where
        I: IntoIterator<Item = BackupCatalogEntry>,
    {
        let fleet = Self::new(litestream);
        fleet.reattach_catalog(catalog)?;
        Ok(fleet)
    }

    /// Whether a stream child runs for the database.
    ///
    /// # Panics
    ///
    /// Panics if the stream registry lock is poisoned.
    pub fn attached(&self, database: &str) -> bool {
        self.supervise(database);
        self.streams
            .lock()
            .expect("the stream lock is not poisoned")
            .get(database)
            .is_some_and(|slot| matches!(slot, StreamSlot::Running(_)))
    }

    /// The database's billing plan (S-06 plan plumbing, D-25): the stored
    /// plan, or [`DEFAULT_PLAN`] when the billing lane has named none.
    /// Unknown plans are returned as stored — the retention maps
    /// ([`retention_days_for_plan`], [`retention_for_plan`]) fail those
    /// closed to Free, so a typo never widens a window.
    ///
    /// # Panics
    ///
    /// Panics if the plan lock is poisoned.
    pub fn plan_for(&self, database: &str) -> String {
        self.plans
            .lock()
            .expect("the plan lock is not poisoned")
            .get(database)
            .cloned()
            .unwrap_or_else(|| DEFAULT_PLAN.to_owned())
    }

    /// Records the database's billing plan (the billing lane owns this
    /// post-M1; the API create/restore paths record the fleet default).
    /// The name canonicalizes to lowercase; an empty name resets to
    /// [`DEFAULT_PLAN`].
    ///
    /// # Panics
    ///
    /// Panics if the plan lock is poisoned.
    pub fn set_plan(&self, database: &str, plan: &str) {
        let normalized = plan.trim().to_ascii_lowercase();
        let normalized = if normalized.is_empty() { DEFAULT_PLAN.to_owned() } else { normalized };
        self.plans
            .lock()
            .expect("the plan lock is not poisoned")
            .insert(database.to_owned(), normalized);
    }

    /// The database's PITR retention in days under its current plan
    /// (unknown plans fail closed to Free via [`retention_days_for_plan`]).
    pub fn retention_days(&self, database: &str) -> u64 {
        retention_days_for_plan(&self.plan_for(database))
    }

    /// Attaches one database under its billing plan: records `plan` and
    /// configures the stream with the matching retention window. This is
    /// the attach path the API create/restore routes use; [`Self::attach`]
    /// stays as the raw-retention primitive the tests drive directly.
    ///
    /// # Errors
    ///
    /// Returns the refusal from [`Self::attach`] when the stream cannot
    /// start (no Litestream on `PATH`, an unwritable replica directory, or
    /// a stream already running for the database).
    pub fn attach_with_plan(
        &self,
        database: &str,
        location: &Path,
        replica_root: &Path,
        plan: &str,
    ) -> io::Result<()> {
        self.set_plan(database, plan);
        self.attach(database, location, replica_root, retention_for_plan(&self.plan_for(database)))
    }

    /// Finds Litestream on `PATH`: the pinned 0.5.x the operator packaged.
    /// `None` means backups are simply off (every attach then refuses).
    #[must_use]
    pub fn discover() -> Option<PathBuf> {
        let path = std::env::var_os("PATH")?;
        std::env::split_paths(&path).map(|dir| dir.join("litestream")).find(|candidate| {
            candidate.is_file()
                && std::fs::metadata(candidate).is_ok_and(|meta| {
                    use std::os::unix::fs::PermissionsExt;
                    meta.permissions().mode() & 0o111 != 0
                })
        })
    }

    /// Attaches one database: writes its per-database config (retention per
    /// plan) and starts `litestream replicate` for it. Re-attaching an
    /// already-streamed database refuses — detach first.
    ///
    /// # Errors
    ///
    /// Returns an error when the replica directory or config cannot be
    /// written, the Litestream child cannot start, or a stream already
    /// runs for the database.
    ///
    /// # Panics
    ///
    /// Panics if the stream registry lock is poisoned.
    pub fn attach(
        &self,
        database: &str,
        location: &Path,
        replica_root: &Path,
        retention: &str,
    ) -> io::Result<()> {
        let encryption = BackupEncryptionConfig::new(
            KeyProviderIdentity::LocalFile,
            DatabaseKeyVersion::generate(),
        );
        let config = BackupAttachConfig {
            location: location.to_path_buf(),
            destination: BackupDestination::File {
                root: replica_root.to_path_buf(),
                // Legacy callers predate the placement catalog.  The
                // explicit catalog path below always supplies the real Region.
                region: RegionCode::GRU,
            },
            retention: retention.to_owned(),
            encryption,
        };
        self.attach_with_config(database, RegionCode::GRU, config)
    }

    /// Attaches a stream from an explicit placement and encryption config.
    /// The destination Region must equal the database placement Region and
    /// encryption metadata cannot be omitted by this path.
    ///
    /// # Errors
    ///
    /// Returns a validation, filesystem, duplicate, or process-spawn error.
    ///
    /// # Panics
    ///
    /// Panics if the stream registry lock is poisoned.
    pub fn attach_with_config(
        &self,
        database: &str,
        placement_region: RegionCode,
        config: BackupAttachConfig,
    ) -> io::Result<()> {
        self.attach_reserved(database, placement_region, config).map(|_| ())
    }

    fn attach_reserved(
        &self,
        database: &str,
        placement_region: RegionCode,
        config: BackupAttachConfig,
    ) -> io::Result<Arc<StreamSpec>> {
        let spec = Arc::new(Self::spec_for(database, placement_region, config)?);
        {
            let mut streams = self.streams.lock().expect("the stream lock is not poisoned");
            if streams.contains_key(database) {
                return Err(io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    format!("a stream for {database} already runs or starts"),
                ));
            }
            // Reserve before touching the filesystem or spawning.  A
            // concurrent duplicate now fails without creating a child.
            streams.insert(database.to_owned(), StreamSlot::Starting(spec.clone()));
        }

        let result = self.spawn(&spec);
        self.finish_attach(database, Arc::clone(&spec), result)?;
        Ok(spec)
    }

    /// Completes only the reservation that initiated this spawn.
    fn finish_attach(
        &self,
        database: &str,
        spec: Arc<StreamSpec>,
        result: io::Result<Child>,
    ) -> io::Result<()> {
        let mut streams = self.streams.lock().expect("the stream lock is not poisoned");
        let current = matches!(streams.get(database), Some(StreamSlot::Starting(reserved)) if Arc::ptr_eq(reserved, &spec));
        if !current {
            drop(streams);
            if let Ok(mut child) = result {
                let _ = child.kill();
                let _ = child.wait();
            }
            return Err(io::Error::new(io::ErrorKind::Interrupted, "backup attach was cancelled"));
        }
        match result {
            Ok(child) => {
                streams.insert(
                    database.to_owned(),
                    StreamSlot::Running(Stream { child, spec, restarts: 0 }),
                );
                Ok(())
            }
            Err(error) => {
                streams.remove(database);
                Err(error)
            }
        }
    }

    /// Re-attaches every existing stream from the durable member catalog.
    /// Startup callers must pass the catalog explicitly; no directory scan is
    /// treated as authority because it cannot prove placement or key policy.
    /// If any entry refuses, all streams attached by this call are stopped.
    ///
    /// # Errors
    ///
    /// Returns the first entry's refusal and leaves no partially reattached
    /// child behind.
    pub fn reattach_catalog<I>(&self, catalog: I) -> io::Result<()>
    where
        I: IntoIterator<Item = BackupCatalogEntry>,
    {
        let mut attached = Vec::new();
        for entry in catalog {
            match self.attach_reserved(&entry.database, entry.placement_region, entry.config) {
                Ok(spec) => attached.push((entry.database, spec)),
                Err(error) => {
                    for (database, spec) in attached {
                        self.detach_reservation(&database, &spec);
                    }
                    return Err(error);
                }
            }
        }
        Ok(())
    }

    fn detach_reservation(&self, database: &str, reservation: &Arc<StreamSpec>) {
        let removed = {
            let mut streams = self.streams.lock().expect("the stream lock is not poisoned");
            let matches = streams.get(database).is_some_and(|slot| {
                let spec = match slot {
                    StreamSlot::Starting(spec)
                    | StreamSlot::Restarting(spec, _)
                    | StreamSlot::Failed(spec, _) => spec,
                    StreamSlot::Running(stream) => &stream.spec,
                };
                Arc::ptr_eq(spec, reservation)
            });
            if matches { streams.remove(database) } else { None }
        };
        if let Some(StreamSlot::Running(mut stream)) = removed {
            let _ = stream.child.kill();
            let _ = stream.child.wait();
        }
    }

    /// Alias for startup composition code that calls this operation an
    /// attach rather than a reattach.
    ///
    /// # Errors
    ///
    /// Returns the catalog entry validation, duplicate-stream, filesystem, or spawn
    /// error. Streams started by this call are detached if a later entry fails.
    pub fn attach_catalog<I>(&self, catalog: I) -> io::Result<()>
    where
        I: IntoIterator<Item = BackupCatalogEntry>,
    {
        self.reattach_catalog(catalog)
    }

    /// Borrowed-catalog form for runtime composition that retains its typed
    /// catalog for other startup services.
    ///
    /// # Errors
    ///
    /// Returns the catalog entry validation, duplicate-stream, filesystem, or spawn
    /// error. Streams started by this call are detached if a later entry fails.
    pub fn reattach_existing(&self, catalog: &[BackupCatalogEntry]) -> io::Result<()> {
        self.reattach_catalog(catalog.iter().cloned())
    }

    /// Startup-composition spelling of [`Self::reattach_catalog`].
    ///
    /// # Errors
    ///
    /// Returns the catalog entry validation, duplicate-stream, filesystem, or spawn
    /// error. Streams started by this call are detached if a later entry fails.
    pub fn start_from_catalog<I>(&self, catalog: I) -> io::Result<()>
    where
        I: IntoIterator<Item = BackupCatalogEntry>,
    {
        self.reattach_catalog(catalog)
    }

    fn spec_for(
        database: &str,
        placement_region: RegionCode,
        config: BackupAttachConfig,
    ) -> io::Result<StreamSpec> {
        if !valid_database_name(database)
            || config.retention.trim().is_empty()
            || !config.destination.valid()
            || config.destination.region() != placement_region
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "backup destination must be in the database placement Region and retention must be set",
            ));
        }
        let (mut replica_dir, replica_url, endpoint) = config.destination.replica(database);
        if replica_dir.as_os_str().is_empty() {
            replica_dir = config
                .location
                .parent()
                .unwrap_or_else(|| Path::new("."))
                .join(".terrabase-litestream")
                .join(database);
        }
        Ok(StreamSpec {
            file: database_file(&config.location),
            replica_dir,
            replica_url,
            destination_region: placement_region,
            endpoint,
            retention: config.retention,
            encryption: config.encryption,
        })
    }

    fn spawn(&self, spec: &StreamSpec) -> io::Result<Child> {
        if !spec.replica_dir.as_os_str().is_empty() {
            std::fs::create_dir_all(&spec.replica_dir)?;
        }
        let config = spec.replica_dir.join("litestream.yml");
        // The provider adapter owns object-storage credentials.  The key
        // identity is metadata only; plaintext key material never reaches
        // Litestream, its environment, or this config.
        let endpoint = spec
            .endpoint
            .as_deref()
            .map_or_else(String::new, |endpoint| format!("        endpoint: {endpoint}\n"));
        let yaml = format!(
            "# terrabase-backup-provider: {}\n# terrabase-backup-key-version: {}\n# terrabase-backup-region: {}\ndbs:\n  - path: {}\n    replicas:\n      - url: {}\n{endpoint}        retention: {}\n        sync-interval: 10s\n        encryption:\n          provider: {}\n          key-version: {}\n",
            spec.encryption.provider.as_str(),
            spec.encryption.key_version,
            spec.destination_region,
            spec.file.display(),
            spec.replica_url,
            spec.retention,
            spec.encryption.provider.as_str(),
            spec.encryption.key_version,
        );
        std::fs::write(&config, yaml)?;
        let log_dir = if spec.replica_dir.as_os_str().is_empty() {
            spec.file.parent().unwrap_or_else(|| Path::new("."))
        } else {
            &spec.replica_dir
        };
        std::fs::create_dir_all(log_dir)?;
        let log = std::fs::File::create(log_dir.join("litestream.log"))?;
        Command::new(&self.litestream)
            .arg("replicate")
            .arg("-config")
            .arg(&config)
            .stdout(Stdio::from(log.try_clone()?))
            .stderr(Stdio::from(log))
            .spawn()
    }

    /// Readiness from artifacts: some generation directory holds a
    /// non-empty snapshot of the database file, and the child is live (or was
    /// restarted within the bounded supervision budget).
    ///
    /// # Panics
    ///
    /// Panics if the stream registry lock is poisoned.
    pub fn is_ready(&self, database: &str) -> bool {
        self.supervise(database);
        let streams = self.streams.lock().expect("the stream lock is not poisoned");
        streams.get(database).is_some_and(|slot| {
            matches!(slot, StreamSlot::Running(stream)
                if snapshot_artifact(&stream.spec.replica_dir).is_some())
        })
    }

    /// Polls the stream child and reports whether a running child remains in
    /// the registry.  Readiness additionally requires a snapshot artifact.
    ///
    /// # Panics
    ///
    /// Panics if the stream registry lock is poisoned.
    #[must_use]
    pub fn check_liveness(&self, database: &str) -> bool {
        self.supervise(database);
        self.streams
            .lock()
            .expect("the stream lock is not poisoned")
            .get(database)
            .is_some_and(|slot| matches!(slot, StreamSlot::Running(_)))
    }

    /// Polls one child with `try_wait` and performs at most the bounded
    /// restart budget.  This is intentionally synchronous: readiness probes
    /// get an authoritative liveness answer and never retain a dead child.
    fn supervise(&self, database: &str) {
        let (spec, attempt) = {
            let mut streams = self.streams.lock().expect("the stream lock is not poisoned");
            let Some(slot) = streams.get_mut(database) else { return };
            match slot {
                StreamSlot::Running(stream) => match stream.child.try_wait() {
                    Ok(None) => return,
                    Ok(Some(_)) => {
                        let attempt = stream.restarts.saturating_add(1);
                        let spec = stream.spec.clone();
                        if attempt > MAX_RESTARTS {
                            *slot = StreamSlot::Failed(spec, attempt);
                            return;
                        }
                        *slot = StreamSlot::Restarting(spec.clone(), attempt);
                        (spec, attempt)
                    }
                    Err(_) => {
                        let _ = stream.child.kill();
                        let _ = stream.child.wait();
                        let attempt = stream.restarts.saturating_add(1);
                        let spec = stream.spec.clone();
                        if attempt > MAX_RESTARTS {
                            *slot = StreamSlot::Failed(spec, attempt);
                            return;
                        }
                        *slot = StreamSlot::Restarting(spec.clone(), attempt);
                        (spec, attempt)
                    }
                },
                StreamSlot::Failed(spec, previous) if *previous < MAX_RESTARTS => {
                    let attempt = previous.saturating_add(1);
                    let spec = spec.clone();
                    *slot = StreamSlot::Restarting(spec.clone(), attempt);
                    (spec, attempt)
                }
                StreamSlot::Failed(_, _)
                | StreamSlot::Starting(_)
                | StreamSlot::Restarting(_, _) => {
                    return;
                }
            }
        };

        let result = self.spawn(&spec);
        self.finish_restart(database, spec, attempt, result);
    }

    /// A detached reservation cannot replace or remove a newer stream.
    fn finish_restart(
        &self,
        database: &str,
        spec: Arc<StreamSpec>,
        attempt: u8,
        result: io::Result<Child>,
    ) {
        let mut streams = self.streams.lock().expect("the stream lock is not poisoned");
        let current = matches!(streams.get(database), Some(StreamSlot::Restarting(reserved, current)) if Arc::ptr_eq(reserved, &spec) && *current == attempt);
        if !current {
            drop(streams);
            if let Ok(mut child) = result {
                let _ = child.kill();
                let _ = child.wait();
            }
            return;
        }
        if let Ok(mut child) = result {
            if child.try_wait().is_ok_and(|status| status.is_none()) {
                streams.insert(
                    database.to_owned(),
                    StreamSlot::Running(Stream { child, spec, restarts: attempt }),
                );
                return;
            }
            let _ = child.kill();
            let _ = child.wait();
        }
        if attempt >= MAX_RESTARTS {
            streams.remove(database);
        } else {
            streams.insert(database.to_owned(), StreamSlot::Failed(spec, attempt));
        }
    }

    /// Polls [`Self::is_ready`] until it holds or the deadline passes.
    pub fn wait_until_ready(&self, database: &str, within: Duration) -> bool {
        let deadline = std::time::Instant::now() + within;
        while !self.is_ready(database) {
            if std::time::Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        true
    }

    /// Restores a verified backup into a fresh database location.
    ///
    /// # Errors
    ///
    /// Returns `NotStreamed` without a source stream, the Litestream failure
    /// when download fails, or `Spawn` for an occupied target, an invalid backup,
    /// or an ownership mismatch. Existing target bytes are never removed.
    pub fn restore_to_location(
        &self,
        database: &str,
        at: Option<u64>,
        location: &Path,
    ) -> Result<(), RestoreError> {
        let source = {
            let streams = self.streams.lock().map_err(|_| {
                RestoreError::Spawn(io::Error::other("stream registry unavailable"))
            })?;
            match streams.get(database) {
                Some(StreamSlot::Running(stream)) => {
                    stream.spec.file.parent().map(Path::to_path_buf)
                }
                _ => None,
            }
            .ok_or_else(|| RestoreError::NotStreamed(database.to_owned()))?
        };
        // Admit the destination before constructing EngineConfig. Its
        // `durable` constructor creates missing paths and follows symlinks,
        // so target admission must happen at this boundary. Creation is
        // exclusive; an existing target is checked without following links
        // and must be empty.
        let created_target = prepare_restore_target(location)?;
        let config = if let Ok(config) = terrabase::EngineConfig::durable(location) { config } else {
            if created_target {
                let _ = std::fs::remove_dir(location);
            }
            return Err(RestoreError::Spawn(io::Error::other("restore target unavailable")));
        };
        let parent = location
            .parent()
            .ok_or_else(|| RestoreError::Spawn(io::Error::other("restore target has no parent")))?;
        let download =
            parent.join(format!(".terrabase-backup-{}", terrabase::identity::generate()));
        std::fs::create_dir(&download).map_err(RestoreError::Spawn)?;
        let result = (|| {
            let snapshot = download.join("backup.sqlite3");
            self.restore(database, at, &snapshot)?;
            terrabase::Engine::restore_local_sqlite_backup(&source, &snapshot, config).map_err(
                |error| {
                    RestoreError::Spawn(io::Error::other(format!(
                        "backup import refused: {error:?}"
                    )))
                },
            )
        })();
        let _ = std::fs::remove_dir_all(download);
        if result.is_err() && created_target {
            // Only clean up an empty directory created by this call. Never
            // remove a pre-existing target or a tree that was published.
            if let Ok(metadata) = std::fs::symlink_metadata(location)
                && metadata.file_type().is_dir()
                && !metadata.file_type().is_symlink()
                && std::fs::read_dir(location).is_ok_and(|mut entries| entries.next().is_none())
            {
                let _ = std::fs::remove_dir(location);
            }
        }
        result
    }

    /// Restores the database (at a point in time when given) to `out`,
    /// which must not exist. Runs to completion; Litestream's stderr tail
    /// rides the error so failures are diagnosable.
    ///
    /// # Errors
    ///
    /// Returns [`RestoreError::NotStreamed`] without a stream,
    /// [`RestoreError::Spawn`] when Litestream cannot run, and
    /// [`RestoreError::Litestream`] when it runs and fails.
    ///
    /// # Panics
    ///
    /// Panics if the stream registry lock is poisoned.
    pub fn restore(&self, database: &str, at: Option<u64>, out: &Path) -> Result<(), RestoreError> {
        let replica_url = {
            let streams = self.streams.lock().expect("the stream lock is not poisoned");
            streams
                .get(database)
                .and_then(|slot| match slot {
                    StreamSlot::Running(stream) => Some(stream.spec.replica_url.clone()),
                    StreamSlot::Starting(_)
                    | StreamSlot::Restarting(_, _)
                    | StreamSlot::Failed(_, _) => None,
                })
                .ok_or_else(|| RestoreError::NotStreamed(database.to_owned()))?
        };
        let mut command = Command::new(&self.litestream);
        command.arg("restore").arg("-o").arg(out);
        if let Some(seconds) = at {
            command.arg("-timestamp").arg(rfc3339(seconds));
        }
        command.arg(&replica_url);
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent).map_err(RestoreError::Spawn)?;
        }
        let output = command.output().map_err(RestoreError::Spawn)?;
        if output.status.success() {
            return Ok(());
        }
        let mut tail = String::from_utf8_lossy(&output.stderr).trim().to_owned();
        if tail.is_empty() {
            String::from_utf8_lossy(&output.stdout).trim().clone_into(&mut tail);
        }
        if tail.len() > 400 {
            tail.truncate(400);
        }
        Err(RestoreError::Litestream { status: output.status, stderr_tail: tail })
    }

    /// Stops one database's stream. The artifacts stay (retention owns
    /// them). Absent databases are a no-op. The plan entry drops with
    /// the stream, so a later re-attach starts from the fleet default
    /// until the billing lane re-supplies the plan.
    ///
    /// # Panics
    ///
    /// Panics if the stream registry or plan lock is poisoned.
    pub fn detach(&self, database: &str) {
        if let Some(StreamSlot::Running(mut stream)) =
            self.streams.lock().expect("the stream lock is not poisoned").remove(database)
        {
            let _ = stream.child.kill();
            let _ = stream.child.wait();
        }
        self.plans.lock().expect("the plan lock is not poisoned").remove(database);
    }

    /// Stops every stream (member shutdown).
    ///
    /// # Panics
    ///
    /// Panics if the stream registry or plan lock is poisoned.
    pub fn detach_all(&self) {
        let streams: Vec<String> =
            self.streams.lock().expect("the stream lock is not poisoned").keys().cloned().collect();
        for database in streams {
            self.detach(&database);
        }
    }
}

impl Drop for BackupStreams {
    fn drop(&mut self) {
        let streams =
            std::mem::take(self.streams.get_mut().expect("the stream lock is not poisoned"));
        for slot in streams.into_values() {
            if let StreamSlot::Running(mut stream) = slot {
                let _ = stream.child.kill();
                let _ = stream.child.wait();
            }
        }
    }
}

/// Why a restore refused.
#[derive(Debug)]
pub enum RestoreError {
    /// No stream runs for that database, so there is no replica to read.
    NotStreamed(String),
    /// The Litestream binary could not run.
    Spawn(io::Error),
    /// Litestream ran and failed; the stderr tail says why.
    Litestream {
        /// The failing exit status.
        status: std::process::ExitStatus,
        /// The last of Litestream's stderr, for diagnosis.
        stderr_tail: String,
    },
}

impl std::fmt::Display for RestoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NotStreamed(database) => {
                write!(f, "no backup stream runs for {database}")
            }
            Self::Spawn(error) => write!(f, "litestream did not run: {error}"),
            Self::Litestream { status, stderr_tail } => {
                write!(f, "litestream restore failed ({status}): {stderr_tail}")
            }
        }
    }
}

/// PITR retention per plan in days (D-25: Free 1 / Starter 7 / Pro 30 /
/// Scale 35). Unknown plans fail closed to Free: a shorter window never
/// over-promises durability.
#[must_use]
pub fn retention_days_for_plan(plan: &str) -> u64 {
    match plan {
        "starter" => 7,
        "pro" => 30,
        "scale" => 35,
        _ => 1,
    }
}

/// The Litestream `retention:` value matching [`retention_days_for_plan`].
#[must_use]
pub fn retention_for_plan(plan: &str) -> &'static str {
    match plan {
        "starter" => "168h",
        "pro" => "720h",
        "scale" => "840h",
        _ => "24h",
    }
}

/// How many throwaway point-in-time restores the time-travel cache keeps.
/// Each entry is a whole database file, so the budget is entries, not
/// bytes: eight restores bound the disk while covering distinct moments.
const TIMETRAVEL_MAX_ENTRIES: usize = 8;

/// One cached throwaway restore: the engine serving it and the location
/// directory eviction removes.
struct CachedAt {
    engine: terrabase::Engine,
    location: PathBuf,
}

/// What a time-travel read serves: the historical value and its revision.
#[derive(Debug)]
pub struct TimeTravelRead {
    /// The document's value as of the requested moment.
    pub value: terrabase::types::DocumentValue,
    /// The revision the value carried then (the live read's `ETag` shape).
    pub revision: String,
}

/// Time-travel reads (S-06 (d)): `GET /documents/{id}?at=<ts>` restores to
/// a throwaway location once per (database, second) and serves later reads
/// for the same moment from the cache. The budget is bounded
/// ([`TIMETRAVEL_MAX_ENTRIES`]); eviction drops the engine and removes the
/// directory. Restores are files under the backup root, never the live
/// engine, never replication (D-29).
pub struct TimeTravelCache {
    streams: Arc<BackupStreams>,
    root: PathBuf,
    entries: Mutex<BTreeMap<(String, u64), CachedAt>>,
}

impl TimeTravelCache {
    /// Opens a cache whose throwaway restores land under a fresh directory
    /// inside `root` (one directory per cache instance, so dropping one
    /// cache never sweeps another's restores).
    pub fn new(streams: Arc<BackupStreams>, root: &Path) -> Self {
        static NEXT_CACHE: AtomicU64 = AtomicU64::new(1);
        let number = NEXT_CACHE.fetch_add(1, Ordering::Relaxed);
        let process = std::process::id();
        let root = root.join(format!("timetravel-{process}-{number}"));
        Self { streams, root, entries: Mutex::new(BTreeMap::new()) }
    }

    /// Reads one document as of unix seconds: a cached restore serves,
    /// else the stream restores once and the result caches. `None` means
    /// the document did not exist then (the live 404's shape).
    ///
    /// # Errors
    ///
    /// Returns the [`Self::restore`] refusal when the throwaway restore
    /// cannot run, and [`RestoreError::Spawn`] when the restored database
    /// refuses to serve.
    ///
    /// # Panics
    ///
    /// Panics if the time-travel cache lock is poisoned.
    pub fn read_at(
        &self,
        database: &str,
        tenant: &terrabase::types::TenantId,
        at_seconds: u64,
        collection: &terrabase::types::CollectionName,
        id: &terrabase::types::DocumentId,
    ) -> Result<Option<TimeTravelRead>, RestoreError> {
        let mut entries = self.entries.lock().expect("the time-travel lock is not poisoned");
        let key = (database.to_owned(), at_seconds);
        if !entries.contains_key(&key) {
            let location = self.location_for(database, at_seconds);
            if let Err(error) =
                self.streams.restore_to_location(database, Some(at_seconds), &location)
            {
                let _ = std::fs::remove_dir_all(&location);
                return Err(error);
            }
            match open_restored(&location) {
                Ok(engine) => {
                    if entries.len() >= TIMETRAVEL_MAX_ENTRIES
                        && let Some((_, evicted)) = entries.pop_first()
                    {
                        let _ = std::fs::remove_dir_all(&evicted.location);
                    }
                    entries.insert(key.clone(), CachedAt { engine, location });
                }
                Err(error) => {
                    let _ = std::fs::remove_dir_all(&location);
                    return Err(error);
                }
            }
        }
        let cached = entries.get(&key).expect("the restore just cached");
        read_document(&cached.engine, tenant, collection, id)
    }

    /// The throwaway location for one (database, moment) restore. The name
    /// sanitizes the database so the cache never escapes its root.
    fn location_for(&self, database: &str, at_seconds: u64) -> PathBuf {
        let safe: String = database
            .chars()
            .map(|cell| if cell.is_ascii_alphanumeric() || cell == '-' { cell } else { '_' })
            .collect();
        self.root.join(format!("{safe}-{at_seconds}"))
    }
}

impl Drop for TimeTravelCache {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Opens a serving engine over an already-restored location directory.
fn open_restored(location: &Path) -> Result<terrabase::Engine, RestoreError> {
    let config = terrabase::EngineConfig::durable(location)
        .map_err(|_| io::Error::other("the restored location refused to initialize"))
        .map_err(RestoreError::Spawn)?;
    terrabase::Engine::open(config)
        .map_err(|_| io::Error::other("the restored database refused to open"))
        .map_err(RestoreError::Spawn)
}

/// Reads one document from a throwaway engine: the same snapshot +
/// transaction shape the live read uses, minus sessions (a historical
/// read has no read-your-writes to wait for).
fn read_document(
    engine: &terrabase::Engine,
    tenant: &terrabase::types::TenantId,
    collection: &terrabase::types::CollectionName,
    id: &terrabase::types::DocumentId,
) -> Result<Option<TimeTravelRead>, RestoreError> {
    let stale = |what: &str| {
        RestoreError::Spawn(io::Error::other(format!(
            "the throwaway restore did not serve: {what}"
        )))
    };
    let scope = engine.scope_for(tenant);
    let snapshot = engine.read_snapshot(&scope).map_err(|_| stale("snapshot"))?;
    let operation = crate::read_operation::next().map_err(|()| stale("operation"))?;
    let transaction = engine.begin(&scope, snapshot, operation).map_err(|_| stale("begin"))?;
    let revision =
        transaction.revision_in_collection(collection, id).map_err(|_| stale("revision"))?;
    let Some(revision) = revision else {
        return Ok(None);
    };
    let document = transaction.read_in_collection(collection, id).map_err(|_| stale("read"))?;
    Ok(document.map(|document| TimeTravelRead {
        value: document.value().clone(),
        revision: revision.clone(),
    }))
}

/// Finds a non-empty snapshot artifact under one replica directory: the
/// first LTX snapshot (Litestream 0.5's layout is `ltx/{level}/…ltx`,
/// level 1 = snapshot). `None` until the first sync.
fn snapshot_artifact(replica_dir: &Path) -> Option<PathBuf> {
    let snapshots = std::fs::read_dir(replica_dir.join("ltx").join("1")).ok()?;
    snapshots
        .flatten()
        .map(|entry| entry.path())
        .find(|path| std::fs::metadata(path).is_ok_and(|meta| meta.len() > 0))
}

fn valid_database_name(database: &str) -> bool {
    (1..=63).contains(&database.len())
        && database.chars().all(|character| {
            character.is_ascii_lowercase() || character.is_ascii_digit() || character == '-'
        })
        && !database.starts_with('-')
        && !database.ends_with('-')
}

/// Counts the WAL segments shipped so far (LTX level 0) — tests wait on
/// this to know a write has actually left the building.
#[cfg(test)]
fn wal_segment_count(replica_dir: &Path) -> usize {
    std::fs::read_dir(replica_dir.join("ltx").join("0"))
        .map_or(0, |segments| segments.flatten().count())
}

/// Renders unix seconds as RFC3339 UTC (Litestream's `-timestamp` format).
/// The workspace keeps unix millis everywhere else; this is the one civil
/// conversion, pinned by tests.
fn rfc3339(unix_seconds: u64) -> String {
    let days = i64::try_from(unix_seconds / 86_400).unwrap_or(i64::MAX);
    let seconds_of_day = unix_seconds % 86_400;
    let (year, month, day) = civil_from_days(days);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z",
        seconds_of_day / 3_600,
        (seconds_of_day % 3_600) / 60,
        seconds_of_day % 60
    )
}

/// Howard Hinnant's `civil_from_days`: days since the unix epoch to
/// (y, m, d) in the proleptic Gregorian calendar.
fn civil_from_days(days: i64) -> (i64, u64, u64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (if m <= 2 { y + 1 } else { y }, m.cast_unsigned(), d.cast_unsigned())
}

/// Atomically admits a restore target directory and reports whether this call
/// created it. Missing targets are created exclusively; existing targets must
/// be real directories with no entries. `symlink_metadata` is intentional:
/// restore destinations are never allowed to resolve through a symlink.
fn prepare_restore_target(location: &Path) -> Result<bool, RestoreError> {
    let parent = location
        .parent()
        .ok_or_else(|| RestoreError::Spawn(io::Error::other("restore target has no parent")))?;
    std::fs::create_dir_all(parent).map_err(RestoreError::Spawn)?;

    let metadata = match std::fs::symlink_metadata(location) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            let mut builder = std::fs::DirBuilder::new();
            #[cfg(unix)]
            {
                use std::os::unix::fs::DirBuilderExt as _;
                builder.mode(0o700);
            }
            match builder.create(location) {
                Ok(()) => return Ok(true),
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
                    std::fs::symlink_metadata(location).map_err(RestoreError::Spawn)?
                }
                Err(error) => return Err(RestoreError::Spawn(error)),
            }
        }
        Err(error) => return Err(RestoreError::Spawn(error)),
    };
    if metadata.file_type().is_symlink() || !metadata.file_type().is_dir() {
        return Err(RestoreError::Spawn(io::Error::other("restore target is not a directory")));
    }
    if std::fs::read_dir(location).map_err(RestoreError::Spawn)?.next().is_some() {
        return Err(RestoreError::Spawn(io::Error::other("restore target is not empty")));
    }
    Ok(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    use terrabase::engine::{BulkIngestRequest, CommitOutcome, IngestConflictMode};
    use terrabase::types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId,
    };
    use terrabase_tbep::engine_host::EngineHost;

    static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

    #[test]
    fn restore_target_admission_creates_absent_directory_and_rejects_occupied_targets() {
        let root = TestLocation::new("restore-target-admission");
        let target = root.path().join("nested").join("target");
        assert!(prepare_restore_target(&target).expect("absent target is admitted"));
        assert!(target.is_dir());
        assert!(std::fs::read_dir(&target).expect("target is readable").next().is_none());
        assert!(!prepare_restore_target(&target).expect("empty target remains admissible"));

        std::fs::write(target.join("occupied"), b"preserve").expect("occupy target");
        assert!(matches!(prepare_restore_target(&target), Err(RestoreError::Spawn(_))));
        assert_eq!(std::fs::read(target.join("occupied")).expect("sentinel"), b"preserve");
    }

    #[cfg(unix)]
    #[test]
    fn restore_target_admission_rejects_symlink_without_following_it() {
        use std::os::unix::fs::symlink;

        let root = TestLocation::new("restore-target-symlink");
        let outside = root.path().join("outside");
        let target = root.path().join("target");
        std::fs::create_dir_all(&outside).expect("outside");
        std::fs::write(outside.join("sentinel"), b"preserve").expect("sentinel");
        symlink(&outside, &target).expect("symlink");

        assert!(matches!(prepare_restore_target(&target), Err(RestoreError::Spawn(_))));
        assert_eq!(std::fs::read(outside.join("sentinel")).expect("sentinel"), b"preserve");
    }

    #[test]
    fn object_storage_destination_requires_tls() {
        let insecure = BackupDestination::ObjectStorage {
            endpoint: "http://object-storage.example".into(),
            bucket: "backups".into(),
            prefix: "terrabase".into(),
            region: RegionCode::GRU,
        };
        assert!(!insecure.valid(), "backup transport must not send credentials over plaintext");

        let secure = BackupDestination::ObjectStorage {
            endpoint: "https://object-storage.example".into(),
            bucket: "backups".into(),
            prefix: "terrabase".into(),
            region: RegionCode::GRU,
        };
        assert!(secure.valid());
    }

    #[test]
    fn object_storage_destination_rejects_endpoint_injection_and_malformed_hosts() {
        for endpoint in [
            "https:///bucket",
            "https://:443/bucket",
            "https://object-storage.example: /bucket",
            "https://user:secret@object-storage.example/bucket",
            "https://object-storage.example/bucket?access_key=secret",
            "https://object-storage.example/bucket#fragment",
            "https://object-storage.example/bucket\nendpoint: evil",
            "https://object-storage.example/bucket\t",
        ] {
            let destination = BackupDestination::ObjectStorage {
                endpoint: endpoint.into(),
                bucket: "backups".into(),
                prefix: "terrabase".into(),
                region: RegionCode::GRU,
            };
            assert!(!destination.valid(), "endpoint must be rejected: {endpoint:?}");
        }
    }

    #[test]
    fn attach_with_config_rejects_invalid_object_storage_endpoint_before_spawn() {
        let streams = BackupStreams::new(PathBuf::from("/missing/litestream"));
        let config = BackupAttachConfig::new(
            PathBuf::from("/tmp/database.sqlite"),
            BackupDestination::ObjectStorage {
                endpoint: "https://user:secret@object-storage.example/bucket?x=1".into(),
                bucket: "backups".into(),
                prefix: "terrabase".into(),
                region: RegionCode::GRU,
            },
            "168h".into(),
            BackupEncryptionConfig::new(
                KeyProviderIdentity::LocalFile,
                DatabaseKeyVersion::generate(),
            ),
        );

        let error = streams
            .attach_with_config("database", RegionCode::GRU, config)
            .expect_err("invalid endpoint must fail configuration validation");
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
        assert!(!streams.attached("database"));
    }

    struct TestLocation(PathBuf);

    impl TestLocation {
        fn new(label: &str) -> Self {
            let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
            Self(
                std::env::temp_dir()
                    .join(format!("terrabase-s06-{label}-{}-{number}", std::process::id())),
            )
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestLocation {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn host(label: &str) -> (TestLocation, EngineHost) {
        let location = TestLocation::new(label);
        let host = EngineHost::open(location.path(), 1, "tenant-a", "project-a", "database-a")
            .expect("the engine host opens");
        (location, host)
    }

    fn put(host: &EngineHost, id: &str, value: i64) {
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&scope).expect("the engine serves a snapshot");
        let operation = OperationId::try_new(&format!("put-{id}-{value}"))
            .expect("the operation identity parses");
        let mut transaction = engine.begin(&scope, snapshot, operation).expect("the write begins");
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(id).expect("the document id parses"),
                    CollectionName::try_new("orders").expect("the collection parses"),
                    DocumentValue::Object(BTreeMap::from([(
                        "value".to_owned(),
                        DocumentValue::Integer(value),
                    )])),
                ),
                ExpectedVersion::Any,
            )
            .expect("the put stages");
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {}
            outcome => panic!("the write committed: {outcome:?}"),
        }
    }

    fn orders_of(host: &EngineHost) -> Vec<Document> {
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&scope).expect("the engine serves a snapshot");
        engine
            .snapshot_documents_in_collection(
                &scope,
                &snapshot,
                &CollectionName::try_new("orders").expect("the collection parses"),
            )
            .expect("the collection scan serves")
    }

    /// The fleet plus its replica root (`attach` derives the per-database
    /// directory under it).
    fn streams(label: &str) -> Option<(TestLocation, BackupStreams, PathBuf)> {
        let litestream = BackupStreams::discover()?;
        let replica_root = TestLocation::new(label);
        let root = replica_root.path().to_path_buf();
        let fleet = BackupStreams::new(litestream);
        Some((replica_root, fleet, root))
    }

    /// The replica directory `attach` creates for the standard database.
    fn replica_of(replica_root: &Path) -> PathBuf {
        replica_root.join("database-a")
    }

    /// Waits until the replica directory holds at least `segments` WAL
    /// files, so a write is provably shipped before the next step.
    /// Waits until the shipped WAL segment count exceeds `baseline` — a
    /// write between reading the baseline and this call provably left the
    /// building (schema-init txns already in the baseline don't count).
    fn wait_for_wal_growth(replica_dir: &Path, baseline: usize) -> bool {
        let deadline = std::time::Instant::now() + Duration::from_secs(30);
        while wal_segment_count(replica_dir) <= baseline {
            if std::time::Instant::now() >= deadline {
                eprintln!("litestream log tail: {}", litestream_log_tail(replica_dir));
                return false;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        true
    }

    /// The last kilobyte of the stream's log, for failed-shipping diagnosis.
    fn litestream_log_tail(replica_dir: &Path) -> String {
        std::fs::read_to_string(replica_dir.join("litestream.log")).map_or_else(
            |_| "<no log>".to_owned(),
            |log| {
                let start = log.len().saturating_sub(1_000);
                log[start..].to_owned()
            },
        )
    }

    /// The done-when journey: write, wait, write, restore to between the
    /// two writes — the restored database serves exactly the first write.
    #[test]
    fn restore_between_two_writes_serves_only_the_first() {
        let Some((replica_location, fleet, replica_root)) = streams("pitr") else {
            eprintln!("skipping: litestream is not on PATH");
            return;
        };
        let replica_dir = replica_of(&replica_root);
        let (location, host) = host("pitr-live");
        fleet
            .attach("database-a", location.path(), &replica_root, "24h")
            .expect("the stream attaches");
        assert!(
            fleet.wait_until_ready("database-a", Duration::from_secs(30)),
            "the snapshot artifact lands"
        );

        // The journey: write, wait, write — both writes ship as WAL
        // segments, and the point in time sits strictly between them.
        // Litestream stamps a WAL segment when the NEXT sync finalizes it,
        // so the restore point waits one sync past the first write's
        // shipping before it is chosen (a write is addressable a sync
        // after it leaves the building).
        let baseline = wal_segment_count(&replica_dir);
        put(&host, "orders/first", 1);
        assert!(wait_for_wal_growth(&replica_dir, baseline), "the first write ships");
        std::thread::sleep(Duration::from_millis(2_500));
        let at = unix_now() + 1;
        while unix_now() <= at {
            std::thread::sleep(Duration::from_millis(100));
        }
        let between = wal_segment_count(&replica_dir);
        put(&host, "orders/second", 2);
        assert!(wait_for_wal_growth(&replica_dir, between), "the second write ships");

        let restored_at = TestLocation::new("pitr-restored");
        fleet
            .restore_to_location("database-a", Some(at), restored_at.path())
            .expect("the point-in-time restore runs");
        let restored = host_of_existing(&restored_at);
        let documents = orders_of(&restored);
        let ids: Vec<&str> = documents.iter().map(|document| document.id().as_str()).collect();
        assert_eq!(ids, vec!["orders/first"], "only the first write is restored");
        drop(restored);

        // Latest restore serves both.
        let latest_at = TestLocation::new("pitr-latest");
        fleet
            .restore_to_location("database-a", None, latest_at.path())
            .expect("the latest restore runs");
        let latest = host_of_existing(&latest_at);
        assert_eq!(orders_of(&latest).len(), 2, "the latest restore serves both writes");
        drop(latest);

        fleet.detach_all();
        drop(host);
        drop(location);
        drop(fleet);
        drop(replica_location);
    }

    /// The done-when timing gate: a 100k-document database branches —
    /// restore to a new location — in under one second locally.
    #[test]
    fn branching_100k_documents_restores_under_one_second() {
        let Some((replica_location, fleet, replica_root)) = streams("branch") else {
            eprintln!("skipping: litestream is not on PATH");
            return;
        };
        let (location, host) = host("branch-live");
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let scope = engine.scope_for(&tenant);
        let request = BulkIngestRequest::try_new(
            IngestConflictMode::CreateOnly,
            LocalDurability::Synced,
            Deadline::after(Duration::from_mins(10)).expect("the deadline is positive"),
        )
        .expect("the ingest request validates");
        let source = (0..100_000).map(|index| {
            Document::new(
                DocumentId::try_new(&format!("orders/seed-{index}"))
                    .expect("the document id parses"),
                CollectionName::try_new("orders").expect("the collection parses"),
                DocumentValue::Object(BTreeMap::from([(
                    "value".to_owned(),
                    DocumentValue::Integer(index),
                )])),
            )
        });
        let receipt = engine
            .ingest_stream(&scope, request)
            .expect("the stream opens")
            .run(source, &Cancellation::new());
        assert_eq!(
            receipt.committed(),
            100_000,
            "every seed document commits (termination: {:?}, refusals: {})",
            receipt.termination(),
            receipt.refused()
        );

        fleet
            .attach("database-a", location.path(), &replica_root, "24h")
            .expect("the stream attaches");
        assert!(
            fleet.wait_until_ready("database-a", Duration::from_mins(1)),
            "the snapshot artifact lands"
        );

        let branch_at = TestLocation::new("branch-restored");
        let started = std::time::Instant::now();
        fleet
            .restore_to_location("database-a", None, branch_at.path())
            .expect("the branch restore runs");
        let elapsed = started.elapsed();
        let branch = host_of_existing(&branch_at);
        assert_eq!(orders_of(&branch).len(), 100_000, "the branch serves every document");
        assert!(elapsed < Duration::from_secs(1), "the branch took {elapsed:?}");
        drop(branch);
        fleet.detach_all();
        drop(host);
        drop(location);
        drop(fleet);
        drop(replica_location);
    }

    /// Opens an engine host over an already-restored location directory
    /// (the file exists; the host verifies it serves).
    fn host_of_existing(location: &TestLocation) -> EngineHost {
        EngineHost::open(location.path(), 1, "tenant-a", "project-a", "database-a")
            .expect("the restored location opens")
    }

    fn unix_now() -> u64 {
        SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |elapsed| elapsed.as_secs())
    }

    /// Retention honors the plan windows (D-25): Free 1 / Starter 7 /
    /// Pro 30 / Scale 35 days, unknown plans fail closed to Free.
    #[test]
    fn retention_pins_plan_windows() {
        assert_eq!(retention_days_for_plan("free"), 1);
        assert_eq!(retention_days_for_plan("starter"), 7);
        assert_eq!(retention_days_for_plan("pro"), 30);
        assert_eq!(retention_days_for_plan("scale"), 35);
        assert_eq!(retention_days_for_plan("team"), 1);
        assert_eq!(retention_for_plan("free"), "24h");
        assert_eq!(retention_for_plan("starter"), "168h");
        assert_eq!(retention_for_plan("pro"), "720h");
        assert_eq!(retention_for_plan("scale"), "840h");
        assert_eq!(retention_for_plan("team"), "24h");
    }

    /// The S-06 plan plumbing: databases default to Free, the billing
    /// lane's `set_plan` feeds the retention window, unknown plans fail
    /// closed, and detach resets to the default.
    #[test]
    fn plan_plumbing_defaults_free_and_fails_closed() {
        let fleet = BackupStreams::new(PathBuf::from("/nonexistent-litestream"));
        assert_eq!(fleet.plan_for("database-a"), DEFAULT_PLAN);
        assert_eq!(fleet.retention_days("database-a"), 1);
        fleet.set_plan("database-a", "pro");
        assert_eq!(fleet.plan_for("database-a"), "pro");
        assert_eq!(fleet.retention_days("database-a"), 30);
        fleet.set_plan("database-a", "  Starter ");
        assert_eq!(fleet.plan_for("database-a"), "starter");
        assert_eq!(fleet.retention_days("database-a"), 7);
        fleet.set_plan("database-a", "team");
        assert_eq!(fleet.plan_for("database-a"), "team");
        assert_eq!(
            fleet.retention_days("database-a"),
            1,
            "unknown plans fail closed to the Free window"
        );
        fleet.detach("database-a");
        assert_eq!(fleet.plan_for("database-a"), DEFAULT_PLAN);
    }

    #[cfg(unix)]
    fn fake_litestream(label: &str, marker: &Path, exit_mode: &str) -> (TestLocation, PathBuf) {
        use std::os::unix::fs::PermissionsExt;
        let directory = TestLocation::new(label);
        std::fs::create_dir_all(directory.path()).expect("the fake process directory creates");
        let binary = directory.path().join("litestream");
        let script = format!(
            "#!/bin/sh\nMARKER={}\nprintf '%s\\n' \"$$\" >> \"$MARKER\"\nif [ \"{}\" = \"always\" ] || ([ \"{}\" = \"once\" ] && [ \"$(wc -l < \"$MARKER\")\" -eq 1 ]); then exit 23; fi\nexec sleep 1000\n",
            marker.display(),
            exit_mode,
            exit_mode,
        );
        std::fs::write(&binary, script).expect("the fake process writes");
        let mut permissions =
            std::fs::metadata(&binary).expect("the fake process exists").permissions();
        permissions.set_mode(0o700);
        std::fs::set_permissions(&binary, permissions).expect("the fake process is executable");
        (directory, binary)
    }

    #[cfg(unix)]
    fn explicit_config(
        location: &Path,
        replica_root: &Path,
        region: RegionCode,
    ) -> BackupAttachConfig {
        BackupAttachConfig {
            location: location.to_path_buf(),
            destination: BackupDestination::File { root: replica_root.to_path_buf(), region },
            retention: "24h".to_owned(),
            encryption: BackupEncryptionConfig::new(
                KeyProviderIdentity::LocalFile,
                DatabaseKeyVersion::generate(),
            ),
        }
    }

    #[cfg(unix)]
    fn marker_lines(path: &Path) -> usize {
        std::fs::read_to_string(path).map_or(0, |text| text.lines().count())
    }

    #[cfg(unix)]
    fn wait_for_marker(path: &Path) {
        wait_for_marker_count(path, 1);
    }

    #[cfg(unix)]
    fn wait_for_marker_count(path: &Path, count: usize) {
        let deadline = std::time::Instant::now() + Duration::from_secs(1);
        while marker_lines(path) < count {
            assert!(std::time::Instant::now() < deadline, "the fake process starts");
            std::thread::sleep(Duration::from_millis(5));
        }
    }

    #[cfg(unix)]
    #[test]
    fn explicit_catalog_requires_same_region_and_encryption_metadata() {
        let fleet = BackupStreams::new(PathBuf::from("/does/not/spawn"));
        let location = TestLocation::new("catalog-location");
        let replica = TestLocation::new("catalog-replica");
        let config = explicit_config(location.path(), replica.path(), RegionCode::SFO);
        let refused = fleet.attach_with_config("database-a", RegionCode::GRU, config);
        assert_eq!(
            refused.expect_err("a cross-region destination is refused").kind(),
            io::ErrorKind::InvalidInput
        );
        assert!(!fleet.attached("database-a"));
    }

    #[cfg(unix)]
    #[test]
    fn startup_catalog_reattaches_every_existing_database() {
        let marker = TestLocation::new("catalog-marker");
        std::fs::create_dir_all(marker.path()).expect("the marker directory creates");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("catalog-process", &marker_path, "never");
        let location_a = TestLocation::new("catalog-location-a");
        let location_b = TestLocation::new("catalog-location-b");
        let replica_a = TestLocation::new("catalog-replica-a");
        let replica_b = TestLocation::new("catalog-replica-b");
        let catalog = vec![
            BackupCatalogEntry {
                database: "database-a".to_owned(),
                config: explicit_config(location_a.path(), replica_a.path(), RegionCode::GRU),
                placement_region: RegionCode::GRU,
            },
            BackupCatalogEntry {
                database: "database-b".to_owned(),
                config: explicit_config(location_b.path(), replica_b.path(), RegionCode::SFO),
                placement_region: RegionCode::SFO,
            },
        ];
        let fleet = BackupStreams::open_from_catalog(binary, catalog)
            .expect("the startup catalog reattaches");
        wait_for_marker_count(&marker_path, 2);
        assert!(fleet.attached("database-a"));
        assert!(fleet.attached("database-b"));
        assert_eq!(marker_lines(&marker_path), 2);
        let yaml = std::fs::read_to_string(replica_a.path().join("database-a/litestream.yml"))
            .expect("the stream config is written");
        assert!(yaml.contains("encryption:") && yaml.contains("key-version:"));
        fleet.detach_all();
    }

    #[cfg(unix)]
    #[test]
    fn catalog_rollback_preserves_concurrent_replacement() {
        let marker = TestLocation::new("rollback-marker");
        std::fs::create_dir_all(marker.path()).expect("marker directory");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("rollback-process", &marker_path, "never");
        let location = TestLocation::new("rollback-location");
        let replica = TestLocation::new("rollback-replica");
        let fleet = BackupStreams::new(binary);
        let config = explicit_config(location.path(), replica.path(), RegionCode::GRU);
        let catalog = (0..3).map(|step| {
            if step == 2 {
                // Run between catalog entries, after both original attaches.
                fleet.detach("database-a");
                fleet
                    .attach_with_config("database-a", RegionCode::GRU, config.clone())
                    .expect("replacement attaches");
                fleet.set_plan("database-a", "pro");
            }
            BackupCatalogEntry {
                database: if step == 1 { "database-b" } else { "database-a" }.to_owned(),
                placement_region: RegionCode::GRU,
                config: config.clone(),
            }
        });
        let error = fleet.reattach_catalog(catalog).expect_err("duplicate catalog entry");
        assert_eq!(error.kind(), io::ErrorKind::AlreadyExists);
        assert!(fleet.attached("database-a"), "rollback must preserve the replacement");
        assert_eq!(fleet.plan_for("database-a"), "pro");
        assert!(!fleet.attached("database-b"), "rollback stops its own stream");
    }

    #[cfg(unix)]
    #[test]
    fn stale_spawn_completions_preserve_a_replacement_reservation() {
        let marker = TestLocation::new("reservation-marker");
        std::fs::create_dir_all(marker.path()).expect("marker directory");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("reservation-process", &marker_path, "never");
        let location = TestLocation::new("reservation-location");
        let replica = TestLocation::new("reservation-replica");
        let fleet = BackupStreams::new(binary);
        let old = Arc::new(
            BackupStreams::spec_for(
                "database-a",
                RegionCode::GRU,
                explicit_config(location.path(), replica.path(), RegionCode::GRU),
            )
            .expect("spec"),
        );
        // Identical configuration is still a distinct detach/reattach lifetime.
        let replacement = Arc::new((*old).clone());
        fleet
            .streams
            .lock()
            .expect("registry")
            .insert("database-a".into(), StreamSlot::Starting(Arc::clone(&replacement)));
        let error = fleet
            .finish_attach(
                "database-a",
                Arc::clone(&old),
                Err(io::Error::other("old spawn failed")),
            )
            .expect_err("cancelled attach");
        assert_eq!(error.kind(), io::ErrorKind::Interrupted);
        let child = fleet.spawn(&old).expect("old child");
        let pid = child.id();
        wait_for_marker(&marker_path);
        fleet
            .finish_attach("database-a", Arc::clone(&old), Ok(child))
            .expect_err("old child discarded");
        assert!(
            !Command::new("kill")
                .args(["-0", &pid.to_string()])
                .stderr(Stdio::null())
                .status()
                .expect("probe")
                .success(),
            "the cancelled child is reaped"
        );
        assert!(
            matches!(fleet.streams.lock().expect("registry").get("database-a"), Some(StreamSlot::Starting(spec)) if Arc::ptr_eq(spec, &replacement))
        );
        fleet.streams.lock().expect("registry").insert(
            "database-a".into(),
            StreamSlot::Restarting(Arc::clone(&replacement), MAX_RESTARTS),
        );
        fleet.finish_restart(
            "database-a",
            old,
            MAX_RESTARTS,
            Err(io::Error::other("old restart failed")),
        );
        assert!(
            matches!(fleet.streams.lock().expect("registry").get("database-a"), Some(StreamSlot::Restarting(spec, attempt)) if Arc::ptr_eq(spec, &replacement) && *attempt == MAX_RESTARTS),
            "an exhausted old restart cannot remove the replacement"
        );
    }

    #[cfg(unix)]
    #[test]
    fn concurrent_duplicate_attach_spawns_one_fake_process() {
        let marker = TestLocation::new("duplicate-marker");
        std::fs::create_dir_all(marker.path()).expect("the marker directory creates");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("duplicate-process", &marker_path, "never");
        let location = TestLocation::new("duplicate-location");
        let replica = TestLocation::new("duplicate-replica");
        let fleet = Arc::new(BackupStreams::new(binary));
        let config = explicit_config(location.path(), replica.path(), RegionCode::GRU);
        let first = {
            let fleet = Arc::clone(&fleet);
            let config = config.clone();
            std::thread::spawn(move || {
                fleet.attach_with_config("database-a", RegionCode::GRU, config)
            })
        };
        let second = {
            let fleet = Arc::clone(&fleet);
            std::thread::spawn(move || {
                fleet.attach_with_config("database-a", RegionCode::GRU, config)
            })
        };
        let first = first.join().expect("the first attach joins");
        let second = second.join().expect("the duplicate attach joins");
        assert!(first.is_ok() ^ second.is_ok(), "exactly one attach wins: {first:?} {second:?}");
        wait_for_marker(&marker_path);
        assert_eq!(marker_lines(&marker_path), 1, "the duplicate must not spawn a leaked process");
        fleet.detach_all();
    }

    #[cfg(unix)]
    #[test]
    fn exited_child_is_restarted_only_within_the_bound() {
        let marker = TestLocation::new("restart-marker");
        std::fs::create_dir_all(marker.path()).expect("the marker directory creates");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("restart-process", &marker_path, "once");
        let location = TestLocation::new("restart-location");
        let replica = TestLocation::new("restart-replica");
        let fleet = BackupStreams::new(binary);
        fleet
            .attach_with_config(
                "database-a",
                RegionCode::GRU,
                explicit_config(location.path(), replica.path(), RegionCode::GRU),
            )
            .expect("the first fake process attaches");
        let deadline = std::time::Instant::now() + Duration::from_secs(2);
        while marker_lines(&marker_path) < 2 {
            let _ = fleet.is_ready("database-a");
            assert!(std::time::Instant::now() < deadline, "the bounded restart starts");
            std::thread::sleep(Duration::from_millis(20));
        }
        assert!(fleet.attached("database-a"), "the replacement child is supervised");
        fleet.detach_all();
    }

    #[cfg(unix)]
    #[test]
    fn repeatedly_exited_child_stops_after_bounded_restarts() {
        let marker = TestLocation::new("bounded-marker");
        std::fs::create_dir_all(marker.path()).expect("the marker directory creates");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("bounded-process", &marker_path, "always");
        let location = TestLocation::new("bounded-location");
        let replica = TestLocation::new("bounded-replica");
        let fleet = BackupStreams::new(binary);
        fleet
            .attach_with_config(
                "database-a",
                RegionCode::GRU,
                explicit_config(location.path(), replica.path(), RegionCode::GRU),
            )
            .expect("the fake process attaches");
        let deadline = std::time::Instant::now() + Duration::from_secs(2);
        while fleet.check_liveness("database-a") {
            assert!(std::time::Instant::now() < deadline, "restarts remain bounded");
            std::thread::sleep(Duration::from_millis(20));
        }
        assert!(marker_lines(&marker_path) <= usize::from(MAX_RESTARTS) + 1);
    }

    #[cfg(unix)]
    #[test]
    fn dropping_fleet_stops_fake_process() {
        let marker = TestLocation::new("shutdown-marker");
        std::fs::create_dir_all(marker.path()).expect("the marker directory creates");
        let marker_path = marker.path().join("pids");
        let (_binary_dir, binary) = fake_litestream("shutdown-process", &marker_path, "never");
        let location = TestLocation::new("shutdown-location");
        let replica = TestLocation::new("shutdown-replica");
        let fleet = BackupStreams::new(binary);
        fleet
            .attach_with_config(
                "database-a",
                RegionCode::GRU,
                explicit_config(location.path(), replica.path(), RegionCode::GRU),
            )
            .expect("the fake process attaches");
        wait_for_marker(&marker_path);
        let pid: i32 = std::fs::read_to_string(&marker_path)
            .expect("the fake process records its pid")
            .trim()
            .parse()
            .expect("the pid parses");
        drop(fleet);
        std::thread::sleep(Duration::from_millis(20));
        let status = std::process::Command::new("kill")
            .args(["-0", &pid.to_string()])
            .status()
            .expect("kill probe runs");
        assert!(!status.success(), "dropping the fleet stops the child");
    }

    /// Without a stream there is no replica to time-travel into: the
    /// cache refuses instead of restoring nothing.
    #[test]
    fn time_travel_without_a_stream_refuses() {
        use terrabase::types::{CollectionName, DocumentId, TenantId};
        let fleet = BackupStreams::new(PathBuf::from("/nonexistent-litestream"));
        let root = TestLocation::new("timetravel-nostream");
        let cache = TimeTravelCache::new(Arc::new(fleet), root.path());
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let collection = CollectionName::try_new("orders").expect("the collection parses");
        let id = DocumentId::try_new("orders/first").expect("the document id parses");
        let refused = cache.read_at("database-a", &tenant, 1_700_000_000, &collection, &id);
        assert!(
            matches!(refused, Err(RestoreError::NotStreamed(_))),
            "the cache refuses without a stream: {refused:?}"
        );
    }

    /// The civil conversion is pinned: known instants must render exactly.
    #[test]
    fn rfc3339_pins_known_instants() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        // 2026-09-03T08:00:00Z
        assert_eq!(rfc3339(1_788_422_400), "2026-09-03T08:00:00Z");
        // A leap-year day: 2024-02-29T12:17:41Z
        assert_eq!(rfc3339(1_709_209_061), "2024-02-29T12:17:41Z");
    }
}
