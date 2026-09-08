//! Shared, bounded scheduling of encrypted native backup streams.
//!
//! A tick performs at most two captures per database during root rotation, with one active capture or
//! upload for the entire fleet. Provider failures retain staged points and the
//! independent journal retention floor. No method is called by write admission.
mod inventory;
mod rotation;
use inventory::{
    evict_retired_local_chains, inventory_path, inventory_point_limit, inventory_points,
    inventory_points_for, persist_inventory,
};

mod remote;

use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use super::object_store::{ObjectDestination, Provider};
use super::{
    BackupEncryption, BackupEnvelope, BackupPlan, BackupReader, PlacementLock, run_backup,
};
use crate::encryption::{DatabaseKeyLease, EncryptionError};
use crate::engine::Engine;
use crate::observability::{BackupMode, TaskOutcome};
use crate::types::StorageSequence;

/// Retains the admitted engine owner for the complete backup worker lifetime.
/// Server adapters can retain a member lease without extracting an unfenced Engine.
pub trait BackupEngineHandle: Send + Sync {
    /// Returns the real engine held by this admitted owner.
    fn engine(&self) -> &Engine;
}

impl BackupEngineHandle for Engine {
    fn engine(&self) -> &Engine {
        self
    }
}

/// Supplies fresh authorization and reads all retained key versions.
/// Implementations must refuse deleted or expired key authorization.
pub trait BackupKeys: BackupReader + Send + Sync {
    /// Authorizes the current backup key for one bounded capture.
    ///
    /// # Errors
    /// Returns the provider's redacted authorization failure.
    fn lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError>;
}

/// OCI-authorized backup hierarchy, including explicitly retained versions.
///
/// The source must be constructed for the database's separate backup key
/// purpose and durable active backup version; data-purpose leases are refused.
#[derive(Debug)]
pub struct OciBackupKeys {
    source: Arc<dyn crate::encryption::DatabaseKeySource>,
    scope: crate::encryption::KeyScope,
}

impl OciBackupKeys {
    /// Validates the backup hierarchy before a stream can be registered.
    ///
    /// # Errors
    /// Fails closed on unavailable keys, wrong purpose or non-OCI authorization.
    pub fn new(
        source: Arc<dyn crate::encryption::DatabaseKeySource>,
    ) -> Result<Self, EncryptionError> {
        let lease = source.current_lease()?;
        if lease.provider() != crate::encryption::KeyProviderIdentity::OciVault
            || lease.metadata().scope().purpose() != crate::encryption::KeyPurpose::Backup
            || lease.is_expired()
        {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        Ok(Self { scope: lease.metadata().scope().clone(), source })
    }
}

impl BackupKeys for OciBackupKeys {
    fn lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
        let lease = self.source.current_lease()?;
        if lease.metadata().scope() != &self.scope
            || lease.provider() != crate::encryption::KeyProviderIdentity::OciVault
            || lease.is_expired()
        {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        Ok(lease)
    }
}

impl BackupReader for OciBackupKeys {
    fn open_backup_envelope(&self, envelope: &[u8]) -> Result<Vec<u8>, EncryptionError> {
        let version = crate::encryption::ArtifactCipher::envelope_key_version(
            &self.scope,
            crate::encryption::ArtifactKind::Backup,
            3,
            envelope,
        )?;
        let lease = self.source.lease(version)?;
        if lease.metadata().scope() != &self.scope || lease.metadata().version() != version {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        crate::encryption::ArtifactCipher::open_envelope_from_database_key_lease(
            &lease,
            crate::encryption::ArtifactKind::Backup,
            3,
            envelope,
        )
    }
}

/// Hard member-wide bounds; each stream also has an independent staging cap.
#[derive(Clone, Copy, Debug)]
pub struct FleetLimits {
    /// Maximum hosted streams, including idle streams.
    pub streams: usize,
    /// Disk cache budget per stream. Half holds the active chain; half reserves
    /// root replacement or a serialized restore/migration. Retained encrypted
    /// metadata has its own bounded inventory and providers retain archives.
    pub staging_bytes_per_stream: u64,
    /// Separate disk budget for authenticated retained manifests.
    pub inventory_bytes_per_stream: u64,
    /// Maximum locally staged recovery points, including pending uploads.
    pub points_per_stream: usize,
}

/// One immutable database stream admitted by the durable member catalog.
#[derive(Clone)]
pub struct StreamConfig {
    /// Engine whose durable journal is captured.
    pub engine: Arc<dyn BackupEngineHandle>,
    /// Database-bound key provider, including retained-version recovery.
    pub keys: Arc<dyn BackupKeys>,
    /// Independently configured providers selected by effective account policy.
    pub destinations: Vec<(Provider, Arc<ObjectDestination>)>,
    /// Every destination admitted by this database's durable policy history.
    /// The current `destinations` set is included automatically; callers must
    /// provide retired destinations again after restart so deletion can purge
    /// their retained encrypted points.
    pub historical_destinations: Vec<(Provider, Arc<ObjectDestination>)>,
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
    /// Whether every provider verified the complete captured chain. Live
    /// readiness also requires a process-local readback receipt younger than
    /// sixty seconds; persisted upload acknowledgements never establish it.
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
    paused: bool,
    verified_at: Option<Instant>,
    retention_due: Option<Instant>,
}

#[derive(Clone)]
struct DetachedStream {
    keys: Arc<dyn BackupKeys>,
    destinations: Vec<(Provider, Arc<ObjectDestination>)>,
    historical_destinations: Vec<(Provider, Arc<ObjectDestination>)>,
    policy_revision: String,
    retention_days: u8,
    staging: PathBuf,
    placement: PlacementLock,
    paused: bool,
}

/// A single shared scheduler, invoked by the member's background runtime.
/// `&mut self` serializes ticks and bounds capture and upload concurrency to one.
pub struct NativeBackupFleet {
    limits: FleetLimits,
    streams: BTreeMap<String, Stream>,
    suspended: BTreeMap<String, DetachedStream>,
    // Detached databases remain addressable until the lifecycle owner has
    // completed physical deletion. Their stream is closed, but its policy
    // history is still required to purge every retained destination.
    detached: BTreeMap<String, DetachedStream>,
}

impl NativeBackupFleet {
    /// Copies the key and destination handles needed for asynchronous point
    /// discovery, without exposing the mutable stream itself.
    pub fn stream_recovery_point_config(
        &self,
        database: &str,
    ) -> Result<(Arc<dyn BackupKeys>, Vec<(Provider, Arc<ObjectDestination>)>, usize), FleetError>
    {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        Ok((
            stream.config.keys.clone(),
            stream.config.destinations.clone(),
            inventory_point_limit(stream),
        ))
    }

    /// Returns the admitted provider set for a database in its stable policy order.
    pub fn configured_providers(&self, database: &str) -> Result<Vec<Provider>, FleetError> {
        if let Some(stream) = self.streams.get(database) {
            return Ok(stream.config.destinations.iter().map(|(provider, _)| *provider).collect());
        }
        if let Some(stream) = self.suspended.get(database) {
            return Ok(stream.destinations.iter().map(|(provider, _)| *provider).collect());
        }
        if let Some(stream) = self.detached.get(database) {
            return Ok(stream.destinations.iter().map(|(provider, _)| *provider).collect());
        }
        Err(FleetError::Configuration)
    }

    /// Resolves a retained point from the first provider whose remote
    /// inventory and encrypted manifests authenticate successfully. A local
    /// inventory entry is only a discovery hint; provider bytes are checked
    /// before the provider is returned so a corrupt first copy cannot fence
    /// off a healthy fallback copy.
    pub async fn resolve_recovery_point(
        &self,
        database: &str,
        selection_kind: &str,
        point_id: &str,
    ) -> Result<(super::BackupManifest, Provider), FleetError> {
        let (keys, destinations, point_limit) = {
            let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
            (
                stream.config.keys.clone(),
                stream.config.destinations.clone(),
                inventory_point_limit(stream),
            )
        };
        Self::resolve_recovery_point_with_config(
            database,
            selection_kind,
            point_id,
            keys,
            destinations,
            point_limit,
        )
        .await
    }

    /// Resolves a retained point using an owned provider configuration.
    ///
    /// This entry point lets callers release a synchronous fleet lock before
    /// starting remote provider I/O.
    pub async fn resolve_recovery_point_with_config(
        database: &str,
        selection_kind: &str,
        point_id: &str,
        keys: Arc<dyn BackupKeys>,
        destinations: Vec<(Provider, Arc<ObjectDestination>)>,
        point_limit: usize,
    ) -> Result<(super::BackupManifest, Provider), FleetError> {
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| FleetError::Capture)?
            .as_millis() as u64;
        let mut saw_provider_failure = false;
        for (provider, destination) in &destinations {
            let points = match remote::remote_points_with_keys(
                database,
                keys.clone(),
                point_limit,
                destination,
            )
            .await
            {
                Ok(points) => points,
                Err(FleetError::Capture | FleetError::Upload) => {
                    saw_provider_failure = true;
                    continue;
                }
                Err(error) => return Err(error),
            };
            let selected = match selection_kind {
                "latest" if point_id.is_empty() => points
                    .into_iter()
                    .filter(|point| point.retain_until_unix_millis() > now)
                    .max_by_key(|point| (point.captured_unix_millis(), point.boundary_sequence())),
                "point_id" => points.into_iter().find(|point| {
                    point.identity() == point_id && point.retain_until_unix_millis() > now
                }),
                _ => None,
            };
            if let Some(point) = selected {
                return Ok((point, *provider));
            }
        }
        if saw_provider_failure { Err(FleetError::Upload) } else { Err(FleetError::Capture) }
    }

    /// Captures one self-contained snapshot for every physical participant at
    /// the caller's already admitted read cuts.  Each tuple is a physical
    /// stream identity and its pinned boundary; no stream chooses a fresh
    /// high-water mark here.
    pub async fn capture_participants_at(
        &mut self,
        participants: &[(String, StorageSequence)],
    ) -> Result<Vec<super::BackupManifest>, FleetError> {
        if participants.is_empty() {
            return Err(FleetError::Configuration);
        }
        let limits = self.limits;
        let mut seen: BTreeSet<String> = BTreeSet::new();
        let mut manifests = Vec::with_capacity(participants.len());
        for (database, boundary) in participants {
            if !seen.insert(database.clone()) || seen.len() > limits.streams {
                return Err(FleetError::Configuration);
            }
            let stream = self.streams.get_mut(database).ok_or(FleetError::Configuration)?;
            if stream.closing || stream.paused {
                return Err(FleetError::Configuration);
            }
            let owner = stream.config.engine.engine().database_owner();
            let tenant = crate::types::TenantId::try_new(&owner.tenant().to_text())
                .map_err(|_| FleetError::Capture)?;
            let lifecycle = stream
                .config
                .engine
                .engine()
                .lifecycle_state(&stream.config.engine.engine().scope_for(&tenant))
                .map_err(|_| FleetError::Capture)?;
            if lifecycle.paused || lifecycle.access_revoked() {
                stream.paused = lifecycle.paused;
                return Err(FleetError::Configuration);
            }
            stream.health.protected = false;
            stream.verified_at = None;
            let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
            let lease = stream.config.keys.lease().map_err(|_| FleetError::Capture)?;
            let encryption = BackupEncryption::from_database_key_lease(&lease, owner.region(), 0)
                .map_err(|_| FleetError::Capture)?;
            let plan = BackupPlan::snapshot_at(*boundary, encryption)
                .with_retention_days(stream.config.retention_days)
                .map_err(|_| FleetError::Configuration)?
                .with_reader(reader)
                .with_placement_lock(stream.config.placement.clone())
                .with_archive_byte_limit(
                    limits.staging_bytes_per_stream.saturating_sub(2 * 1024 * 1024),
                );
            let engine = Arc::clone(&stream.config.engine);
            let staging = stream.config.staging.clone();
            let staging_for_run = staging.clone();
            let result = tokio::task::spawn_blocking(move || {
                run_backup(engine.engine(), plan, &staging_for_run)
            })
            .await
            .map_err(|_| FleetError::Capture)?
            .map_err(|_| FleetError::Capture)?;
            let point = result.manifest().clone();
            let point_path = staging.join(format!("rp-{}", point.identity()));
            point.verify(&staging).map_err(|_| FleetError::Capture)?;
            if upload_destinations(database, stream, std::slice::from_ref(&point_path), limits)
                .await?
            {
                return Err(FleetError::Upload);
            }
            manifests.push(point);
        }
        Ok(manifests)
    }

