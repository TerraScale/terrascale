//! Shared, bounded scheduling of encrypted native backup streams.
//!
//! A tick performs at most one capture per database, with one active capture or
//! upload for the entire fleet. Provider failures retain staged points and the
//! independent journal retention floor. No method is called by write admission.
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use super::object_store::{ObjectDestination, Provider};
use super::{BackupEncryption, BackupPlan, BackupReader, PlacementLock, run_backup};
use crate::encryption::{DatabaseKeyLease, EncryptionError};
use crate::engine::Engine;
use crate::types::StorageSequence;

/// Supplies fresh authorization and reads all retained key versions.
/// Implementations must refuse deleted or expired key authorization.
pub trait BackupKeys: BackupReader + Send + Sync {
    /// Authorizes the current backup key for one bounded capture.
    ///
    /// # Errors
    /// Returns the provider's redacted authorization failure.
    fn lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError>;
}

/// Hard member-wide bounds; each stream also has an independent staging cap.
#[derive(Clone, Copy, Debug)]
pub struct FleetLimits {
    /// Maximum hosted streams, including idle streams.
    pub streams: usize,
    /// Maximum ciphertext and manifest bytes staged per stream.
    pub staging_bytes_per_stream: u64,
    /// Maximum retained recovery points per stream before operator retention.
    pub points_per_stream: usize,
}

/// One immutable database stream admitted by the durable member catalog.
pub struct StreamConfig {
    /// Engine whose durable journal is captured.
    pub engine: Arc<Engine>,
    /// Database-bound key provider, including retained-version recovery.
    pub keys: Arc<dyn BackupKeys>,
    /// Independently configured providers selected by effective account policy.
    pub destinations: Vec<(Provider, Arc<ObjectDestination>)>,
    /// Persisted effective policy revision; changing policy requires re-admission.
    pub policy_revision: String,
    /// Private durable staging directory for this database.
    pub staging: PathBuf,
    /// Immutable residency admitted for this database.
    pub placement: PlacementLock,
}

/// Backup state is separate from configured policy and RF=2 health.
#[derive(Clone, Debug, Default)]
pub struct StreamHealth {
    /// Latest journal boundary verified on every configured destination.
    pub protected_sequence: u64,
    /// Sequence currently visible in the source journal.
    pub source_sequence: u64,
    /// Whether all configured providers hold the complete captured chain.
    pub protected: bool,
    /// Consecutive failed ticks; no failure changes the destination policy.
    pub failures: u64,
    /// Per-provider upload results from the latest attempted tick.
    pub providers: Vec<(Provider, bool)>,
}

struct Stream {
    config: StreamConfig,
    health: StreamHealth,
    due: Instant,
    pending_since: Option<Instant>,
}

/// A single shared scheduler, invoked by the member's background runtime.
/// `&mut self` serializes ticks and bounds capture and upload concurrency to one.
pub struct NativeBackupFleet {
    limits: FleetLimits,
    streams: BTreeMap<String, Stream>,
}

/// Redacted scheduling/capture refusal.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FleetError {
    /// Invalid limits, duplicate identity/provider, or missing policy.
    Configuration,
    /// Durable source, staging, or key authorization is unavailable.
    Capture,
    /// A selected destination failed independently.
    Upload,
    /// Staging is full. Pending bytes remain protected from journal pruning.
    StagingFull,
}

impl NativeBackupFleet {
    /// Validates finite member limits before any stream is attached.
    ///
    /// # Errors
    /// Rejects empty limits and staging too small for the bounded manifest.
    pub fn new(limits: FleetLimits) -> Result<Self, FleetError> {
        if limits.streams == 0 || limits.points_per_stream == 0
            || limits.staging_bytes_per_stream < 2 * 1024 * 1024 {
            return Err(FleetError::Configuration);
        }
        Ok(Self { limits, streams: BTreeMap::new() })
    }

