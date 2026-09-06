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
    /// Plan PITR retention: Free/Starter/Pro/Scale use 1/7/30/35 days.
    pub retention_days: u8,
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
    closing: bool,
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
        if limits.streams == 0
            || limits.streams > 65_536
            || limits.points_per_stream == 0
            || limits.points_per_stream > 4096
            || limits.staging_bytes_per_stream < 3 * 1024 * 1024
        {
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
        if !matches!(config.retention_days, 1 | 7 | 30 | 35)
            || config.policy_revision.len() > 256
            || config.policy_revision.chars().any(char::is_control)
            || self.streams.len() >= self.limits.streams
            || self.streams.contains_key(&identity)
            || config.policy_revision.trim().is_empty()
            || config.destinations.is_empty()
            || config.destinations.iter().any(|(provider, destination)| {
                *provider != destination.provider()
                    || destination.admitted_region() != config.engine.database_owner().region()
            })
            || config.destinations.len() > 2
            || config.destinations.len() == 2
                && config.destinations[0].0 == config.destinations[1].0
        {
            return Err(FleetError::Configuration);
        }
        config.keys.lease().map_err(|_| FleetError::Capture)?;
        std::fs::create_dir_all(&config.staging).map_err(|_| FleetError::Capture)?;
        config
            .engine
            .advance_backup_frontier(StorageSequence::default())
            .map_err(|_| FleetError::Capture)?;
        self.streams.insert(
            identity,
            Stream {
                config,
                health: StreamHealth::default(),
                due: Instant::now(),
                pending_since: None,
                closing: false,
            },
        );
        Ok(())
    }

    /// Applies an explicit effective account-policy revision. New providers
    /// receive every retained point before replacing the active policy. A
    /// failure leaves existing protection configured and a durable retry intent.
    /// Retired provider artifacts remain retained; policy edits never bulk-delete.
    /// The caller persists the desired policy in its durable lifecycle catalog
    /// and retries this same revision after interruption.
    ///
    /// # Errors
    /// Refuses invalid/provider-mismatched policies, corrupt chains and outages.
    pub async fn change_policy(
        &mut self,
        database: &str,
        revision: &str,
        destinations: Vec<(Provider, Arc<ObjectDestination>)>,
    ) -> Result<(), FleetError> {
        let stream = self.streams.get_mut(database).ok_or(FleetError::Configuration)?;
        if revision.is_empty()
            || revision.len() > 256
            || revision.chars().any(char::is_control)
            || destinations.is_empty()
            || destinations.len() > 2
            || destinations.iter().any(|(provider, destination)| {
                *provider != destination.provider()
                    || destination.admitted_region()
                        != stream.config.engine.database_owner().region()
            })
            || destinations.len() == 2 && destinations[0].0 == destinations[1].0
        {
            return Err(FleetError::Configuration);
        }
        let points = point_paths(&stream.config.staging, self.limits.points_per_stream)?;
        if points.is_empty() {
            return Err(FleetError::Capture);
        }
        let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
        let chain = super::verify_latest_chain_encrypted(&stream.config.staging, reader.as_ref())
            .map_err(|_| FleetError::Capture)?;
        let names = destinations
            .iter()
            .map(|(provider, _)| match provider {
                Provider::Oci => "oci".to_owned(),
                Provider::Tigris => "tigris".to_owned(),
            })
            .collect();
        persist_upload_journal(&stream.config.staging.join("policy-transition"), revision, &names)?;
        for (_, destination) in &destinations {
            for point in &points {
                let name =
                    point.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?;
                destination
                    .upload_point(point, &format!("{database}/{name}"))
                    .await
                    .map_err(|_| FleetError::Upload)?;
            }
        }
        // Commit the exact configured set only after every survivor/new provider
        // independently verifies a complete chain through the same boundary.
        persist_upload_journal(&stream.config.staging.join("policy-active"), revision, &names)?;
        stream.config.destinations = destinations;
        stream.config.policy_revision = revision.to_owned();
        stream.health.protected_sequence =
            chain.last().ok_or(FleetError::Capture)?.boundary_sequence().get();
        stream.health.providers =
            stream.config.destinations.iter().map(|(provider, _)| (*provider, true)).collect();
        stream.health.protected = true;
        stream.due = Instant::now();
        std::fs::remove_file(stream.config.staging.join("policy-transition"))
            .map_err(|_| FleetError::Capture)?;
        super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)
    }

    /// Deletes retained points after the lifecycle authority has durably closed
    /// this database. `retained_destinations` must come from the historical
    /// destination catalog, including retired policies; current policy alone
    /// cannot prove complete deletion. Retry preserves the same closed stream.
    /// Keys must be revoked by the key authority independently of object cleanup.
    ///
    /// # Errors
    /// Refuses missing configured destinations, incomplete listings or provider
    /// failures. No completion receipt is returned until all deletes succeed.
    pub async fn purge_retained(
        &mut self,
        database: &str,
        retained_destinations: &[Arc<ObjectDestination>],
    ) -> Result<usize, FleetError> {
        let stream = self.streams.get_mut(database).ok_or(FleetError::Configuration)?;
        if retained_destinations.is_empty()
            || retained_destinations.len() > 64
            || stream.config.destinations.iter().any(|(_, current)| {
                !retained_destinations.iter().any(|destination| Arc::ptr_eq(current, destination))
            })
        {
            return Err(FleetError::Configuration);
        }
        stream.closing = true;
        stream.health.protected = false;
        std::fs::create_dir_all(&stream.config.staging).map_err(|_| FleetError::Capture)?;
        let mut removed = 0;
        for destination in retained_destinations {
            let journal =
                stream.config.staging.join(format!("deleting-{}", destination.identity()));
            let mut pending =
                read_upload_journal(&journal, "delete-v1", self.limits.points_per_stream)?;
            let points = destination
                .list_points(database, self.limits.points_per_stream)
                .await
                .map_err(|_| FleetError::Upload)?;
            for point in points {
                let name = point.rsplit('/').next().ok_or(FleetError::Capture)?;
                pending.insert(name.to_owned());
            }
            persist_upload_journal(&journal, "delete-v1", &pending)?;
            for name in pending.clone() {
                destination
                    .delete_point(&format!("{database}/{name}"))
                    .await
                    .map_err(|_| FleetError::Upload)?;
                pending.remove(&name);
                persist_upload_journal(&journal, "delete-v1", &pending)?;
                removed += 1;
            }
        }
        match std::fs::remove_dir_all(&stream.config.staging) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => return Err(FleetError::Capture),
        }
        if let Some(parent) = stream.config.staging.parent() {
            super::sync_directory(parent).map_err(|_| FleetError::Capture)?;
        }
        Ok(removed)
    }

    /// Stops future capture for a catalog-closed database, preserving all local
    /// and remote retained artifacts. The lifecycle owner separately revokes
    /// access, stops metering and performs authorized key/artifact deletion.
    /// A running tick must finish before this exclusive operation can execute.
    pub fn detach(&mut self, database: &str) {
        self.streams.remove(database);
    }

    /// Runs each due stream once, continuing independent databases on failure.
    /// Capture/upload remains outside client acknowledgements. The configured
    /// ten-second interval is a scheduling target, never an RPO claim.
    pub async fn sync_once(&mut self) -> Vec<(String, FleetError)> {
        let mut failures = Vec::new();
        for (identity, stream) in &mut self.streams {
            if stream.closing || stream.due > Instant::now() {
                continue;
            }
            let source = stream.config.engine.journaled_high_water();
            if let Ok(sequence) = source {
                stream.health.source_sequence = sequence.unwrap_or_default().get();
            }
            if !stream.health.protected
                || stream.health.source_sequence > stream.health.protected_sequence
            {
                stream.pending_since.get_or_insert_with(Instant::now);
            }
            let result = sync_stream(identity, stream, self.limits).await;
            stream.due = Instant::now() + Duration::from_secs(10);
            match result {
                Ok(()) => {
                    stream.health.failures = 0;
                    stream.pending_since = None;
                }
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
        self.restore_selected(database, provider, point, None, target).await
    }

    /// Restores every complete transaction through an inclusive Unix millisecond
    /// from one independently selected provider's retained encrypted chain.
    ///
    /// # Errors
    /// Refuses timestamps outside verified coverage or an incomplete chain.
    pub async fn restore_at_from_provider(
        &mut self,
        database: &str,
        provider: Provider,
        at_unix_millis: u64,
        target: &Path,
    ) -> Result<super::RecoveryReceipt, FleetError> {
        self.restore_selected(database, provider, None, Some(at_unix_millis), target).await
    }

    async fn restore_selected(
        &mut self,
        database: &str,
        provider: Provider,
        point: Option<&str>,
        at: Option<u64>,
        target: &Path,
    ) -> Result<super::RecoveryReceipt, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        let destination = stream
            .config
            .destinations
            .iter()
            .find(|(kind, _)| *kind == provider)
            .map(|(_, destination)| destination)
            .ok_or(FleetError::Configuration)?;
        let prefixes = destination
            .list_points(database, self.limits.points_per_stream)
            .await
            .map_err(|_| FleetError::Upload)?;
        let staging = stream
            .config
            .staging
            .join(format!("restore-{}", crate::identity::process_generator().generate()));
        std::fs::create_dir(&staging).map_err(|_| FleetError::Capture)?;
        let result = async {
            for prefix in prefixes {
                let name = prefix.rsplit('/').next().ok_or(FleetError::Capture)?;
                let remaining = self
                    .limits
                    .staging_bytes_per_stream
                    .checked_sub(staged_bytes(&stream.config.staging)?)
                    .ok_or(FleetError::StagingFull)?;
                destination
                    .download_point_with_byte_limit(&prefix, &staging.join(name), remaining)
                    .await
                    .map_err(|_| FleetError::Upload)?;
            }
            let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
            if let Some(at) = at {
                return super::RestorePlan::restore_at_unix_millis_encrypted_in_region(
                    at,
                    &staging,
                    target,
                    reader.as_ref(),
                    &stream.config.placement,
                    stream.config.engine.database_owner().region(),
                )
                .map_err(|_| FleetError::Capture);
            }
            match point {
                Some(point) => super::RestorePlan::restore_recovery_point_encrypted_in_region(
                    point,
                    &staging,
                    target,
                    reader.as_ref(),
                    &stream.config.placement,
                    stream.config.engine.database_owner().region(),
                ),
                None => super::RestorePlan::restore_latest_encrypted_in_region(
                    &staging,
                    target,
                    reader.as_ref(),
                    &stream.config.placement,
                    stream.config.engine.database_owner().region(),
                ),
            }
            .map_err(|_| FleetError::Capture)
        }
        .await;
        let _ = std::fs::remove_dir_all(staging);
        result
    }

    /// Returns bounded current health without initiating backup work.
    #[must_use]
    pub fn health(&self, database: &str) -> Option<&StreamHealth> {
        self.streams.get(database).map(|stream| &stream.health)
    }

    /// Observes protection against the current durable journal boundary.
    ///
    /// This performs no upload and does not reuse a cached source boundary.
    /// Success requires every configured provider's latest readback receipt;
    /// an idle unmeasured stream cannot claim a protected zero sequence.
    ///
    /// # Errors
    /// Refuses an unknown stream or an unreadable durable source journal.
    pub fn observe_protection(&self, database: &str) -> Result<StreamHealth, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        let source =
            stream.config.engine.journaled_high_water().map_err(|_| FleetError::Capture)?;
        let mut observed = stream.health.clone();
        observed.source_sequence = source.unwrap_or_default().get();
        let providers_verified = !stream.config.destinations.is_empty()
            && observed.providers.len() == stream.config.destinations.len()
            && stream.config.destinations.iter().all(|(provider, _)| {
                observed.providers.iter().filter(|(actual, ok)| actual == provider && *ok).count()
                    == 1
            });
        observed.protected = observed.protected
            && observed.failures == 0
            && providers_verified
            && observed.protected_sequence >= observed.source_sequence;
        Ok(observed)
    }

    /// Returns the configured policy separately from observed protection.
    #[must_use]
    pub fn policy(&self, database: &str) -> Option<(&str, Vec<Provider>)> {
        self.streams.get(database).map(|stream| {
            (
                stream.config.policy_revision.as_str(),
                stream.config.destinations.iter().map(|(provider, _)| *provider).collect(),
            )
        })
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
            for (provider, _) in &stream.config.destinations {
                let label = match provider {
                    Provider::Oci => "oci",
                    Provider::Tigris => "tigris",
                };
                let healthy = stream
                    .health
                    .providers
                    .iter()
                    .any(|(kind, healthy)| kind == provider && *healthy);
                let _ = writeln!(
                    output,
                    "terrabase_backup_provider_healthy{{database=\"{database}\",provider=\"{label}\"}} {}",
                    u8::from(healthy)
                );
            }
            let _ = writeln!(
                output,
                "terrabase_backup_protected_sequence{{database=\"{database}\"}} {}",
                stream.health.protected_sequence
            );
            let _ = writeln!(
                output,
                "terrabase_backup_pending_seconds{{database=\"{database}\"}} {age}"
            );
            let _ = writeln!(
                output,
                "terrabase_backup_healthy{{database=\"{database}\"}} {}",
                u8::from(stream.health.protected)
            );
        }
        output
    }
}