    /// Atomically publishes one envelope catalog entry after all participant
    /// manifests and archive members have verified locally. The catalog lives
    /// beside the first participant's private staging tree and is ignored by
    /// ordinary point discovery until the complete marker is renamed.
    pub async fn publish_envelope(&self, envelope: &BackupEnvelope) -> Result<PathBuf, FleetError> {
        let first = envelope.participants().first().ok_or(FleetError::Configuration)?;
        // The local catalog and remote prefix are owned by the first physical
        // participant.  Keep the logical envelope identity bound to that same
        // owner; otherwise a caller could publish a validly checksummed
        // envelope under one database and later report it for another.
        if envelope.database_id() != first.database_id {
            return Err(FleetError::Configuration);
        }
        for participant in envelope.participants() {
            let stream =
                self.streams.get(&participant.database_id).ok_or(FleetError::Configuration)?;
            participant.manifest.verify(&stream.config.staging).map_err(|_| FleetError::Capture)?;
        }
        let stream = self.streams.get(&first.database_id).ok_or(FleetError::Configuration)?;
        let catalog = stream.config.staging.join("envelopes");
        std::fs::create_dir_all(&catalog).map_err(|_| FleetError::Capture)?;
        let name = format!("env-{}", envelope.digest());
        let temporary = catalog.join(format!(".{name}.staging"));
        let published = catalog.join(&name);
        std::fs::create_dir_all(&temporary).map_err(|_| FleetError::Capture)?;
        let bytes = envelope.encode().map_err(|_| FleetError::Capture)?;
        let marker = temporary.join("envelope.tbv1");
        std::fs::write(&marker, bytes).map_err(|_| FleetError::Capture)?;
        let file = std::fs::OpenOptions::new()
            .read(true)
            .open(&marker)
            .map_err(|_| FleetError::Capture)?;
        file.sync_all().map_err(|_| FleetError::Capture)?;
        // The provider must never observe an envelope whose local staged
        // directory entry can still be lost on crash.  Participant archives
        // are already sealed independently; make this envelope attempt
        // durable before beginning remote publication, then expose it locally
        // only after every configured provider has acknowledged it.
        super::sync_directory(&temporary).map_err(|_| FleetError::Capture)?;
        for (_provider, destination) in &stream.config.destinations {
            let prefix = format!("{}/envelopes/{name}", first.database_id);
            destination.upload_envelope(&marker, &prefix).await.map_err(|_| FleetError::Upload)?;
        }
        // Keep the local catalog unpublished until every configured provider
        // has acknowledged the exact staged envelope.  A failed provider
        // must leave the durable marker retryable rather than exposing an
        // envelope that is only partially replicated.
        std::fs::rename(&temporary, &published).map_err(|_| FleetError::Capture)?;
        super::sync_directory(&catalog).map_err(|_| FleetError::Capture)?;
        Ok(published)
    }

    /// Reads and strictly decodes a provider-published envelope catalog entry.
    /// The provider artifact is the authority for remote restore discovery;
    /// local staging is never substituted when it is absent or corrupt.
    pub async fn download_envelope(
        &self,
        database: &str,
        digest: &str,
        provider: Provider,
    ) -> Result<BackupEnvelope, FleetError> {
        let destination = self.envelope_destination(database, provider)?;
        Self::download_envelope_from_destination(database, digest, destination).await
    }

    /// Returns the admitted destination for an envelope download.
    ///
    /// The returned destination is independently reference counted so callers
    /// can release the fleet lock before awaiting provider I/O.
    pub fn envelope_destination(
        &self,
        database: &str,
        provider: Provider,
    ) -> Result<Arc<ObjectDestination>, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        stream
            .config
            .destinations
            .iter()
            .find(|(kind, _)| *kind == provider)
            .map(|(_, destination)| Arc::clone(destination))
            .ok_or(FleetError::Configuration)
    }

    /// Returns current destinations followed by every retained historical
    /// destination for envelope recovery. Policy changes retain old copies so
    /// a point published before the transition remains recoverable after the
    /// active destination set changes.
    pub fn recovery_envelope_destinations(
        &self,
        database: &str,
    ) -> Result<Vec<(Provider, Arc<ObjectDestination>)>, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        let mut destinations = Vec::new();
        let mut seen = BTreeSet::new();
        for (provider, destination) in
            stream.config.destinations.iter().chain(stream.config.historical_destinations.iter())
        {
            if seen.insert(destination.identity().to_owned()) {
                destinations.push((*provider, Arc::clone(destination)));
            }
        }
        Ok(destinations)
    }

    /// Downloads and authenticates an envelope using an already admitted
    /// destination. The destination is owned by the future, so no fleet lock
    /// needs to remain held while provider I/O is in flight.
    pub async fn download_envelope_from_destination(
        database: &str,
        digest: &str,
        destination: Arc<ObjectDestination>,
    ) -> Result<BackupEnvelope, FleetError> {
        if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err(FleetError::Configuration);
        }
        let prefix = format!("{database}/envelopes/env-{digest}");
        let bytes = destination.download_envelope(&prefix).await.map_err(|_| FleetError::Upload)?;
        let envelope = BackupEnvelope::decode(&bytes).map_err(|_| FleetError::Capture)?;
        if envelope.digest() != digest || envelope.database_id() != database {
            return Err(FleetError::Capture);
        }
        Ok(envelope)
    }
}