    /// Attaches one explicitly catalogued stream and pins its retention floor.
    /// Existing durable points are verified on the first tick before protection
    /// is reported, including after a member restart.
    ///
    /// # Errors
    /// Rejects duplicate streams, unbounded/empty policies and unavailable keys.
    pub fn attach(&mut self, config: StreamConfig) -> Result<(), FleetError> {
        let identity = config.engine.database_id().to_string();
        if self.streams.len() >= self.limits.streams || self.streams.contains_key(&identity)
            || config.policy_revision.trim().is_empty() || config.destinations.is_empty()
            || config.destinations.iter().any(|(provider, destination)| *provider != destination.provider())
            || config.destinations.len() > 2
            || config.destinations.len() == 2 && config.destinations[0].0 == config.destinations[1].0 {
            return Err(FleetError::Configuration);
        }
        config.keys.lease().map_err(|_| FleetError::Capture)?;
        std::fs::create_dir_all(&config.staging).map_err(|_| FleetError::Capture)?;
        config.engine.advance_backup_frontier(StorageSequence::default()).map_err(|_| FleetError::Capture)?;
        self.streams.insert(identity, Stream { config, health: StreamHealth::default(), due: Instant::now(), pending_since: None });
        Ok(())
    }

    /// Runs each due stream once, continuing independent databases on failure.
    /// Capture/upload remains outside client acknowledgements. The configured
    /// ten-second interval is a scheduling target, never an RPO claim.
    pub async fn sync_once(&mut self) -> Vec<(String, FleetError)> {
        let mut failures = Vec::new();
        for (identity, stream) in &mut self.streams {
            if stream.due > Instant::now() { continue; }
            let source = stream.config.engine.journaled_high_water();
            if let Ok(sequence) = source {
                stream.health.source_sequence = sequence.unwrap_or_default().get();
            }
            if !stream.health.protected || stream.health.source_sequence > stream.health.protected_sequence {
                stream.pending_since.get_or_insert_with(Instant::now);
            }
            let result = sync_stream(identity, stream, self.limits).await;
            stream.due = Instant::now() + Duration::from_secs(10);
            match result {
                Ok(()) => { stream.health.failures = 0; stream.pending_since = None; }
                Err(error) => {
                    stream.health.protected = false;
                    stream.health.failures = stream.health.failures.saturating_add(1);
                    failures.push((identity.clone(), error));
                }
            }
        }
        failures
    }

    /// Restores a selected recovery point using one provider independently.
    /// The source remains active; publication always targets a new location.
    ///
    /// # Errors
    /// Refuses unknown providers, incomplete/corrupt chains, exhausted staging,
    /// unauthorized keys and existing targets.
    pub async fn restore_from_provider(
        &mut self,
        database: &str,
        provider: Provider,
        point: Option<&str>,
        target: &Path,
    ) -> Result<super::RecoveryReceipt, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        let destination = stream.config.destinations.iter().find(|(kind, _)| *kind == provider)
            .map(|(_, destination)| destination).ok_or(FleetError::Configuration)?;
        let prefixes = destination.list_points(database, self.limits.points_per_stream).await.map_err(|_| FleetError::Upload)?;
        let staging = stream.config.staging.join(format!("restore-{}", crate::identity::process_generator().generate()));
        std::fs::create_dir(&staging).map_err(|_| FleetError::Capture)?;
        let result = async {
            for prefix in prefixes {
                let name = prefix.rsplit('/').next().ok_or(FleetError::Capture)?;
                destination.download_point(&prefix, &staging.join(name)).await.map_err(|_| FleetError::Upload)?;
                if staged_bytes(&stream.config.staging)? > self.limits.staging_bytes_per_stream {
                    return Err(FleetError::StagingFull);
                }
            }
            let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
            match point {
                Some(point) => super::RestorePlan::restore_recovery_point_encrypted_in_region(point, &staging, target, reader.as_ref(), &stream.config.placement, stream.config.engine.database_owner().region()),
                None => super::RestorePlan::restore_latest_encrypted_in_region(&staging, target, reader.as_ref(), &stream.config.placement, stream.config.engine.database_owner().region()),
            }.map_err(|_| FleetError::Capture)
        }.await;
        let _ = std::fs::remove_dir_all(staging);
        result
    }

    /// Returns bounded current health without initiating backup work.
    #[must_use]
    pub fn health(&self, database: &str) -> Option<&StreamHealth> {
        self.streams.get(database).map(|stream| &stream.health)
    }

    /// Emits native backup frontier and outage gauges for Prometheus.
    /// Pending age measures observed unprotected work; it is not presented as
    /// the timestamp of the last remotely recoverable customer commit.
    #[must_use]
    pub fn prometheus(&self) -> String {
        let mut output = String::new();
        for (database, stream) in &self.streams {
            use std::fmt::Write;
            let age = stream.pending_since.map_or(0.0, |at| at.elapsed().as_secs_f64());
            let _ = writeln!(output, "terrabase_backup_protected_sequence{{database=\"{database}\"}} {}", stream.health.protected_sequence);
            let _ = writeln!(output, "terrabase_backup_pending_seconds{{database=\"{database}\"}} {age}");
            let _ = writeln!(output, "terrabase_backup_healthy{{database=\"{database}\"}} {}", u8::from(stream.health.protected));
        }
        output
    }
}