async fn sync_stream(
    identity: &str,
    stream: &mut Stream,
    limits: FleetLimits,
) -> Result<(), FleetError> {
    enforce_retention(identity, stream, limits).await?;
    let points = point_paths(&stream.config.staging, limits.points_per_stream)?;
    let bytes = staged_bytes(&stream.config.staging)?;
    let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
    let chain = if points.is_empty() {
        Vec::new()
    } else {
        super::verify_latest_chain_encrypted(&stream.config.staging, reader.as_ref())
            .map_err(|_| FleetError::Capture)?
    };
    let base = chain.last().map(super::BackupManifest::boundary_sequence);
    let high_water = stream
        .config
        .engine
        .journaled_high_water()
        .map_err(|_| FleetError::Capture)?
        .unwrap_or_default();
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|_| FleetError::Capture)?
        .as_millis();
    let now = u64::try_from(now).map_err(|_| FleetError::Capture)?;
    let root_due = chain
        .first()
        .is_none_or(|root| now.saturating_sub(root.captured_unix_millis()) >= 86_400_000);
    if stream.health.protected && base == Some(high_water) && !root_due {
        return Ok(());
    }
    let mut captured_boundary = base.unwrap_or_default();
    let mut capture_refusal = None;
    if base != Some(high_water) || root_due {
        if points.len() >= limits.points_per_stream
            || bytes.saturating_add(2 * 1024 * 1024) >= limits.staging_bytes_per_stream
        {
            capture_refusal = Some(FleetError::StagingFull);
        } else {
            let lease = stream.config.keys.lease().map_err(|_| FleetError::Capture)?;
            let encryption = BackupEncryption::from_database_key_lease(
                &lease,
                stream.config.engine.database_owner().region(),
                0,
            )
            .map_err(|_| FleetError::Capture)?;
            let plan = match (base, root_due) {
                (Some(base), false) => BackupPlan::incremental_since(base, encryption),
                _ => BackupPlan::full(encryption),
            }
            .with_reader(reader)
            .with_placement_lock(stream.config.placement.clone())
            .with_archive_byte_limit(limits.staging_bytes_per_stream - bytes - 2 * 1024 * 1024);
            let engine = Arc::clone(&stream.config.engine);
            let staging = stream.config.staging.clone();
            let result = tokio::task::spawn_blocking(move || run_backup(&engine, plan, &staging))
                .await
                .map_err(|_| FleetError::Capture)?;
            match result {
                Ok(backup) => captured_boundary = backup.manifest().boundary_sequence(),
                Err(_) => capture_refusal = Some(FleetError::Capture),
            }
        }
    }
    let points = point_paths(&stream.config.staging, limits.points_per_stream)?;
    stream.health.providers.clear();
    let mut failed = false;
    for (provider, destination) in &stream.config.destinations {
        let mut succeeded = true;
        let journal = stream.config.staging.join(match provider {
            Provider::Oci => "uploaded-oci",
            Provider::Tigris => "uploaded-tigris",
        });
        let mut acknowledged = read_upload_journal(
            &journal,
            &stream.config.policy_revision,
            limits.points_per_stream,
        )?;
        for point in &points {
            let name =
                point.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?;
            if acknowledged.contains(name) {
                continue;
            }
            if destination.upload_point(point, &format!("{identity}/{name}")).await.is_err() {
                succeeded = false;
                break;
            }
            acknowledged.insert(name.to_owned());
            persist_upload_journal(&journal, &stream.config.policy_revision, &acknowledged)?;
        }
        stream.health.providers.push((*provider, succeeded));
        failed |= !succeeded;
    }
    if failed {
        return Err(FleetError::Upload);
    }
    // Advance only after complete read-back verification on EVERY destination.
    stream
        .config
        .engine
        .advance_backup_frontier(captured_boundary)
        .map_err(|_| FleetError::Capture)?;
    stream.health.protected_sequence = captured_boundary.get();
    stream.health.protected = capture_refusal.is_none();
    capture_refusal.map_or(Ok(()), Err)
}