async fn purge_retained_parts(
    database: &str,
    retained_destinations: &[Arc<ObjectDestination>],
    keys: &dyn BackupKeys,
    staging: &Path,
    retention_days: u8,
) -> Result<usize, FleetError> {
    if retained_destinations.is_empty() || retained_destinations.len() > 64 {
        return Err(FleetError::Configuration);
    }
    std::fs::create_dir_all(staging).map_err(|_| FleetError::Capture)?;
    let retained = inventory_points_for(staging, keys, retention_days)?;
    let mut removed = 0;
    for destination in retained_destinations {
        let journal = staging.join(format!("deleting-{}", destination.identity()));
        let mut pending = read_upload_journal(
            &journal,
            "delete-v1",
            (usize::from(retention_days) + 1) * 8_640 * 2 + 8_640,
        )?;
        pending.extend(retained.iter().map(|point| format!("rp-{}", point.identity())));
        let points = destination
            .list_points(database, (usize::from(retention_days) + 1) * 8_640 * 2 + 8_640)
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
    match std::fs::remove_dir_all(staging) {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(_) => return Err(FleetError::Capture),
    }
    if let Some(parent) = staging.parent() {
        super::sync_directory(parent).map_err(|_| FleetError::Capture)?;
    }
    Ok(removed)
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

/// Receipt returned only after a native lifecycle effect has completed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LifecycleReceipt {
    /// Durable lifecycle operation identity.
    pub operation_id: String,
    /// Database whose backup lifecycle changed.
    pub database: String,
    /// Lifecycle action applied.
    pub action: String,
    /// Authenticated recovery point identity when one exists.
    pub point_id: String,
    /// Authenticated recovery point digest when one exists.
    pub point_digest: String,
}

impl NativeBackupFleet {
    /// Captures and verifies one native point for a lifecycle operation.
    ///
    /// # Errors
    /// Returns [`FleetError`] if capture, retention lookup, or verification
    /// fails.
    pub async fn lifecycle_snapshot(
        &mut self,
        database: &str,
        operation_id: &str,
    ) -> Result<LifecycleReceipt, FleetError> {
        let manifest = self.capture_snapshot(database).await?;
        let points = self.retained_points(database)?;
        if points.iter().all(|point| point.boundary_sequence() != manifest.boundary_sequence()) {
            return Err(FleetError::Capture);
        }
        Ok(LifecycleReceipt {
            operation_id: operation_id.to_owned(),
            database: database.to_owned(),
            action: "snapshot".into(),
            point_id: manifest.identity().to_owned(),
            point_digest: manifest.identity().to_owned(),
        })
    }

    /// Verifies that the native fleet retains an authenticated point.
    ///
    /// # Errors
    /// Returns [`FleetError`] if retention lookup fails or no point exists.
    pub fn lifecycle_verify(
        &self,
        database: &str,
        operation_id: &str,
    ) -> Result<LifecycleReceipt, FleetError> {
        let points = self.retained_points(database)?;
        if points.is_empty() {
            return Err(FleetError::Capture);
        }
        let point = points.last().ok_or(FleetError::Capture)?;
        Ok(LifecycleReceipt {
            operation_id: operation_id.to_owned(),
            database: database.to_owned(),
            action: "verify_snapshot_placement".into(),
            point_id: point.identity().to_owned(),
            point_digest: point.identity().to_owned(),
        })
    }

    /// Deletes all retained points through the fleet-owned historical destinations.
    ///
    /// # Errors
    /// Returns [`FleetError`] if the database is unknown or purge fails.
    pub async fn lifecycle_delete(
        &mut self,
        database: &str,
        operation_id: &str,
    ) -> Result<LifecycleReceipt, FleetError> {
        let removed = if self.streams.contains_key(database) {
            let destinations = self
                .streams
                .get(database)
                .ok_or(FleetError::Configuration)?
                .config
                .historical_destinations
                .iter()
                .map(|(_, destination)| Arc::clone(destination))
                .collect::<Vec<_>>();
            self.purge_retained(database, &destinations).await?
        } else if let Some(stream) = self.detached.get(database) {
            let destinations = stream
                .historical_destinations
                .iter()
                .map(|(_, destination)| Arc::clone(destination))
                .collect::<Vec<_>>();
            let removed = purge_retained_parts(
                database,
                &destinations,
                stream.keys.as_ref(),
                &stream.staging,
                stream.retention_days,
            )
            .await?;
            self.detached.remove(database);
            removed
        } else {
            return Err(FleetError::Configuration);
        };
        self.streams.remove(database);
        Ok(LifecycleReceipt {
            operation_id: operation_id.to_owned(),
            database: database.to_owned(),
            action: "delete_owned_data".into(),
            point_id: String::new(),
            point_digest: removed.to_string(),
        })
    }

    /// Validates finite member limits before any stream is attached.
    ///
    /// # Errors
    /// Rejects empty limits and staging too small for the bounded manifest.
    pub fn new(limits: FleetLimits) -> Result<Self, FleetError> {
        if limits.streams == 0
            || limits.streams > 65_536
            || limits.points_per_stream == 0
            || limits.points_per_stream > 1_000_000
            || limits.staging_bytes_per_stream < 3 * 1024 * 1024
            || limits.inventory_bytes_per_stream < 4 * 1024 * 1024
        {
            return Err(FleetError::Configuration);
        }
        Ok(Self {
            limits,
            streams: BTreeMap::new(),
            suspended: BTreeMap::new(),
            detached: BTreeMap::new(),
        })
    }

    /// Attaches one explicitly catalogued stream and pins its retention floor.
    /// Existing durable points are verified on the first tick before protection
    /// is reported, including after a member restart.
    ///
    /// # Errors
    /// Rejects duplicate streams, unbounded/empty policies and unavailable keys.
    pub fn attach(&mut self, mut config: StreamConfig) -> Result<(), FleetError> {
        let identity = config.engine.engine().database_id().to_string();
        if !matches!(config.retention_days, 1 | 7 | 30 | 35)
            || config.policy_revision.len() > 256
            || config.policy_revision.chars().any(char::is_control)
            || self.streams.len() + self.suspended.len() + self.detached.len()
                >= self.limits.streams
            || self.streams.contains_key(&identity)
            || self.suspended.contains_key(&identity)
            || self.detached.contains_key(&identity)
            || config.policy_revision.trim().is_empty()
            || config.destinations.is_empty()
            || config.destinations.iter().any(|(provider, destination)| {
                *provider != destination.provider()
                    || destination.admitted_region()
                        != config.engine.engine().database_owner().region()
            })
            || config.destinations.len() > 2
            || config.destinations.len() == 2
                && config.destinations[0].0 == config.destinations[1].0
        {
            return Err(FleetError::Configuration);
        }
        let mut seen_destinations = BTreeSet::new();
        if config.historical_destinations.iter().any(|(provider, destination)| {
            *provider != destination.provider()
                || destination.admitted_region() != config.engine.engine().database_owner().region()
                || !seen_destinations.insert(destination.identity().to_owned())
        }) {
            return Err(FleetError::Configuration);
        }
        let mut historical_destinations = config.historical_destinations.clone();
        for destination in config.destinations.iter().cloned() {
            if seen_destinations.insert(destination.1.identity().to_owned()) {
                historical_destinations.push(destination);
            }
        }
        let required_history = read_policy_history(&config.staging.join("policy-active"))?;
        let available_history = historical_destinations
            .iter()
            .map(|(provider, destination)| destination_history_key(*provider, destination))
            .collect::<BTreeSet<_>>();
        if !required_history.is_subset(&available_history) {
            return Err(FleetError::Configuration);
        }
        config.historical_destinations = historical_destinations;
        config.keys.lease().map_err(|_| FleetError::Capture)?;
        let owner = config.engine.engine().database_owner();
        let tenant = crate::types::TenantId::try_new(&owner.tenant().to_text())
            .map_err(|_| FleetError::Capture)?;
        let lifecycle = config
            .engine
            .engine()
            .lifecycle_state(&config.engine.engine().scope_for(&tenant))
            .map_err(|_| FleetError::Capture)?;
        if lifecycle.access_revoked() {
            return Err(FleetError::Configuration);
        }
        std::fs::create_dir_all(&config.staging).map_err(|_| FleetError::Capture)?;
        config
            .engine
            .engine()
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
                paused: lifecycle.paused,
                verified_at: None,
                retention_due: None,
            },
        );
        Ok(())
    }

    /// Forces one independently verified capture through the current journal
    /// boundary and returns the exact immutable point for operation receipts.
    ///
    /// # Errors
    /// Refuses closed/unknown streams, unavailable keys, staging and providers.
    pub async fn capture_snapshot(
        &mut self,
        database: &str,
    ) -> Result<super::BackupManifest, FleetError> {
        let stream = self.streams.get_mut(database).ok_or(FleetError::Configuration)?;
        if stream.closing || stream.paused {
            return Err(FleetError::Configuration);
        }
        let owner = stream.config.engine.engine().database_owner();
        let tenant = crate::types::TenantId::try_new(&owner.tenant().to_text())
            .map_err(|_| FleetError::Capture)?;
        let lifecycle = stream
            .config
            .engine
            .engine()
            .lifecycle_state(&stream.config.engine.engine().scope_for(&tenant))
            .map_err(|_| FleetError::Capture)?;
        if lifecycle.paused || lifecycle.access_revoked() {
            stream.paused = lifecycle.paused;
            return Err(FleetError::Configuration);
        }
        stream.health.protected = false;
        stream.verified_at = None;
        sync_stream(database, stream, self.limits).await?;
        let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
        super::verify_latest_chain_encrypted(&stream.config.staging, reader.as_ref())
            .map_err(|_| FleetError::Capture)?
            .pop()
            .ok_or(FleetError::Capture)
    }

    /// Lists authenticated retained point metadata, bounded by the stream limit.
    /// These source facts include captured clocks and immutable retention horizon.
    ///
    /// # Errors
    /// Refuses corrupt manifests, unavailable keys or an exceeded point limit.
    pub fn retained_points(
        &self,
        database: &str,
    ) -> Result<Vec<super::BackupManifest>, FleetError> {
        let mut points = if let Some(stream) = self.streams.get(database) {
            inventory_points(stream)?
        } else if let Some(stream) = self.detached.get(database) {
            inventory_points_for(&stream.staging, stream.keys.as_ref(), stream.retention_days)?
        } else {
            return Err(FleetError::Configuration);
        };
        points.sort_by_key(|point| (point.captured_unix_millis(), point.boundary_sequence()));
        Ok(points)
    }

    /// Lists locally published multi-participant recovery envelopes for an
    /// admitted stream. Envelope publication is independent from ordinary
    /// point discovery; a corrupt published envelope fails closed rather than
    /// allowing a participant manifest to be mistaken for a logical point.
    pub fn retained_envelopes(&self, database: &str) -> Result<Vec<BackupEnvelope>, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        let catalog = stream.config.staging.join("envelopes");
        let entries = match std::fs::read_dir(&catalog) {
            Ok(entries) => entries,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(_) => return Err(FleetError::Capture),
        };
        let mut envelopes = Vec::new();
        for entry in entries {
            let entry = entry.map_err(|_| FleetError::Capture)?;
            let file_type = entry.file_type().map_err(|_| FleetError::Capture)?;
            if !file_type.is_dir() {
                continue;
            }
            let name = entry.file_name();
            let Some(digest) = name.to_str().and_then(|name| name.strip_prefix("env-")) else {
                continue;
            };
            if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
                continue;
            }
            let marker = entry.path().join("envelope.tbv1");
            let bytes = std::fs::read(marker).map_err(|_| FleetError::Capture)?;
            let envelope = BackupEnvelope::decode(&bytes).map_err(|_| FleetError::Capture)?;
            if envelope.digest() != digest || envelope.database_id() != database {
                return Err(FleetError::Capture);
            }
            envelopes.push(envelope);
        }
        envelopes.sort_by_key(|envelope| {
            (envelope.captured_unix_millis(), envelope.digest().to_owned())
        });
        Ok(envelopes)
    }

    /// Returns the latest artifact-bound physical retention deadline. An empty
    /// inventory has no retained horizon; an unavailable inventory is an error.
    /// Historical destination inventories remain the lifecycle catalog's input.
    ///
    /// # Errors
    /// Returns the same inventory/authentication failures as `retained_points`.
    pub fn retained_until_unix_millis(&self, database: &str) -> Result<Option<u64>, FleetError> {
        Ok(self
            .retained_points(database)?
            .iter()
            .map(super::BackupManifest::retain_until_unix_millis)
            .max())
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
                        != stream.config.engine.engine().database_owner().region()
            })
            || destinations.len() == 2 && destinations[0].0 == destinations[1].0
        {
            return Err(FleetError::Configuration);
        }
        persist_inventory(stream, self.limits)?;
        let points = inventory_points(stream)?;
        let chain = super::restore::resolve_encrypted_points_chain(
            &points,
            &super::restore::Selection::Latest,
        )
        .map_err(|_| FleetError::Capture)?;
        let names = destinations
            .iter()
            .map(|(provider, destination)| destination_history_key(*provider, destination))
            .collect();
        persist_upload_journal(&stream.config.staging.join("policy-transition"), revision, &names)?;
        remote::migrate_points(database, stream, self.limits, &destinations).await?;
        // Commit the exact configured set only after every survivor/new provider
        // independently verifies a complete chain through the same boundary.
        let mut historical_destinations = stream.config.historical_destinations.clone();
        historical_destinations.extend(destinations.iter().cloned());
        let mut seen_destinations = BTreeSet::new();
        historical_destinations
            .retain(|(_, destination)| seen_destinations.insert(destination.identity().to_owned()));
        let historical_names = historical_destinations
            .iter()
            .map(|(provider, destination)| destination_history_key(*provider, destination))
            .collect::<BTreeSet<_>>();
        persist_upload_journal(
            &stream.config.staging.join("policy-active"),
            revision,
            &historical_names,
        )?;
        stream.config.destinations = destinations;
        stream.config.historical_destinations = historical_destinations;
        revision.clone_into(&mut stream.config.policy_revision);
        stream.health.protected_sequence =
            chain.last().ok_or(FleetError::Capture)?.boundary_sequence().get();
        stream.health.providers =
            stream.config.destinations.iter().map(|(provider, _)| (*provider, true)).collect();
        stream.health.protected = true;
        stream.verified_at = Some(Instant::now());
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
                !retained_destinations
                    .iter()
                    .any(|destination| current.identity() == destination.identity())
            })
        {
            return Err(FleetError::Configuration);
        }
        stream.closing = true;
        stream.health.protected = false;
        std::fs::create_dir_all(&stream.config.staging).map_err(|_| FleetError::Capture)?;
        let retained = inventory_points(stream)?;
        let mut removed = 0;
        for destination in retained_destinations {
            let journal =
                stream.config.staging.join(format!("deleting-{}", destination.identity()));
            let mut pending =
                read_upload_journal(&journal, "delete-v1", inventory_point_limit(stream))?;
            // Include known captures whose interrupted upload never published
            // an index, and preserve these exact prefixes across delete retries.
            pending.extend(retained.iter().map(|point| format!("rp-{}", point.identity())));
            let points = destination
                .list_points(database, inventory_point_limit(stream))
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
        if let Some(mut stream) = self.streams.remove(database) {
            stream.closing = true;
            stream.paused = true;
            let StreamConfig {
                keys,
                destinations,
                historical_destinations,
                policy_revision,
                retention_days,
                staging,
                placement,
                ..
            } = stream.config;
            self.detached.insert(
                database.to_owned(),
                DetachedStream {
                    keys,
                    destinations,
                    historical_destinations,
                    policy_revision,
                    retention_days,
                    staging,
                    placement,
                    paused: stream.paused,
                },
            );
        } else if let Some(stream) = self.suspended.remove(database) {
            self.detached.insert(database.to_owned(), stream);
        }
    }

    /// Releases a paused target's engine while retaining its exact backup policy.
    /// The fleet lock serializes this with in-flight provider work.
    ///
    /// # Errors
    /// Returns [`FleetError`] when the target is unavailable or cannot be
    /// suspended safely.
    pub fn suspend_for_restore(&mut self, database: &str) -> Result<(), FleetError> {
        if self.suspended.contains_key(database) {
            return Ok(());
        }
        let Some(stream) = self.streams.get(database) else {
            return Ok(());
        };
        let engine = stream.config.engine.engine();
        let tenant = crate::types::TenantId::try_new(&engine.database_owner().tenant().to_text())
            .map_err(|_| FleetError::Configuration)?;
        let state =
            engine.lifecycle_state(&engine.scope_for(&tenant)).map_err(|_| FleetError::Capture)?;
        if !state.paused || state.access_revoked() || stream.closing {
            return Err(FleetError::Configuration);
        }
        let stream = self.streams.remove(database).ok_or(FleetError::Configuration)?;
        let StreamConfig {
            engine,
            keys,
            destinations,
            historical_destinations,
            policy_revision,
            retention_days,
            staging,
            placement,
        } = stream.config;
        drop(engine);
        self.suspended.insert(
            database.to_owned(),
            DetachedStream {
                keys,
                destinations,
                historical_destinations,
                policy_revision,
                retention_days,
                staging,
                placement,
                paused: stream.paused,
            },
        );
        Ok(())
    }

    /// Rebinds the retained policy to the reopened target after restore or rollback.
    ///
    /// # Errors
    /// Returns [`FleetError`] when the engine identity or retained policy is
    /// invalid, or attachment fails.
    pub fn resume_after_restore(
        &mut self,
        database: &str,
        engine: Arc<dyn BackupEngineHandle>,
        resume_capture: bool,
    ) -> Result<(), FleetError> {
        if engine.engine().database_id().to_string() != database {
            return Err(FleetError::Configuration);
        }
        let Some(policy) = self.suspended.remove(database) else {
            if resume_capture && self.streams.contains_key(database) {
                self.resume(database)?;
            }
            return Ok(());
        };
        let retained = policy.clone();
        let result = self.attach(StreamConfig {
            engine,
            keys: policy.keys,
            destinations: policy.destinations,
            historical_destinations: policy.historical_destinations,
            policy_revision: policy.policy_revision,
            retention_days: policy.retention_days,
            staging: policy.staging,
            placement: policy.placement,
        });
        if let Err(error) = result {
            self.suspended.insert(database.to_owned(), retained);
            return Err(error);
        }
        self.streams.get_mut(database).ok_or(FleetError::Configuration)?.paused =
            retained.paused && !resume_capture;
        Ok(())
    }

    /// Pauses capture for one stream while retaining its policy and artifacts.
    ///
    /// # Errors
    /// Returns [`FleetError`] when the database is unknown or closing.
    pub fn pause(&mut self, database: &str) -> Result<(), FleetError> {
        let stream = self.streams.get_mut(database).ok_or(FleetError::Configuration)?;
        if stream.closing {
            return Err(FleetError::Configuration);
        }
        stream.paused = true;
        Ok(())
    }

    /// Resumes capture for one retained stream without changing its identity.
    ///
    /// # Errors
    /// Returns [`FleetError`] when the database is unknown or closing.
    pub fn resume(&mut self, database: &str) -> Result<(), FleetError> {
        let stream = self.streams.get_mut(database).ok_or(FleetError::Configuration)?;
        if stream.closing {
            return Err(FleetError::Configuration);
        }
        stream.paused = false;
        stream.due = Instant::now();
        Ok(())
    }

    /// Runs each due stream once, continuing independent databases on failure.
    /// Capture/upload remains outside client acknowledgements. The configured
    /// ten-second interval is a scheduling target, never an RPO claim.
    pub async fn sync_once(&mut self) -> Vec<(String, FleetError)> {
        let mut failures = Vec::new();
        for (identity, stream) in &mut self.streams {
            if stream.closing || stream.paused || stream.due > Instant::now() {
                continue;
            }
            let owner = stream.config.engine.engine().database_owner();
            let Ok(tenant) = crate::types::TenantId::try_new(&owner.tenant().to_text()) else {
                failures.push((identity.clone(), FleetError::Capture));
                continue;
            };
            let Ok(lifecycle) = stream
                .config
                .engine
                .engine()
                .lifecycle_state(&stream.config.engine.engine().scope_for(&tenant))
            else {
                failures.push((identity.clone(), FleetError::Capture));
                continue;
            };
            if lifecycle.access_revoked() {
                failures.push((identity.clone(), FleetError::Configuration));
                continue;
            }
            if lifecycle.paused {
                stream.paused = true;
                continue;
            }
            let source = stream.config.engine.engine().journaled_high_water();
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
            let metrics = stream.config.engine.engine().metrics();
            metrics.record_backup(
                BackupMode::Continuous,
                result.as_ref().map_or(TaskOutcome::Error, |_| TaskOutcome::Success),
            );
            if result.is_ok() {
                metrics.set_backup_last_success_age(BackupMode::Continuous, Duration::ZERO);
            } else if let Some(verified_at) = stream.verified_at {
                metrics.set_backup_last_success_age(BackupMode::Continuous, verified_at.elapsed());
            }
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
        target: &crate::engine::EngineConfig,
        identity: &super::restore::RestoreIdentity,
    ) -> Result<super::RecoveryReceipt, FleetError> {
        self.restore_selected(database, provider, point, None, target, identity).await
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
        target: &crate::engine::EngineConfig,
        identity: &super::restore::RestoreIdentity,
    ) -> Result<super::RecoveryReceipt, FleetError> {
        self.restore_selected(database, provider, None, Some(at_unix_millis), target, identity)
            .await
    }

    async fn restore_selected(
        &mut self,
        database: &str,
        provider: Provider,
        point: Option<&str>,
        at: Option<u64>,
        target: &crate::engine::EngineConfig,
        identity: &super::restore::RestoreIdentity,
    ) -> Result<super::RecoveryReceipt, FleetError> {
        let stream = self.streams.get(database).ok_or(FleetError::Configuration)?;
        let owner = target.owner().ok_or(FleetError::Configuration)?;
        let expected_selection = match (point, at) {
            (Some(point), None) => point.to_owned(),
            (None, Some(at)) => at.to_string(),
            (None, None) => "latest".into(),
            (Some(_), Some(_)) => return Err(FleetError::Configuration),
        };
        if !target.has_encrypted_owner()
            || !stream.config.placement.allows(owner.region())
            || identity.source_database_id != database
            || identity.target_database_id != owner.database().to_text()
            || identity.provider != format!("{provider:?}").to_lowercase()
            || identity.selection != expected_selection
        {
            return Err(FleetError::Configuration);
        }
        let destination = stream
            .config
            .destinations
            .iter()
            .find(|(kind, _)| *kind == provider)
            .map(|(_, destination)| destination)
            .ok_or(FleetError::Configuration)?;
        let points = remote::remote_points(database, stream, destination).await?;
        let chain = if let Some(at) = at {
            super::restore::resolve_timestamp_points_chain(&points, at)
        } else {
            let selection = point.map_or(super::restore::Selection::Latest, |identity| {
                super::restore::Selection::Point { identity: identity.to_owned() }
            });
            super::restore::resolve_encrypted_points_chain(&points, &selection)
        }
        .map_err(|_| FleetError::Capture)?;
        let staging =
            stream.config.staging.join(format!("restore-{}", crate::identity::generate()));
        std::fs::create_dir(&staging).map_err(|_| FleetError::Capture)?;
        let result = async {
            for manifest in &chain {
                let name = format!("rp-{}", manifest.identity());
                let prefix = format!("{database}/{name}");
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
                return super::RestorePlan::restore_at_unix_millis_encrypted_with_config(
                    at,
                    &staging,
                    target,
                    reader.as_ref(),
                    identity,
                )
                .map_err(|_| FleetError::Capture);
            }
            match point {
                Some(point) => super::RestorePlan::restore_recovery_point_encrypted_with_config(
                    point,
                    &staging,
                    target,
                    reader.as_ref(),
                    identity,
                ),
                None => super::RestorePlan::restore_latest_encrypted_with_config(
                    &staging,
                    target,
                    reader.as_ref(),
                    identity,
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
        let source = stream
            .config
            .engine
            .engine()
            .journaled_high_water()
            .map_err(|_| FleetError::Capture)?;
        let mut observed = stream.health.clone();
        observed.source_sequence = source.unwrap_or_default().get();
        let providers_verified = !stream.config.destinations.is_empty()
            && observed.providers.len() == stream.config.destinations.len()
            && stream.config.destinations.iter().all(|(provider, _)| {
                observed.providers.iter().filter(|(actual, ok)| actual == provider && *ok).count()
                    == 1
            });
        observed.protected = observed.protected
            && stream.verified_at.is_some_and(|at| at.elapsed() < Duration::from_secs(60))
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
                u8::from(
                    stream.health.protected
                        && stream
                            .verified_at
                            .is_some_and(|at| at.elapsed() < Duration::from_secs(60))
                )
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
    persist_inventory(stream, limits)?;
    if stream.retention_due.is_none_or(|due| due <= Instant::now()) {
        enforce_retention(identity, stream, limits).await?;
        stream.retention_due = Some(Instant::now() + Duration::from_secs(3600));
    }
    let pending = point_paths(&stream.config.staging, limits.points_per_stream)?;
    if !pending.is_empty() && has_pending_uploads(stream, &pending, limits)? {
        // Drain the durable transfer queue before capturing another root. An
        // outage cannot accumulate repeated full snapshots in the reserve.
        if upload_destinations(identity, stream, &pending, limits).await? {
            return Err(FleetError::Upload);
        }
        evict_retired_local_chains(stream, limits)?;
    }
    if let Some((base, clock)) = rotation::read(stream)? {
        let staged = super::restore::discover_encrypted_points(
            &stream.config.staging,
            stream.config.keys.as_ref(),
        )
        .map_err(|_| FleetError::Capture)?;
        if staged.iter().any(|point| {
            point.base_sequence() == Some(base) && point.captured_unix_millis() > clock
        }) {
            let pending = point_paths(&stream.config.staging, limits.points_per_stream)?;
            if upload_destinations(identity, stream, &pending, limits).await? {
                return Err(FleetError::Upload);
            }
            rotation::finish(stream)?;
            evict_retired_local_chains(stream, limits)?;
        }
    }
    let points = point_paths(&stream.config.staging, limits.points_per_stream)?;
    let bytes = staged_bytes(&stream.config.staging)?;
    let reader: Arc<dyn BackupReader + Send + Sync> = stream.config.keys.clone();
    let mut chain = if points.is_empty() {
        Vec::new()
    } else {
        super::verify_latest_chain_encrypted(&stream.config.staging, reader.as_ref())
            .map_err(|_| FleetError::Capture)?
    };
    let rotation = rotation::read(stream)?;
    if let Some((base, clock)) = rotation {
        let snapshots =
            super::restore::discover_encrypted_points(&stream.config.staging, reader.as_ref())
                .map_err(|_| FleetError::Capture)?;
        if let Some(snapshot) = snapshots.into_iter().find(|point| {
            point.kind() == super::BackupKind::Snapshot
                && point.boundary_sequence() == base
                && point.captured_unix_millis() == clock
        }) {
            super::verify_members(&stream.config.staging, snapshot.identity(), snapshot.members())
                .map_err(|_| FleetError::Capture)?;
            chain = vec![snapshot];
        }
    }
    let active_bytes = chain.iter().try_fold(0_u64, |total, point| {
        staged_bytes(&stream.config.staging.join(format!("rp-{}", point.identity())))
            .map(|bytes| total.saturating_add(bytes))
    })?;
    let base = chain.last().map(super::BackupManifest::boundary_sequence);
    let high_water = stream
        .config
        .engine
        .engine()
        .journaled_high_water()
        .map_err(|_| FleetError::Capture)?
        .unwrap_or_default();
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|_| FleetError::Capture)?
        .as_millis();
    let now = u64::try_from(now).map_err(|_| FleetError::Capture)?;
    // Reserve space for a replacement root before the bounded active chain
    // fills. Provider retention is independent from this local rotation.
    let root_due = chain
        .first()
        .is_none_or(|root| now.saturating_sub(root.captured_unix_millis()) >= 86_400_000)
        || chain.len() >= (limits.points_per_stream / 2).max(1)
        || active_bytes >= limits.staging_bytes_per_stream / 3;
    let pending_snapshot =
        chain.last().is_some_and(|point| point.kind() == super::BackupKind::Snapshot);
    let root_due = (root_due || rotation.is_some()) && !pending_snapshot;
    let heartbeat_due = chain
        .last()
        .is_some_and(|point| now.saturating_sub(point.recoverable_through_unix_millis()) >= 10_000);
    let additional = if root_due && base.is_some() { 2 } else { 1 };
    if (base != Some(high_water) || root_due || pending_snapshot || heartbeat_due)
        && !inventory::has_capacity(stream, limits, additional)?
    {
        // At capacity retention still reads an admissible inventory and can
        // free expired chains. Never publish a point that makes it unreadable.
        enforce_retention(identity, stream, limits).await?;
        if !inventory::has_capacity(stream, limits, additional)? {
            return Err(FleetError::StagingFull);
        }
    }
    let mut captured_boundary = base.unwrap_or_default();
    let mut capture_refusal = None;
    if base != Some(high_water) || root_due || pending_snapshot || heartbeat_due {
        if points.len() >= limits.points_per_stream
            || bytes.saturating_add(2 * 1024 * 1024) >= limits.staging_bytes_per_stream
        {
            capture_refusal = Some(FleetError::StagingFull);
        } else {
            let mut current_bytes = active_bytes;
            // Rotate at the last archived boundary, then extend it. Taking a
            // full root at the current high-water would omit intervening
            // journal transactions from timestamp recovery.
            if root_due && let Some(base) = base {
                let clock = rotation.map_or_else(
                    || now.max(chain.last().map_or(0, super::BackupManifest::captured_unix_millis)),
                    |(_, clock)| clock,
                );
                if rotation.is_none() {
                    rotation::begin(stream, base, clock)?;
                }
                let plan = capture_plan(
                    stream,
                    limits,
                    current_bytes,
                    bytes,
                    Some(base),
                    true,
                    reader.clone(),
                )?
                .with_capture_unix_millis(clock);
                let engine = Arc::clone(&stream.config.engine);
                let staging = stream.config.staging.clone();
                let result = tokio::task::spawn_blocking(move || {
                    run_backup(engine.engine(), plan, &staging)
                })
                .await
                .map_err(|_| FleetError::Capture)?;
                let replacement = result.map_err(|_| FleetError::Capture)?;
                current_bytes = staged_bytes(
                    &stream
                        .config
                        .staging
                        .join(format!("rp-{}", replacement.manifest().identity())),
                )?;
                persist_inventory(stream, limits)?;
                let pending = point_paths(&stream.config.staging, limits.points_per_stream)?;
                if upload_destinations(identity, stream, &pending, limits).await? {
                    return Err(FleetError::Upload);
                }
            }
            let bridge_clock = if let Some((_, clock)) = rotation::read(stream)? {
                // Distinguish the bridge durably from pre-intent no-op points
                // even when two captures fall within one wall-clock millisecond.
                let captured = loop {
                    tokio::time::sleep(Duration::from_millis(1)).await;
                    let captured = std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map_err(|_| FleetError::Capture)?
                        .as_millis();
                    let captured = u64::try_from(captured).map_err(|_| FleetError::Capture)?;
                    if captured > clock {
                        break captured;
                    }
                };
                Some(captured)
            } else {
                None
            };
            let mut plan = capture_plan(
                stream,
                limits,
                current_bytes,
                staged_bytes(&stream.config.staging)?,
                base,
                false,
                reader.clone(),
            )?;
            if let Some(clock) = bridge_clock {
                plan = plan.with_capture_unix_millis(clock);
            }
            let engine = Arc::clone(&stream.config.engine);
            let staging = stream.config.staging.clone();
            let staging_for_backup = staging.clone();
            let result = tokio::task::spawn_blocking(move || {
                run_backup(engine.engine(), plan, &staging_for_backup)
            })
            .await
            .map_err(|_| FleetError::Capture)?;
            match result {
                Ok(backup) => captured_boundary = backup.manifest().boundary_sequence(),
                Err(_) => capture_refusal = Some(FleetError::Capture),
            }
        }
    }
    let points = point_paths(&stream.config.staging, limits.points_per_stream)?;
    let failed = upload_destinations(identity, stream, &points, limits).await?;
    if failed {
        return Err(FleetError::Upload);
    }
    // Advance only after complete read-back verification on EVERY destination.
    stream
        .config
        .engine
        .engine()
        .advance_backup_frontier(captured_boundary)
        .map_err(|_| FleetError::Capture)?;
    persist_inventory(stream, limits)?;
    if capture_refusal.is_none() {
        rotation::finish(stream)?;
        evict_retired_local_chains(stream, limits)?;
    }
    stream.health.protected_sequence = captured_boundary.get();
    stream.health.protected = capture_refusal.is_none();
    capture_refusal.map_or(Ok(()), Err)
}

fn capture_plan(
    stream: &Stream,
    limits: FleetLimits,
    bytes: u64,
    total_bytes: u64,
    base: Option<StorageSequence>,
    rotate: bool,
    reader: Arc<dyn BackupReader + Send + Sync>,
) -> Result<BackupPlan, FleetError> {
    let lease = stream.config.keys.lease().map_err(|_| FleetError::Capture)?;
    let encryption = BackupEncryption::from_database_key_lease(
        &lease,
        stream.config.engine.engine().database_owner().region(),
        0,
    )
    .map_err(|_| FleetError::Capture)?;
    let plan = match (base, rotate) {
        (Some(base), true) => BackupPlan::snapshot_at(base, encryption),
        (Some(base), false) => BackupPlan::incremental_since(base, encryption),
        (None, _) => BackupPlan::full(encryption),
    };
    let archive_limit = (limits.staging_bytes_per_stream / 2)
        .saturating_sub(if rotate || base.is_none() { 0 } else { bytes })
        .saturating_sub(2 * 1024 * 1024)
        .min(
            limits
                .staging_bytes_per_stream
                .saturating_sub(total_bytes)
                .saturating_sub(2 * 1024 * 1024),
        );
    Ok(plan
        .with_retention_days(stream.config.retention_days)
        .map_err(|_| FleetError::Configuration)?
        .with_reader(reader)
        .with_placement_lock(stream.config.placement.clone())
        .with_archive_byte_limit(archive_limit))
}

fn has_pending_uploads(
    stream: &Stream,
    points: &[PathBuf],
    limits: FleetLimits,
) -> Result<bool, FleetError> {
    for (provider, _) in &stream.config.destinations {
        let journal = stream.config.staging.join(match provider {
            Provider::Oci => "uploaded-oci",
            Provider::Tigris => "uploaded-tigris",
        });
        let acknowledged = read_upload_journal(
            &journal,
            &stream.config.policy_revision,
            limits.points_per_stream,
        )?;
        if points.iter().any(|point| {
            point
                .file_name()
                .and_then(|name| name.to_str())
                .is_none_or(|name| !acknowledged.contains(name))
        }) {
            return Ok(true);
        }
    }
    Ok(false)
}

async fn upload_destinations(
    identity: &str,
    stream: &mut Stream,
    points: &[PathBuf],
    limits: FleetLimits,
) -> Result<bool, FleetError> {
    let audit_due = stream.verified_at.is_none_or(|at| at.elapsed() >= Duration::from_secs(60));
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
        for point in points {
            let name =
                point.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?;
            if acknowledged.contains(name) {
                if stream.verified_at.is_some_and(|at| at.elapsed() < Duration::from_secs(60)) {
                    continue;
                }
                if destination.verify_point(point, &format!("{identity}/{name}")).await.is_ok() {
                    continue;
                }
                // Report the failed readback now. The next tick repairs this
                // exact sealed point instead of trusting the stale receipt.
                acknowledged.remove(name);
                persist_upload_journal(&journal, &stream.config.policy_revision, &acknowledged)?;
                succeeded = false;
                break;
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
    if !failed && audit_due {
        stream.verified_at = Some(Instant::now());
    }
    Ok(failed)
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
    let points = inventory_points(stream)?;
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
    let root_name = format!("rp-{}", root.identity());
    let manifest_path =
        inventory_path(stream).join(&root_name).join(super::manifest::ENCRYPTED_MANIFEST_FILE);
    for (_, destination) in &stream.config.destinations {
        destination
            .verify_manifest(&format!("{identity}/{root_name}"), &manifest_path, root)
            .await
            .map_err(|_| FleetError::Upload)?;
    }
    // Authentication plus every member digest was verified before admission to
    // deletion. Exclusive fleet execution excludes concurrent local restores.
    for point in &points {
        if point.retain_until_unix_millis() > now
            || point.captured_unix_millis() >= root.captured_unix_millis()
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
        for directory in [stream.config.staging.join(&name), inventory_path(stream).join(&name)] {
            inventory::remove_point(&directory)?;
        }
        super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)?;
        super::sync_directory(&inventory_path(stream)).map_err(|_| FleetError::Capture)?;
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
        if entry.file_name() == ".inventory" {
            continue;
        }
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

fn destination_history_key(provider: Provider, destination: &ObjectDestination) -> String {
    let provider = match provider {
        Provider::Oci => "oci",
        Provider::Tigris => "tigris",
    };
    format!("{provider}:{}", destination.identity())
}

fn read_policy_history(path: &Path) -> Result<BTreeSet<String>, FleetError> {
    use std::io::Read;
    let file = match std::fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(BTreeSet::new());
        }
        Err(_) => return Err(FleetError::Capture),
    };
    let mut body = String::new();
    file.take(1025).read_to_string(&mut body).map_err(|_| FleetError::Capture)?;
    if body.len() > 1024 {
        return Err(FleetError::Capture);
    }
    let mut lines = body.lines();
    if lines.next().is_none() {
        return Err(FleetError::Capture);
    }
    let mut destinations = BTreeSet::new();
    for line in lines {
        let (provider, identity) = line.split_once(':').ok_or(FleetError::Capture)?;
        match provider {
            "oci" | "tigris" => {}
            _ => return Err(FleetError::Capture),
        }
        if identity.is_empty() || identity.len() > 128 || identity.chars().any(char::is_control) {
            return Err(FleetError::Capture);
        }
        destinations.insert(line.to_owned());
    }
    Ok(destinations)
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
    use crate::backup::BackupEnvelopeParticipant;
    use crate::backup::tests::{committed, location, open_engine, put, text};
    use crate::encryption::{
        DatabaseKey, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
    };
    use crate::types::LocalDurability;
    use object_store::ObjectStore;

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
                Duration::from_mins(1),
            )
            .unwrap(),
        )))
    }

    #[test]
    fn policy_history_binds_the_persisted_destination_identity() {
        let path = location("backup-policy-history-identity");
        std::fs::create_dir_all(&path).unwrap();
        std::fs::write(path.join("policy-active"), "revision\noci:destination-identity\n").unwrap();

        assert_eq!(
            read_policy_history(&path.join("policy-active")).unwrap(),
            BTreeSet::from(["oci:destination-identity".to_owned()])
        );
        assert!(read_policy_history(&path.join("missing")).unwrap().is_empty());
        std::fs::write(path.join("policy-active"), "revision\noci\n").unwrap();
        assert_eq!(read_policy_history(&path.join("policy-active")), Err(FleetError::Capture));
    }

    #[tokio::test]
    async fn envelope_catalog_is_not_published_when_a_provider_fails() {
        use object_store::ObjectStore;

        let (_, engine) = open_engine("fleet-envelope-atomicity");
        let engine = Arc::new(engine);
        let database = engine.database_id().to_string();
        let staging = location("fleet-envelope-atomicity-staging");
        let good_store = Arc::new(object_store::memory::InMemory::new());
        let good = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            good_store,
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let good_tigris = Arc::new(ObjectDestination::for_test(
            Provider::Tigris,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let mut fleet = NativeBackupFleet::new(FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 4,
        })
        .unwrap();
        fleet
            .attach(StreamConfig {
                engine: Arc::clone(&engine) as Arc<dyn BackupEngineHandle>,
                keys: keys(&engine),
                destinations: vec![
                    (Provider::Oci, Arc::clone(&good)),
                    (Provider::Tigris, Arc::clone(&good_tigris)),
                ],
                historical_destinations: Vec::new(),
                policy_revision: "envelope-atomicity".into(),
                retention_days: 1,
                staging: staging.clone(),
                placement: PlacementLock::br_only(),
            })
            .unwrap();

        let manifest = fleet.capture_snapshot(&database).await.unwrap();
        let incarnation = u128::from_be_bytes(engine.database_id().as_bytes());
        let envelope = BackupEnvelope::assemble(
            database.clone(),
            incarnation,
            1,
            vec![BackupEnvelopeParticipant {
                participant_id: "shard-0000000000000001".into(),
                database_id: database.clone(),
                database_incarnation: incarnation,
                route_generation: 1,
                boundary: manifest.boundary_sequence().get(),
                manifest: manifest.clone(),
            }],
        )
        .unwrap();

        let mismatched = BackupEnvelope::assemble(
            "another-database".into(),
            incarnation,
            1,
            vec![BackupEnvelopeParticipant {
                participant_id: "shard-0000000000000001".into(),
                database_id: database.clone(),
                database_incarnation: incarnation,
                route_generation: 1,
                boundary: manifest.boundary_sequence().get(),
                manifest: manifest.clone(),
            }],
        )
        .unwrap();
        assert_eq!(fleet.publish_envelope(&mismatched).await, Err(FleetError::Configuration));

        let broken_path = location("fleet-envelope-atomicity-provider");
        std::fs::create_dir_all(&broken_path).unwrap();
        let unavailable =
            object_store::local::LocalFileSystem::new_with_prefix(&broken_path).unwrap();
        std::fs::remove_dir(&broken_path).unwrap();
        std::fs::write(&broken_path, b"provider unavailable").unwrap();
        fleet.streams.get_mut(&database).unwrap().config.destinations = vec![
            (Provider::Oci, Arc::clone(&good)),
            (
                Provider::Tigris,
                Arc::new(ObjectDestination::for_test(
                    Provider::Tigris,
                    Arc::new(unavailable) as Arc<dyn ObjectStore>,
                    4 * 1024 * 1024,
                    8 * 1024 * 1024,
                )),
            ),
        ];

        let name = format!("env-{}", envelope.digest());
        let published = staging.join("envelopes").join(&name);
        assert_eq!(fleet.publish_envelope(&envelope).await, Err(FleetError::Upload));
        assert!(!published.exists());
        assert!(staging.join("envelopes").join(format!(".{name}.staging")).exists());

        fleet.streams.get_mut(&database).unwrap().config.destinations =
            vec![(Provider::Oci, good), (Provider::Tigris, good_tigris)];
        assert_eq!(fleet.publish_envelope(&envelope).await.unwrap(), published);
        assert!(published.join("envelope.tbv1").exists());
        let retained = fleet.retained_envelopes(&database).unwrap();
        assert_eq!(retained, vec![envelope]);

        std::fs::remove_dir_all(staging).unwrap();
        std::fs::remove_file(broken_path).unwrap();
    }

    #[tokio::test]
    async fn idle_heartbeat_extends_remote_timestamp_coverage_without_billing() {
        use crate::usage::{UsagePageRequest, UsagePlacement};
        let (_, engine) = open_engine("fleet-idle-source");
        let owner = engine.database_owner().clone();
        let tenant = crate::types::TenantId::try_new(&owner.tenant().to_text()).unwrap();
        engine
            .configure_usage_placement(
                UsagePlacement::try_new(
                    tenant.clone(),
                    &owner.project().to_text(),
                    &owner.database().to_text(),
                    "partition",
                    owner.region(),
                    vec![owner.region()],
                    "idle-usage-policy",
                )
                .unwrap(),
            )
            .unwrap();
        committed(put(
            &engine,
            &owner.tenant().to_text(),
            "idle-write",
            "idle-doc",
            "users",
            text("retained"),
            LocalDurability::Synced,
        ));
        let engine = Arc::new(engine);
        let key_provider = keys(&engine);
        let staging = location("fleet-idle-staging");
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let config = || StreamConfig {
            engine: engine.clone(),
            keys: key_provider.clone(),
            destinations: vec![(Provider::Oci, destination.clone())],
            historical_destinations: Vec::new(),
            policy_revision: "idle-policy".into(),
            retention_days: 1,
            staging: staging.clone(),
            placement: PlacementLock::br_only(),
        };
        let limits = FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 4,
        };
        let identity = engine.database_id().to_string();
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        let first = fleet.capture_snapshot(&identity).await.unwrap();
        let before = engine
            .usage_page(
                &engine.scope_for(&tenant),
                UsagePageRequest::try_new(None, 100, None).unwrap(),
            )
            .unwrap();
        assert_eq!(before.facts().len(), 1);
        let sequence = engine.journaled_high_water().unwrap();
        // Immediate repeat verifies the same boundary without publishing an
        // unnecessary heartbeat. A due scheduled tick seals idle coverage.
        fleet.capture_snapshot(&identity).await.unwrap();
        assert_eq!(fleet.retained_points(&identity).unwrap().len(), 1);
        tokio::time::sleep(Duration::from_millis(10_010)).await;
        assert!(fleet.sync_once().await.is_empty());
        let metrics = engine.metrics().snapshot();
        assert_eq!(metrics.backup_runs(), &[(BackupMode::Continuous, TaskOutcome::Success, 1)]);
        assert_eq!(metrics.backup_last_success_age().len(), 1);
        assert_eq!(metrics.backup_last_success_age()[0].0, BackupMode::Continuous);
        assert!(metrics.backup_last_success_age()[0].1 < 1.0);
        let latest = fleet.retained_points(&identity).unwrap().pop().unwrap();
        assert_eq!(latest.boundary_sequence(), first.boundary_sequence());
        assert!(latest.recoverable_through_unix_millis() > first.recoverable_through_unix_millis());
        assert_eq!(engine.journaled_high_water().unwrap(), sequence);
        let after = engine
            .usage_page(
                &engine.scope_for(&tenant),
                UsagePageRequest::try_new(None, 100, None).unwrap(),
            )
            .unwrap();
        assert_eq!(after.facts(), before.facts());
        let at = latest.recoverable_through_unix_millis();
        drop(fleet);
        // Remove all cached archives to make restore rely on the provider's
        // independent manifest discovery and selected-chain download.
        for point in point_paths(&staging, 4).unwrap() {
            std::fs::remove_dir_all(point).unwrap();
        }
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        let (target_owner, target) = restore_config("fleet-idle-target", &owner);
        let receipt = fleet
            .restore_at_from_provider(
                &identity,
                Provider::Oci,
                at,
                &target,
                &crate::backup::restore::RestoreIdentity {
                    operation_id: "idle-pitr".into(),
                    source_database_id: identity.clone(),
                    target_database_id: target_owner.database().to_string(),
                    provider: "oci".into(),
                    selection: at.to_string(),
                    expected_manifest_digest: latest.identity().into(),
                    expected_journal_boundary: latest.boundary_sequence().get(),
                    restore_epoch: 1,
                },
            )
            .await
            .unwrap();
        assert_eq!(receipt.restored_documents(), 1);
    }

    #[tokio::test]
    async fn outage_during_rotation_retries_without_exceeding_disk_bound() {
        let (_, engine) = open_engine("fleet-rotation-outage");
        let engine = Arc::new(engine);
        let staging = location("fleet-rotation-outage-staging");
        let provider_path = location("fleet-rotation-provider");
        let parked_path = provider_path.with_extension("parked");
        std::fs::create_dir_all(&provider_path).unwrap();
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(
                object_store::local::LocalFileSystem::new_with_prefix(&provider_path).unwrap(),
            ),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let key_provider = keys(&engine);
        let config = || StreamConfig {
            engine: engine.clone(),
            keys: key_provider.clone(),
            destinations: vec![(Provider::Oci, destination.clone())],
            historical_destinations: Vec::new(),
            policy_revision: "policy-1".into(),
            retention_days: 1,
            staging: staging.clone(),
            placement: PlacementLock::br_only(),
        };
        let limits = FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 4,
        };
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        let identity = engine.database_id().to_string();
        for tick in 0..2 {
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
                &format!("outage-op-{tick}"),
                &format!("outage-doc-{tick}"),
                "users",
                text(&"payload".repeat(32_768)),
                LocalDurability::Synced,
            ));
            fleet.capture_snapshot(&identity).await.unwrap();
        }
        std::fs::rename(&provider_path, &parked_path).unwrap();
        std::fs::write(&provider_path, b"provider unavailable").unwrap();
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "outage-op-later",
            "outage-doc-later",
            "users",
            text("later"),
            LocalDurability::Synced,
        ));
        assert_eq!(fleet.capture_snapshot(&identity).await.unwrap_err(), FleetError::Upload);
        let bytes = staged_bytes(&staging).unwrap();
        assert!(bytes > 100_000);
        assert!(bytes < limits.staging_bytes_per_stream);
        drop(fleet);
        fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        for _ in 0..5 {
            assert_eq!(fleet.capture_snapshot(&identity).await.unwrap_err(), FleetError::Upload);
            assert!(staged_bytes(&staging).unwrap() <= bytes);
            assert_eq!(point_paths(&staging, 4).unwrap().len(), 3);
            assert!(!fleet.observe_protection(&identity).unwrap().protected);
            assert!(staging.join("rotation-pending").exists());
        }
        std::fs::remove_file(&provider_path).unwrap();
        std::fs::rename(&parked_path, &provider_path).unwrap();
        fleet.capture_snapshot(&identity).await.unwrap();
        assert!(fleet.health(&identity).unwrap().protected);
        assert_eq!(point_paths(&staging, 4).unwrap().len(), 2);
        assert!(staged_bytes(&staging).unwrap() < limits.staging_bytes_per_stream / 2);
    }

    #[tokio::test]
    async fn rotation_preserves_timestamp_between_post_base_writes() {
        let (_, engine) = open_engine("fleet-rotation-time");
        let engine = Arc::new(engine);
        let staging = location("fleet-rotation-time-staging");
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let mut fleet = NativeBackupFleet::new(FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 4,
        })
        .unwrap();
        fleet
            .attach(StreamConfig {
                engine: engine.clone(),
                keys: keys(&engine),
                destinations: vec![(Provider::Oci, destination)],
                historical_destinations: Vec::new(),
                policy_revision: "policy-1".into(),
                retention_days: 1,
                staging: staging.clone(),
                placement: PlacementLock::br_only(),
            })
            .unwrap();
        let identity = engine.database_id().to_string();
        for tick in 0..2 {
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
                &format!("time-op-{tick}"),
                &format!("time-doc-{tick}"),
                "users",
                text("before"),
                LocalDurability::Synced,
            ));
            fleet.capture_snapshot(&identity).await.unwrap();
        }
        std::thread::sleep(Duration::from_millis(2));
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "time-op-a",
            "time-doc-a",
            "users",
            text("included"),
            LocalDurability::Synced,
        ));
        let at = engine
            .backup_boundary_unix_millis(engine.journaled_high_water().unwrap().unwrap())
            .unwrap();
        std::thread::sleep(Duration::from_millis(2));
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "time-op-b",
            "time-doc-b",
            "users",
            text("excluded"),
            LocalDurability::Synced,
        ));
        let latest = fleet.capture_snapshot(&identity).await.unwrap();
        assert_eq!(point_paths(&staging, 4).unwrap().len(), 2);
        let (target_owner, target) =
            restore_config("fleet-rotation-time-target", engine.database_owner());
        let receipt = fleet
            .restore_at_from_provider(
                &identity,
                Provider::Oci,
                at,
                &target,
                &crate::backup::restore::RestoreIdentity {
                    operation_id: "rotation-time-restore".into(),
                    source_database_id: identity.clone(),
                    target_database_id: target_owner.database().to_string(),
                    provider: "oci".into(),
                    selection: at.to_string(),
                    expected_manifest_digest: latest.identity().into(),
                    expected_journal_boundary: latest.boundary_sequence().get(),
                    restore_epoch: 1,
                },
            )
            .await
            .unwrap();
        assert_eq!(receipt.restored_documents(), 3);
    }

    #[tokio::test]
    async fn sustained_capture_rotates_cache_without_shortening_provider_retention() {
        let (_, engine) = open_engine("fleet-bounded-source");
        let engine = Arc::new(engine);
        let key_provider = keys(&engine);
        let staging = location("fleet-bounded-staging");
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let config = || StreamConfig {
            engine: engine.clone(),
            keys: key_provider.clone(),
            destinations: vec![(Provider::Oci, destination.clone())],
            historical_destinations: Vec::new(),
            policy_revision: "policy-1".into(),
            retention_days: 1,
            staging: staging.clone(),
            placement: PlacementLock::br_only(),
        };
        let limits = FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 64,
        };
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        let identity = engine.database_id().to_string();
        for tick in 0..70 {
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
                &format!("bounded-op-{tick}"),
                "bounded-doc",
                "users",
                text(&format!("value-{tick}")),
                LocalDurability::Synced,
            ));
            fleet.streams.get_mut(&identity).unwrap().due = Instant::now();
            let failures = fleet.sync_once().await;
            assert!(failures.is_empty(), "tick {tick}: {failures:?}");
            assert!(fleet.observe_protection(&identity).unwrap().protected);
            assert!(point_paths(&staging, 64).unwrap().len() <= 32);
            assert!(staged_bytes(&staging).unwrap() < limits.staging_bytes_per_stream / 2);
            if tick == 40 {
                // Model a crash after atomic eviction rename and partial
                // unlink: restart discovery must ignore and reclaim it.
                for directory in [&staging, &staging.join(".inventory")] {
                    let tombstone = directory.join(format!(".evict-rp-{}", "a".repeat(64)));
                    std::fs::create_dir_all(&tombstone).unwrap();
                    std::fs::write(tombstone.join("remaining-artifact"), b"partial eviction")
                        .unwrap();
                }
                for name in [
                    ".staging-123-0".to_owned(),
                    format!("restore-{}", crate::identity::generate()),
                    format!("migration-{}", crate::identity::generate()),
                ] {
                    let pending = staging.join(name);
                    std::fs::create_dir(&pending).unwrap();
                    std::fs::write(pending.join("partial-artifact"), vec![0_u8; 1024 * 1024])
                        .unwrap();
                }
                let pending_manifest =
                    staging.join(".inventory").join(format!(".pending-{}", "b".repeat(64)));
                std::fs::create_dir(&pending_manifest).unwrap();
                std::fs::write(pending_manifest.join("partial-envelope"), b"unpublished").unwrap();
                drop(fleet);
                fleet = NativeBackupFleet::new(limits).unwrap();
                fleet.attach(config()).unwrap();
                assert!(!fleet.observe_protection(&identity).unwrap().protected);
            }
        }
        for directory in [&staging, &staging.join(".inventory")] {
            assert!(!directory.join(format!(".evict-rp-{}", "a".repeat(64))).exists());
        }
        assert!(std::fs::read_dir(&staging).unwrap().all(|entry| {
            let name = entry.unwrap().file_name().to_string_lossy().into_owned();
            !name.starts_with(".staging-")
                && !name.starts_with("restore-")
                && !name.starts_with("migration-")
        }));
        assert!(!staging.join(".inventory").join(format!(".pending-{}", "b".repeat(64))).exists());
        let retained = fleet.retained_points(&identity).unwrap().len();
        // Metadata admission fails before publishing another archive, then
        // resumes after capacity is restored without losing retained points.
        let original_inventory_limit = fleet.limits.inventory_bytes_per_stream;
        fleet.limits.inventory_bytes_per_stream =
            staged_bytes(&staging.join(".inventory")).unwrap();
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "inventory-budget-op",
            "bounded-doc",
            "users",
            text("after metadata pressure"),
            LocalDurability::Synced,
        ));
        assert_eq!(fleet.capture_snapshot(&identity).await.unwrap_err(), FleetError::StagingFull);
        assert_eq!(fleet.retained_points(&identity).unwrap().len(), retained);
        fleet.limits.inventory_bytes_per_stream = original_inventory_limit;
        fleet.capture_snapshot(&identity).await.unwrap();
        let retained = fleet.retained_points(&identity).unwrap().len();
        assert!(retained >= 71);
        assert_eq!(destination.list_points(&identity, 128).await.unwrap().len(), retained);
        let roots = fleet
            .retained_points(&identity)
            .unwrap()
            .into_iter()
            .filter(|point| point.base_sequence().is_none())
            .count();
        assert!(roots >= 3);
    }

    #[tokio::test]
    async fn acknowledged_provider_loss_is_detected_after_restart_and_repaired() {
        use object_store::ObjectStore;
        let (_, engine) = open_engine("fleet-readback-source");
        let engine = Arc::new(engine);
        let key_provider = keys(&engine);
        let staging = location("fleet-readback-staging");
        let store = Arc::new(object_store::memory::InMemory::new());
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            store.clone(),
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
                (Provider::Oci, destination.clone()),
                (Provider::Tigris, second.clone()),
            ],
            historical_destinations: Vec::new(),
            policy_revision: "policy-1".into(),
            retention_days: 1,
            staging: staging.clone(),
            placement: PlacementLock::br_only(),
        };
        let limits = FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 8,
        };
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "op-readback",
            "doc-readback",
            "users",
            text("one"),
            LocalDurability::Synced,
        ));
        assert!(fleet.sync_once().await.is_empty());
        let identity = engine.database_id().to_string();
        let point = fleet.retained_points(&identity).unwrap().pop().unwrap();
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "op-readback-2",
            "doc-readback-2",
            "users",
            text("two"),
            LocalDurability::Synced,
        ));
        let latest = fleet.capture_snapshot(&identity).await.unwrap();
        assert!(latest.base_sequence().is_some());
        assert_ne!(latest.identity(), point.identity());
        let prefix = format!("{identity}/rp-{}", point.identity());
        destination.delete_point(&prefix).await.unwrap();
        drop(fleet);
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        assert_eq!(fleet.sync_once().await, vec![(identity.clone(), FleetError::Upload)]);
        assert!(!fleet.observe_protection(&identity).unwrap().protected);
        assert_eq!(fleet.health(&identity).unwrap().protected_sequence, 0);
        assert_eq!(
            fleet.health(&identity).unwrap().providers,
            vec![(Provider::Oci, false), (Provider::Tigris, true)]
        );
        fleet.capture_snapshot(&identity).await.unwrap();
        assert!(fleet.health(&identity).unwrap().protected);
        let manifest = object_store::path::Path::from(format!(
            "{prefix}/{}",
            super::super::manifest::ENCRYPTED_MANIFEST_FILE
        ));
        store.put(&manifest, b"corrupt".to_vec().into()).await.unwrap();
        assert_eq!(fleet.capture_snapshot(&identity).await.unwrap_err(), FleetError::Upload);
        assert!(!fleet.observe_protection(&identity).unwrap().protected);
        fleet.capture_snapshot(&identity).await.unwrap();
        assert!(fleet.health(&identity).unwrap().protected);
    }

    #[tokio::test]
    async fn paused_lifecycle_is_restored_across_fleet_restart() {
        let (_, engine) = open_engine("fleet-pause-restart");
        let owner = engine.database_owner().clone();
        let tenant = crate::types::TenantId::try_new(&owner.tenant().to_text()).unwrap();
        let scope = engine.scope_for(&tenant);
        engine.lifecycle_pause(&scope, "pause-a").unwrap();
        let engine = Arc::new(engine);
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let config = || StreamConfig {
            engine: engine.clone(),
            keys: keys(&engine),
            destinations: vec![(Provider::Oci, destination.clone())],
            historical_destinations: Vec::new(),
            policy_revision: "policy-1".into(),
            retention_days: 1,
            staging: location("fleet-pause-restart-staging"),
            placement: PlacementLock::explicit([owner.region()]).unwrap(),
        };
        let mut fleet = NativeBackupFleet::new(FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 8,
        })
        .unwrap();
        fleet.attach(config()).unwrap();
        assert!(fleet.sync_once().await.is_empty());
        engine.lifecycle_resume(&scope, "resume-b").unwrap();
        let _ = engine.lifecycle_pause(&scope, "pause-a");
        assert!(!engine.lifecycle_state(&scope).unwrap().paused);
        fleet.resume(&owner.database().to_string()).unwrap();
    }

    fn restore_config(
        label: &str,
        owner: &crate::storage::DatabaseOwner,
    ) -> (crate::storage::DatabaseOwner, crate::engine::EngineConfig) {
        let target_owner = crate::storage::DatabaseOwner::try_new(
            owner.environment().as_str(),
            owner.tenant(),
            owner.project(),
            crate::identity::DatabaseUlid::generate(),
            owner.region(),
        )
        .unwrap();
        let config = crate::engine::EngineConfig::durable(location(label))
            .unwrap()
            .with_owner(target_owner.clone())
            .with_storage_keys(crate::engine::test_storage_keys(&target_owner, 71));
        (target_owner, config)
    }

    fn latest_restore_identity(
        fleet: &NativeBackupFleet,
        staging: &std::path::Path,
        identity: &str,
        target_owner: &crate::storage::DatabaseOwner,
        operation_id: String,
        provider: Provider,
    ) -> crate::backup::restore::RestoreIdentity {
        let point = crate::backup::restore::discover_encrypted_points(
            staging,
            fleet.streams.get(identity).unwrap().config.keys.as_ref(),
        )
        .unwrap()
        .last()
        .unwrap()
        .clone();
        crate::backup::restore::RestoreIdentity {
            operation_id,
            source_database_id: identity.to_owned(),
            target_database_id: target_owner.database().to_string(),
            provider: format!("{provider:?}").to_lowercase(),
            selection: "latest".into(),
            expected_manifest_digest: point.identity().to_owned(),
            expected_journal_boundary: point.boundary_sequence().get(),
            restore_epoch: 1,
        }
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
                inventory_bytes_per_stream: 16 * 1024 * 1024,
                points_per_stream: 8,
            })
            .unwrap();
            fleet
                .attach(StreamConfig {
                    keys: keys(&engine),
                    engine: engine.clone(),
                    destinations: vec![(Provider::Oci, good), (Provider::Tigris, bad)],
                    historical_destinations: Vec::new(),
                    policy_revision: "policy-1".into(),
                    retention_days: 1,
                    staging: staging.clone(),
                    placement: PlacementLock::br_only(),
                })
                .unwrap();
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
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
            let (target_owner, target_config) =
                restore_config("fleet-outage-restore", engine.database_owner());
            let receipt = fleet
                .restore_from_provider(
                    &identity,
                    Provider::Oci,
                    None,
                    &target_config,
                    &latest_restore_identity(
                        &fleet,
                        &staging,
                        &identity,
                        &target_owner,
                        "fleet-outage-restore-op".into(),
                        Provider::Oci,
                    ),
                )
                .await
                .unwrap();
            assert_eq!(receipt.restored_documents(), 1);
            // Even a full staging queue must retry the old pending point once
            // the provider recovers; newer unarchived work stays unprotected.
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
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

    async fn verify_fleet_policy_and_purge(
        fleet: &mut NativeBackupFleet,
        engine: &Engine,
        first: &Arc<ObjectDestination>,
        second: &Arc<ObjectDestination>,
    ) {
        let identity = engine.database_id().to_string();
        fleet
            .change_policy(&identity, "policy-2", vec![(Provider::Tigris, second.clone())])
            .await
            .unwrap();
        assert_eq!(fleet.policy(&identity).unwrap().0, "policy-2");
        assert_eq!(fleet.health(&identity).unwrap().providers, vec![(Provider::Tigris, true)]);
        assert_eq!(
            fleet
                .recovery_envelope_destinations(&identity)
                .unwrap()
                .into_iter()
                .map(|(provider, _)| provider)
                .collect::<Vec<_>>(),
            vec![Provider::Tigris, Provider::Oci]
        );
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
            engine,
            engine.database_owner().tenant().to_text().as_str(),
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
        // Soft-delete detaches capture but keeps the historical destination
        // catalog alive until the physical deletion boundary.
        fleet.detach(&identity);
        assert!(fleet.retained_points(&identity).is_ok());
        let receipt =
            fleet.lifecycle_delete(&identity, "delete-after-policy-switch").await.unwrap();
        assert_eq!(receipt.action, "delete_owned_data");
        assert!(receipt.point_digest.parse::<usize>().unwrap() > 0);
        assert!(first.list_points(&identity, 8).await.unwrap().is_empty());
        assert!(second.list_points(&identity, 8).await.unwrap().is_empty());
        assert!(fleet.sync_once().await.is_empty());
    }

    async fn verify_fleet_retention(
        fleet: &mut NativeBackupFleet,
        engine: &Engine,
        staging: &std::path::Path,
        limits: FleetLimits,
        first: &Arc<ObjectDestination>,
        second: &Arc<ObjectDestination>,
    ) {
        let identity = engine.database_id().to_string();
        let key_provider = fleet.streams.get(&identity).unwrap().config.keys.clone();
        // A new full root lets retention remove older complete chains;
        // each remaining provider still restores without those predecessors.
        std::thread::sleep(Duration::from_millis(2));
        committed(put(
            engine,
            engine.database_owner().tenant().to_text().as_str(),
            "op-3",
            "doc-3",
            "users",
            text("three"),
            LocalDurability::Synced,
        ));
        let lease = key_provider.lease().unwrap();
        let root = run_backup(
            engine,
            BackupPlan::full(
                BackupEncryption::from_database_key_lease(
                    &lease,
                    engine.database_owner().region(),
                    0,
                )
                .unwrap(),
            ),
            staging,
        )
        .unwrap();
        fleet.streams.get_mut(&identity).unwrap().due = Instant::now();
        assert!(fleet.sync_once().await.is_empty());
        let root_name = format!("rp-{}", root.manifest().identity());
        // An uploaded journal is only a hint: loss of the retained root from
        // one provider must block pruning of every older local point.
        first.delete_point(&format!("{identity}/{root_name}")).await.unwrap();
        enforce_retention_at(
            &identity,
            fleet.streams.get(&identity).unwrap(),
            limits,
            root.manifest().captured_unix_millis() + 86_400_001,
        )
        .await
        .expect_err("provider root loss must preserve the older chain");
        assert_eq!(inventory_points(fleet.streams.get(&identity).unwrap()).unwrap().len(), 3);

        // Re-establish the authenticated root at the failed provider. The
        // original upload journal remains an acknowledgement, but pruning is
        // allowed only after a fresh remote download and member digest check.
        first
            .upload_point(&staging.join(&root_name), &format!("{identity}/{root_name}"))
            .await
            .unwrap();
        enforce_retention_at(
            &identity,
            fleet.streams.get(&identity).unwrap(),
            limits,
            root.manifest().captured_unix_millis() + 86_400_001,
        )
        .await
        .unwrap();
        assert_eq!(point_paths(staging, 8).unwrap().len(), 1);
        for provider in [Provider::Oci, Provider::Tigris] {
            let (target_owner, target_config) =
                restore_config("retained-root-restore", engine.database_owner());
            let receipt = fleet
                .restore_from_provider(
                    &identity,
                    provider,
                    None,
                    &target_config,
                    &crate::backup::restore::RestoreIdentity {
                        operation_id: format!("retained-root-restore-{provider:?}"),
                        source_database_id: identity.clone(),
                        target_database_id: target_owner.database().to_string(),
                        provider: format!("{provider:?}").to_lowercase(),
                        selection: "latest".into(),
                        expected_manifest_digest: root.manifest().identity().into(),
                        expected_journal_boundary: root.manifest().boundary_sequence().get(),
                        restore_epoch: 1,
                    },
                )
                .await
                .unwrap();
            assert_eq!(receipt.restored_documents(), 3);
            assert_eq!(receipt.chain_depth(), 1);
        }
        assert_eq!(
            first.list_points(&identity, 8).await.unwrap(),
            vec![format!("{identity}/{root_name}")]
        );
        assert_eq!(
            second.list_points(&identity, 8).await.unwrap(),
            vec![format!("{identity}/{root_name}")]
        );
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
                historical_destinations: Vec::new(),
                policy_revision: "policy-1".into(),
                retention_days: 1,
                staging: staging.clone(),
                placement: PlacementLock::br_only(),
            };
            let limits = FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 16 * 1024 * 1024,
                inventory_bytes_per_stream: 16 * 1024 * 1024,
                points_per_stream: 8,
            };
            let mut fleet = NativeBackupFleet::new(limits).unwrap();
            fleet.attach(config()).unwrap();
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
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
                engine.database_owner().tenant().to_text().as_str(),
                "op-2",
                "doc-2",
                "users",
                text("two"),
                LocalDurability::Synced,
            ));
            assert!(fleet.sync_once().await.is_empty());
            for provider in [Provider::Oci, Provider::Tigris] {
                let (target_owner, target_config) =
                    restore_config("fleet-restore", engine.database_owner());
                let receipt = fleet
                    .restore_from_provider(
                        &identity,
                        provider,
                        None,
                        &target_config,
                        &latest_restore_identity(
                            &fleet,
                            &staging,
                            &identity,
                            &target_owner,
                            format!("fleet-restore-{provider:?}"),
                            provider,
                        ),
                    )
                    .await
                    .unwrap();
                assert_eq!(receipt.restored_documents(), 2);
                assert_eq!(receipt.chain_depth(), 2);
            }
            assert_eq!(
                fleet.health(&identity).unwrap().providers,
                vec![(Provider::Oci, true), (Provider::Tigris, true)]
            );
            verify_fleet_retention(&mut fleet, &engine, &staging, limits, &first, &second).await;
            verify_fleet_policy_and_purge(&mut fleet, &engine, &first, &second).await;
            assert!(!staging.exists());
        });
    }

    #[tokio::test]
    async fn remote_point_resolution_falls_back_after_corrupt_provider_manifest() {
        let (_, engine) = open_engine("fleet-provider-fallback");
        let engine = Arc::new(engine);
        let database = engine.database_id().to_string();
        let staging = location("fleet-provider-fallback-staging");
        let first_store = Arc::new(object_store::memory::InMemory::new());
        let second_store = Arc::new(object_store::memory::InMemory::new());
        let first = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            first_store.clone(),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let second = Arc::new(ObjectDestination::for_test(
            Provider::Tigris,
            second_store,
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let mut fleet = NativeBackupFleet::new(FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 4,
        })
        .unwrap();
        fleet
            .attach(StreamConfig {
                engine: engine.clone(),
                keys: keys(&engine),
                destinations: vec![(Provider::Oci, first), (Provider::Tigris, second)],
                historical_destinations: Vec::new(),
                policy_revision: "provider-fallback".into(),
                retention_days: 1,
                staging,
                placement: PlacementLock::br_only(),
            })
            .unwrap();
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "provider-fallback-op",
            "provider-fallback-doc",
            "users",
            text("provider-fallback"),
            LocalDurability::Synced,
        ));
        assert!(fleet.sync_once().await.is_empty());
        let point = fleet.retained_points(&database).unwrap().pop().unwrap();
        let key = format!(
            "{database}/rp-{}/{}",
            point.identity(),
            crate::backup::manifest::ENCRYPTED_MANIFEST_FILE
        );
        first_store.put(&key.into(), b"corrupt provider manifest".to_vec().into()).await.unwrap();

        let (resolved, provider) =
            fleet.resolve_recovery_point(&database, "latest", "").await.unwrap();
        assert_eq!(provider, Provider::Tigris);
        assert_eq!(resolved.identity(), point.identity());
    }
    #[tokio::test]
    async fn retained_remote_history_restores_selected_chain_and_migrates_after_restart() {
        let (_, engine) = open_engine("remote-inventory-source");
        let engine = Arc::new(engine);
        let identity = engine.database_id().to_string();
        let staging = location("remote-inventory-staging");
        let source = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let target = Arc::new(ObjectDestination::for_test(
            Provider::Tigris,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        let key_provider = keys(&engine);
        let config = || StreamConfig {
            engine: engine.clone(),
            keys: key_provider.clone(),
            destinations: vec![(Provider::Oci, source.clone())],
            historical_destinations: Vec::new(),
            policy_revision: "initial".into(),
            retention_days: 1,
            staging: staging.clone(),
            placement: PlacementLock::br_only(),
        };
        let limits = FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 2,
        };
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        let mut history = Vec::new();
        for index in 0..4 {
            committed(put(
                &engine,
                engine.database_owner().tenant().to_text().as_str(),
                &format!("remote-op-{index}"),
                &format!("remote-doc-{index}"),
                "users",
                text("value"),
                LocalDurability::Synced,
            ));
            let lease = fleet.streams[&identity].config.keys.lease().unwrap();
            let result = run_backup(
                &engine,
                BackupPlan::full(
                    BackupEncryption::from_database_key_lease(
                        &lease,
                        engine.database_owner().region(),
                        1,
                    )
                    .unwrap(),
                ),
                &staging,
            )
            .unwrap();
            let manifest = result.manifest().clone();
            let name = format!("rp-{}", manifest.identity());
            source.upload_point(&staging.join(&name), &format!("{identity}/{name}")).await.unwrap();
            persist_inventory(&fleet.streams[&identity], limits).unwrap();
            std::fs::remove_dir_all(staging.join(&name)).unwrap();
            history.push(manifest);
        }
        drop(fleet);
        let mut fleet = NativeBackupFleet::new(limits).unwrap();
        fleet.attach(config()).unwrap();
        assert_eq!(fleet.retained_points(&identity).unwrap().len(), 4);
        let selected = &history[0];
        let (owner, restore) = restore_config("remote-selected-restore", engine.database_owner());
        let foreign_owner = crate::storage::DatabaseOwner::try_new(
            owner.environment().as_str(),
            owner.tenant(),
            owner.project(),
            crate::identity::DatabaseUlid::generate(),
            crate::regions::RegionCode::FRA,
        )
        .unwrap();
        let foreign = crate::engine::EngineConfig::durable(location("remote-foreign-restore"))
            .unwrap()
            .with_owner(foreign_owner.clone())
            .with_storage_keys(crate::engine::test_storage_keys(&foreign_owner, 72));
        assert!(matches!(
            fleet
                .restore_from_provider(
                    &identity,
                    Provider::Oci,
                    Some(selected.identity()),
                    &foreign,
                    &crate::backup::restore::RestoreIdentity {
                        operation_id: "remote-foreign-op".into(),
                        source_database_id: identity.clone(),
                        target_database_id: foreign_owner.database().to_string(),
                        provider: "oci".into(),
                        selection: selected.identity().into(),
                        expected_manifest_digest: selected.identity().into(),
                        expected_journal_boundary: selected.boundary_sequence().get(),
                        restore_epoch: 1,
                    }
                )
                .await,
            Err(FleetError::Configuration)
        ));
        let receipt = fleet
            .restore_from_provider(
                &identity,
                Provider::Oci,
                Some(selected.identity()),
                &restore,
                &crate::backup::restore::RestoreIdentity {
                    operation_id: "remote-selected-op".into(),
                    source_database_id: identity.clone(),
                    target_database_id: owner.database().to_string(),
                    provider: "oci".into(),
                    selection: selected.identity().into(),
                    expected_manifest_digest: selected.identity().into(),
                    expected_journal_boundary: selected.boundary_sequence().get(),
                    restore_epoch: 1,
                },
            )
            .await
            .unwrap();
        assert_eq!(receipt.chain_depth(), 1);
        assert_eq!(receipt.restored_documents(), 1);
        let bad_path = location("remote-policy-unavailable");
        std::fs::create_dir_all(&bad_path).unwrap();
        let unavailable = object_store::local::LocalFileSystem::new_with_prefix(&bad_path).unwrap();
        std::fs::remove_dir(&bad_path).unwrap();
        std::fs::write(&bad_path, b"unavailable").unwrap();
        let broken = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(unavailable),
            4 * 1024 * 1024,
            8 * 1024 * 1024,
        ));
        // The first destination receives a point before the second fails. The
        // durable original policy and its complete history remain recoverable.
        assert_eq!(
            fleet
                .change_policy(
                    &identity,
                    "partial",
                    vec![(Provider::Tigris, target.clone()), (Provider::Oci, broken),]
                )
                .await,
            Err(FleetError::Upload)
        );
        assert_eq!(fleet.policy(&identity).unwrap().0, "initial");
        assert_eq!(source.list_points(&identity, 10).await.unwrap().len(), 4);
        assert!(staging.join("policy-transition").exists());
        fleet
            .change_policy(&identity, "migrated", vec![(Provider::Tigris, target.clone())])
            .await
            .unwrap();
        assert_eq!(target.list_points(&identity, 10).await.unwrap().len(), 4);
        assert_eq!(source.list_points(&identity, 10).await.unwrap().len(), 4);
        assert!(point_paths(&staging, 2).unwrap().is_empty());
        assert_eq!(
            fleet.purge_retained(&identity, &[source.clone(), target.clone()]).await.unwrap(),
            8
        );
        assert!(source.list_points(&identity, 10).await.unwrap().is_empty());
        assert!(target.list_points(&identity, 10).await.unwrap().is_empty());
    }

    #[tokio::test]
    async fn lifecycle_delete_after_detach_purges_historical_destinations_at_boundary() {
        let (_, engine) = open_engine("detached-delete-boundary");
        let engine = Arc::new(engine);
        let identity = engine.database_id().to_string();
        let staging = location("detached-delete-boundary-staging");
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
        let mut fleet = NativeBackupFleet::new(FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 4,
        })
        .unwrap();
        fleet
            .attach(StreamConfig {
                engine: Arc::clone(&engine) as Arc<dyn BackupEngineHandle>,
                keys: keys(&engine),
                destinations: vec![(Provider::Oci, Arc::clone(&first))],
                historical_destinations: Vec::new(),
                policy_revision: "initial".into(),
                retention_days: 1,
                staging: staging.clone(),
                placement: PlacementLock::br_only(),
            })
            .unwrap();
        committed(put(
            &engine,
            engine.database_owner().tenant().to_text().as_str(),
            "detached-op",
            "detached-doc",
            "users",
            text("value"),
            LocalDurability::Synced,
        ));
        assert!(fleet.sync_once().await.is_empty());
        assert_eq!(first.list_points(&identity, 4).await.unwrap().len(), 1);

        fleet
            .change_policy(&identity, "migrated", vec![(Provider::Tigris, Arc::clone(&second))])
            .await
            .unwrap();
        assert_eq!(second.list_points(&identity, 4).await.unwrap().len(), 1);
        let tenant =
            crate::types::TenantId::try_new(&engine.database_owner().tenant().to_text()).unwrap();
        let scope = engine.scope_for(&tenant);
        engine.lifecycle_pause(&scope, "pause-before-detach").unwrap();
        fleet.suspend_for_restore(&identity).unwrap();
        fleet.detach(&identity);

        // Detachment closes capture but retains the catalog needed to delete
        // both the active and retired provider copies.
        assert!(fleet.sync_once().await.is_empty());
        assert!(fleet.retained_until_unix_millis(&identity).unwrap().is_some());
        assert_eq!(
            fleet.lifecycle_verify(&identity, "verify-detached").unwrap().database,
            identity
        );
        assert_eq!(fleet.configured_providers(&identity).unwrap(), vec![Provider::Tigris]);

        let receipt = fleet.lifecycle_delete(&identity, "delete-detached").await.unwrap();
        assert_eq!(receipt.action, "delete_owned_data");
        assert!(first.list_points(&identity, 4).await.unwrap().is_empty());
        assert!(second.list_points(&identity, 4).await.unwrap().is_empty());
        assert!(fleet.retained_until_unix_millis(&identity).is_err());
        assert!(fleet.configured_providers(&identity).is_err());
        assert!(!staging.exists());
    }
}