async fn sync_stream(identity: &str, stream: &mut Stream, limits: FleetLimits) -> Result<(), FleetError> {
    let points = point_paths(&stream.config.staging)?;
    let bytes = staged_bytes(&stream.config.staging)?;
    let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
    let chain = if points.is_empty() { Vec::new() } else {
        super::verify_latest_chain_encrypted(&stream.config.staging, reader.as_ref()).map_err(|_| FleetError::Capture)?
    };
    let base = chain.last().map(super::BackupManifest::boundary_sequence);
    let high_water = stream.config.engine.journaled_high_water().map_err(|_| FleetError::Capture)?.unwrap_or_default();
    if base != Some(high_water) {
        if points.len() >= limits.points_per_stream || bytes.saturating_add(1024 * 1024) >= limits.staging_bytes_per_stream {
            return Err(FleetError::StagingFull);
        }
        let lease = stream.config.keys.lease().map_err(|_| FleetError::Capture)?;
        let encryption = BackupEncryption::from_database_key_lease(&lease, stream.config.engine.database_owner().region(), 0).map_err(|_| FleetError::Capture)?;
        let plan = match base { Some(base) => BackupPlan::incremental_since(base, encryption), None => BackupPlan::full(encryption) }
            .with_reader(reader).with_placement_lock(stream.config.placement.clone())
            .with_archive_byte_limit(limits.staging_bytes_per_stream - bytes - 1024 * 1024);
        let engine = Arc::clone(&stream.config.engine);
        let staging = stream.config.staging.clone();
        tokio::task::spawn_blocking(move || run_backup(&engine, plan, &staging))
            .await.map_err(|_| FleetError::Capture)?.map_err(|_| FleetError::Capture)?;
    }
    let points = point_paths(&stream.config.staging)?;
    stream.health.providers.clear();
    let mut failed = false;
    for (provider, destination) in &stream.config.destinations {
        let mut succeeded = true;
        for point in &points {
            let name = point.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?;
            if destination.upload_point(point, &format!("{identity}/{name}")).await.is_err() {
                succeeded = false;
                break;
            }
        }
        stream.health.providers.push((*provider, succeeded));
        failed |= !succeeded;
    }
    if failed { return Err(FleetError::Upload); }
    // Advance only after complete read-back verification on EVERY destination.
    stream.config.engine.advance_backup_frontier(high_water).map_err(|_| FleetError::Capture)?;
    stream.health.protected_sequence = high_water.get();
    stream.health.protected = true;
    Ok(())
}

fn point_paths(path: &Path) -> Result<Vec<PathBuf>, FleetError> {
    let mut points = Vec::new();
    for entry in std::fs::read_dir(path).map_err(|_| FleetError::Capture)? {
        let entry = entry.map_err(|_| FleetError::Capture)?;
        if entry.file_name().to_str().is_some_and(|name| name.starts_with("rp-")) {
            if !entry.file_type().map_err(|_| FleetError::Capture)?.is_dir() { return Err(FleetError::Capture); }
            points.push(entry.path());
        }
    }
    points.sort();
    Ok(points)
}

fn staged_bytes(path: &Path) -> Result<u64, FleetError> {
    let mut bytes = 0_u64;
    for entry in std::fs::read_dir(path).map_err(|_| FleetError::Capture)? {
        let entry = entry.map_err(|_| FleetError::Capture)?;
        let metadata = std::fs::symlink_metadata(entry.path()).map_err(|_| FleetError::Capture)?;
        if metadata.file_type().is_symlink() { return Err(FleetError::Capture); }
        bytes = bytes.checked_add(if metadata.is_dir() { staged_bytes(&entry.path())? } else { metadata.len() }).ok_or(FleetError::StagingFull)?;
    }
    Ok(bytes)
}