async fn enforce_retention(
    identity: &str,
    stream: &Stream,
    limits: FleetLimits,
) -> Result<(), FleetError> {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|_| FleetError::Capture)?
        .as_millis();
    enforce_retention_at(
        identity,
        stream,
        limits,
        u64::try_from(now).map_err(|_| FleetError::Capture)?,
    )
    .await
}

async fn enforce_retention_at(
    identity: &str,
    stream: &Stream,
    limits: FleetLimits,
    now: u64,
) -> Result<(), FleetError> {
    let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
    let points = super::restore::discover_encrypted_points(&stream.config.staging, reader.as_ref())
        .map_err(|_| FleetError::Capture)?;
    if points.len() > limits.points_per_stream {
        return Err(FleetError::StagingFull);
    }
    let cutoff = now.saturating_sub(u64::from(stream.config.retention_days) * 86_400_000);
    // Keep the newest complete root at/before the retention cutoff and every
    // newer point. The preceding root, rather than a timestamp-only segment
    // filter, preserves the complete chain through the entire promised window.
    let Some(root) = points
        .iter()
        .filter(|point| point.base_sequence().is_none() && point.captured_unix_millis() <= cutoff)
        .max_by_key(|point| (point.captured_unix_millis(), point.boundary_sequence()))
    else {
        return Ok(());
    };
    super::verify_members(&stream.config.staging, root.identity(), root.members())
        .map_err(|_| FleetError::Capture)?;
    let root_name = format!("rp-{}", root.identity());
    for (provider, _) in &stream.config.destinations {
        let path = stream.config.staging.join(match provider {
            Provider::Oci => "uploaded-oci",
            Provider::Tigris => "uploaded-tigris",
        });
        let uploaded =
            read_upload_journal(&path, &stream.config.policy_revision, limits.points_per_stream)?;
        if !uploaded.contains(&root_name) {
            return Ok(());
        }
    }
    // Authentication plus every member digest was verified before admission to
    // deletion. Exclusive fleet execution excludes concurrent local restores.
    for point in &points {
        if point.captured_unix_millis() >= root.captured_unix_millis()
            || point.boundary_sequence() >= root.boundary_sequence()
        {
            continue;
        }
        let name = format!("rp-{}", point.identity());
        for (_, destination) in &stream.config.destinations {
            destination
                .delete_point(&format!("{identity}/{name}"))
                .await
                .map_err(|_| FleetError::Upload)?;
        }
        std::fs::remove_dir_all(stream.config.staging.join(&name))
            .map_err(|_| FleetError::Capture)?;
        super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)?;
        for (provider, _) in &stream.config.destinations {
            let path = stream.config.staging.join(match provider {
                Provider::Oci => "uploaded-oci",
                Provider::Tigris => "uploaded-tigris",
            });
            let mut uploaded = read_upload_journal(
                &path,
                &stream.config.policy_revision,
                limits.points_per_stream,
            )?;
            uploaded.remove(&name);
            persist_upload_journal(&path, &stream.config.policy_revision, &uploaded)?;
        }
    }
    Ok(())
}

fn point_paths(path: &Path, limit: usize) -> Result<Vec<PathBuf>, FleetError> {
    let mut points = Vec::new();
    for entry in std::fs::read_dir(path).map_err(|_| FleetError::Capture)? {
        let entry = entry.map_err(|_| FleetError::Capture)?;
        if entry.file_name().to_str().is_some_and(|name| name.starts_with("rp-")) {
            if !entry.file_type().map_err(|_| FleetError::Capture)?.is_dir() {
                return Err(FleetError::Capture);
            }
            if points.len() >= limit {
                return Err(FleetError::StagingFull);
            }
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
        if metadata.file_type().is_symlink() {
            return Err(FleetError::Capture);
        }
        bytes = bytes
            .checked_add(if metadata.is_dir() {
                staged_bytes(&entry.path())?
            } else {
                metadata.len()
            })
            .ok_or(FleetError::StagingFull)?;
    }
    Ok(bytes)
}

fn read_upload_journal(
    path: &Path,
    revision: &str,
    limit: usize,
) -> Result<std::collections::BTreeSet<String>, FleetError> {
    use std::io::Read;
    let file = match std::fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(std::collections::BTreeSet::default());
        }
        Err(_) => return Err(FleetError::Capture),
    };
    let max = limit.saturating_mul(68).saturating_add(257);
    let mut body = String::new();
    file.take(max as u64 + 1).read_to_string(&mut body).map_err(|_| FleetError::Capture)?;
    if body.len() > max {
        return Err(FleetError::Capture);
    }
    let mut lines = body.lines();
    if lines.next() != Some(revision) {
        return Ok(std::collections::BTreeSet::default());
    }
    let mut points = std::collections::BTreeSet::new();
    for name in lines {
        let identity = name.strip_prefix("rp-").ok_or(FleetError::Capture)?;
        super::manifest::validate_identity(identity).map_err(|_| FleetError::Capture)?;
        points.insert(name.to_owned());
    }
    if points.len() > limit {
        return Err(FleetError::Capture);
    }
    Ok(points)
}

fn persist_upload_journal(
    path: &Path,
    revision: &str,
    points: &std::collections::BTreeSet<String>,
) -> Result<(), FleetError> {
    use std::io::Write;
    let temporary = path.with_extension("pending");
    let mut file = std::fs::File::create(&temporary).map_err(|_| FleetError::Capture)?;
    writeln!(file, "{revision}").map_err(|_| FleetError::Capture)?;
    for point in points {
        writeln!(file, "{point}").map_err(|_| FleetError::Capture)?;
    }
    file.sync_all().map_err(|_| FleetError::Capture)?;
    std::fs::rename(&temporary, path).map_err(|_| FleetError::Capture)?;
    super::sync_directory(path.parent().ok_or(FleetError::Capture)?)
        .map_err(|_| FleetError::Capture)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::backup::tests::{committed, location, open_engine, put, text};
    use crate::encryption::{
        DatabaseKey, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
    };
    use crate::types::LocalDurability;

    struct Keys(Arc<DatabaseKeyLease>);
    impl BackupReader for Keys {
        fn open_backup_envelope(&self, bytes: &[u8]) -> Result<Vec<u8>, EncryptionError> {
            self.0.open_backup_envelope(bytes)
        }
    }
    impl BackupKeys for Keys {
        fn lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            Ok(self.0.clone())
        }
    }

    fn keys(engine: &Engine) -> Arc<dyn BackupKeys> {
        let owner = engine.database_owner();
        let scope = KeyScope::new_with_project(
            owner.environment().as_str(),
            owner.tenant(),
            owner.project(),
            owner.database(),
            KeyPurpose::Backup,
        )
        .unwrap();
        Arc::new(Keys(Arc::new(
            DatabaseKeyLease::test_oci_vault(
                KeyVersionMetadata::new(scope, DatabaseKeyVersion::generate()),
                DatabaseKey::from_bytes([91; 32]),
                Duration::from_secs(60),
            )
            .unwrap(),
        )))
    }

    #[test]
    fn provider_outage_preserves_independent_success_and_source_journal_floor() {
        let runtime = tokio::runtime::Runtime::new().unwrap();
        runtime.block_on(async {
            let (_, engine) = open_engine("fleet-outage-source");
            let engine = Arc::new(engine);
            let broken_path = location("fleet-unavailable-provider");
            std::fs::create_dir_all(&broken_path).unwrap();
            let unavailable =
                object_store::local::LocalFileSystem::new_with_prefix(&broken_path).unwrap();
            std::fs::remove_dir(&broken_path).unwrap();
            std::fs::write(&broken_path, b"provider unavailable").unwrap();
            let good = Arc::new(ObjectDestination::for_test(
                Provider::Oci,
                Arc::new(object_store::memory::InMemory::new()),
                4 * 1024 * 1024,
                8 * 1024 * 1024,
            ));
            let bad = Arc::new(ObjectDestination::for_test(
                Provider::Tigris,
                Arc::new(unavailable),
                4 * 1024 * 1024,
                8 * 1024 * 1024,
            ));
            let staging = location("fleet-outage-staging");
            let mut fleet = NativeBackupFleet::new(FleetLimits {
                streams: 1,
                staging_bytes_per_stream: 16 * 1024 * 1024,
                points_per_stream: 8,
            })
            .unwrap();
            fleet
                .attach(StreamConfig {
                    keys: keys(&engine),
                    engine: engine.clone(),
                    destinations: vec![(Provider::Oci, good), (Provider::Tigris, bad)],
                    policy_revision: "policy-1".into(),
                    retention_days: 1,
                    staging: staging.clone(),
                    placement: PlacementLock::br_only(),
                })
                .unwrap();
            committed(put(
                &engine,
                "alpha",
                "op-1",
                "doc-1",
                "users",
                text("one"),
                LocalDurability::Synced,
            ));
            let identity = engine.database_id().to_string();
            assert_eq!(fleet.sync_once().await, vec![(identity.clone(), FleetError::Upload)]);
            let health = fleet.health(&identity).unwrap();
            assert!(!health.protected);
            assert_eq!(health.protected_sequence, 0);
            assert_eq!(health.providers, vec![(Provider::Oci, true), (Provider::Tigris, false)]);
            assert!(
                !read_upload_journal(&staging.join("uploaded-oci"), "policy-1", 8)
                    .unwrap()
                    .is_empty()
            );
            assert!(
                read_upload_journal(&staging.join("uploaded-tigris"), "policy-1", 8)
                    .unwrap()
                    .is_empty()
            );
            let receipt = fleet
                .restore_from_provider(
                    &identity,
                    Provider::Oci,
                    None,
                    &location("fleet-outage-restore"),
                )
                .await
                .unwrap();
            assert_eq!(receipt.restored_documents(), 1);
            // Even a full staging queue must retry the old pending point once
            // the provider recovers; newer unarchived work stays unprotected.
            committed(put(
                &engine,
                "alpha",
                "op-2",
                "doc-2",
                "users",
                text("two"),
                LocalDurability::Synced,
            ));
            std::fs::remove_file(&broken_path).unwrap();
            std::fs::create_dir(&broken_path).unwrap();
            fleet.limits.points_per_stream = 1;
            fleet.streams.get_mut(&identity).unwrap().due = Instant::now();
            assert_eq!(fleet.sync_once().await, vec![(identity.clone(), FleetError::StagingFull)]);
            let health = fleet.health(&identity).unwrap();
            assert!(health.protected_sequence > 0);
            assert!(health.source_sequence > health.protected_sequence);
            assert_eq!(health.providers, vec![(Provider::Oci, true), (Provider::Tigris, true)]);
        });
    }

    #[test]
    fn native_fleet_restores_both_providers_and_reopens_encrypted_chain() {
        let runtime = tokio::runtime::Runtime::new().unwrap();
        runtime.block_on(async {
            let (_, engine) = open_engine("fleet-source");
            let engine = Arc::new(engine);
            let key_provider = keys(&engine);
            let staging = location("fleet-staging");
            let first = Arc::new(ObjectDestination::for_test(
                Provider::Oci,
                Arc::new(object_store::memory::InMemory::new()),
                4 * 1024 * 1024,
                8 * 1024 * 1024,
            ));
            let second = Arc::new(ObjectDestination::for_test(
                Provider::Tigris,
                Arc::new(object_store::memory::InMemory::new()),
                4 * 1024 * 1024,
                8 * 1024 * 1024,
            ));
            let config = || StreamConfig {
                engine: engine.clone(),
                keys: key_provider.clone(),
                destinations: vec![
                    (Provider::Oci, first.clone()),
                    (Provider::Tigris, second.clone()),
                ],
                policy_revision: "policy-1".into(),
                retention_days: 1,
                staging: staging.clone(),
                placement: PlacementLock::br_only(),
            };
            let limits = FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 16 * 1024 * 1024,
                points_per_stream: 8,
            };
            let mut fleet = NativeBackupFleet::new(limits).unwrap();
            fleet.attach(config()).unwrap();
            committed(put(
                &engine,
                "alpha",
                "op-1",
                "doc-1",
                "users",
                text("one"),
                LocalDurability::Synced,
            ));
            assert!(fleet.sync_once().await.is_empty());
            let identity = engine.database_id().to_string();
            assert!(fleet.health(&identity).unwrap().protected);
            assert!(fleet.observe_protection(&identity).unwrap().protected);
            drop(fleet);
            let mut fleet = NativeBackupFleet::new(limits).unwrap();
            fleet.attach(config()).unwrap();
            committed(put(
                &engine,
                "alpha",
                "op-2",
                "doc-2",
                "users",
                text("two"),
                LocalDurability::Synced,
            ));
            assert!(fleet.sync_once().await.is_empty());
            for provider in [Provider::Oci, Provider::Tigris] {
                let receipt = fleet
                    .restore_from_provider(&identity, provider, None, &location("fleet-restore"))
                    .await
                    .unwrap();
                assert_eq!(receipt.restored_documents(), 2);
                assert_eq!(receipt.chain_depth(), 2);
            }
            assert_eq!(
                fleet.health(&identity).unwrap().providers,
                vec![(Provider::Oci, true), (Provider::Tigris, true)]
            );
            fleet
                .change_policy(&identity, "policy-2", vec![(Provider::Tigris, second.clone())])
                .await
                .unwrap();
            assert_eq!(fleet.policy(&identity).unwrap().0, "policy-2");
            assert_eq!(fleet.health(&identity).unwrap().providers, vec![(Provider::Tigris, true)]);
            // Explicit removal retains old provider history for retention/deletion.
            assert!(!first.list_points(&identity, 8).await.unwrap().is_empty());
            fleet
                .change_policy(
                    &identity,
                    "policy-3",
                    vec![(Provider::Oci, first.clone()), (Provider::Tigris, second.clone())],
                )
                .await
                .unwrap();
            assert!(fleet.health(&identity).unwrap().protected);
            // A new durable write must be visible before another scheduler tick.
            committed(put(
                &engine,
                "alpha",
                "op-observe",
                "doc-1",
                "users",
                text("observe"),
                LocalDurability::Synced,
            ));
            let observed = fleet.observe_protection(&identity).unwrap();
            assert!(!observed.protected);
            assert!(observed.source_sequence > observed.protected_sequence);
            assert!(
                fleet.health(&identity).unwrap().protected,
                "observation leaves historical receipt unchanged"
            );
            assert!(
                fleet.purge_retained(&identity, &[first.clone(), second.clone()]).await.unwrap()
                    > 0
            );
            assert!(first.list_points(&identity, 8).await.unwrap().is_empty());
            assert!(second.list_points(&identity, 8).await.unwrap().is_empty());
            assert!(fleet.sync_once().await.is_empty());
            assert!(!staging.exists());
        });
    }
}
