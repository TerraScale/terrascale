//! Canonical SQL result-view delivery (D-14/D-38/D-46/D-55).
//!
//! Views are keyed by the complete SQL attach input and authorization scope.
//! Snapshot and log revisions are immutable `MessagePack` envelopes. Empty
//! long-polls are `204` and `no-store`; no client-visible numeric session is
//! used by this transport.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, OnceLock, Weak};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use axum::extract::ws::{Message, WebSocket};
use axum::extract::{Path, Query, State, WebSocketUpgrade};
use axum::http::{HeaderMap, HeaderValue, StatusCode};
use axum::http::{Method, Uri};
use axum::response::{IntoResponse, Response};
use serde::{Deserialize, Serialize};
use tokio::sync::Notify;

use crate::api::{ApiError, ApiState};
use crate::view_capability::{
    ViewCapabilityClaims, ViewCapabilityIssuer, ViewCapabilityVerifier, hmac_sha256_hex,
};

mod incremental;
mod polling;

/// Fixed capability header; it never appears in a URL or cache key.
pub const VIEW_CAPABILITY_HEADER: &str = "X-TerraScale-View-Capability";
/// `MessagePack` media type for snapshots, logs, resets, and protocol errors.
pub const MSGPACK_MEDIA_TYPE: &str = "application/msgpack";
/// Maximum SQL statement bytes accepted by view attach.
pub const MAX_ATTACH_SQL_BYTES: usize = 262_144;
/// Maximum parameter bytes accepted by view attach.
pub const MAX_ATTACH_BINARY_BYTES: usize = 1_048_576;
/// Maximum HTTP long-poll duration.
pub const LONG_POLL_WINDOW: Duration = Duration::from_secs(25);
const EMISSION_CADENCE: Duration = Duration::from_millis(100);
const RETENTION: Duration = Duration::from_hours(168);
const CAPABILITY_BUCKET: u64 = 600;
const CONFORMANCE_CAPABILITY_KEY: &[u8] = b"terrascale-reactive-capability-v1";

/// Header emitted by the sync edge after it has authenticated and removed
/// customer credentials.  It is deliberately bound to the complete request
/// target, so a proof for a cached log response cannot be replayed on a
/// different view, method, or offset.
pub const ORIGIN_PROOF_HEADER: &str = "x-terrascale-origin-proof";

/// Marker inserted by the authentication middleware after an origin proof
/// has been verified.  Snapshot and log handlers may use this marker as the
/// edge-authenticated path; direct regional requests still use the bearer
/// credential and view capability path.
#[derive(Clone, Copy, Debug)]
pub struct OriginProofVerified;

/// Current and previous HMAC keys accepted from the sync edge during a key
/// rotation.  Empty keys are the explicit disabled state used by local test
/// routers; production composition must inject real key material.
#[derive(Clone, Debug)]
pub struct OriginProofKeyRing {
    current: Vec<u8>,
    previous: Option<Vec<u8>>,
}

/// Current and previous keys used to mint and verify view capabilities.
/// Production composition injects this ring; the conformance constructor is
/// retained solely for deterministic contract fixtures.
#[derive(Clone, Debug)]
pub struct CapabilityKeyRing {
    current: Vec<u8>,
    previous: Option<Vec<u8>>,
}

impl CapabilityKeyRing {
    /// Builds a validated capability key ring.
    pub fn new(current: impl AsRef<[u8]>, previous: Option<impl AsRef<[u8]>>) -> Option<Self> {
        let current = current.as_ref();
        if current.len() < 16 || current.len() > 4096 {
            return None;
        }
        let previous = previous.map(|key| key.as_ref().to_vec());
        if previous
            .as_ref()
            .is_some_and(|key| key.len() < 16 || key.len() > 4096 || key.as_slice() == current)
        {
            return None;
        }
        Some(Self { current: current.to_vec(), previous })
    }

    /// Deterministic ring used by old conformance fixture generation.
    ///
    /// # Panics
    ///
    /// Panics if the compiled conformance key violates the key length limits.
    #[must_use]
    pub fn conformance() -> Self {
        Self::new(CONFORMANCE_CAPABILITY_KEY, None::<&[u8]>).expect("conformance key is valid")
    }

    /// Explicit disabled ring used until production key material is loaded.
    #[must_use]
    pub const fn disabled() -> Self {
        Self { current: Vec::new(), previous: None }
    }

    fn issuer(&self) -> Option<ViewCapabilityIssuer> {
        ViewCapabilityIssuer::new(&self.current).ok()
    }

    fn verifier(&self) -> Option<ViewCapabilityVerifier> {
        match self.previous.as_deref() {
            Some(previous) => ViewCapabilityVerifier::with_previous(&self.current, previous).ok(),
            None => ViewCapabilityVerifier::new(&self.current).ok(),
        }
    }
}

impl Default for CapabilityKeyRing {
    fn default() -> Self {
        Self::disabled()
    }
}

impl OriginProofKeyRing {
    /// Builds a key ring. Keys must have at least 16 bytes of entropy.
    pub fn new(current: impl AsRef<[u8]>, previous: Option<impl AsRef<[u8]>>) -> Option<Self> {
        let current = current.as_ref();
        if current.len() < 16 || current.len() > 4096 {
            return None;
        }
        let previous = previous.map(|key| key.as_ref().to_vec());
        if previous
            .as_ref()
            .is_some_and(|key| key.len() < 16 || key.len() > 4096 || key.as_slice() == current)
        {
            return None;
        }
        Some(Self { current: current.to_vec(), previous })
    }

    /// The disabled ring used by local development and unit-test fixtures.
    #[must_use]
    pub const fn disabled() -> Self {
        Self { current: Vec::new(), previous: None }
    }
}

impl Default for OriginProofKeyRing {
    fn default() -> Self {
        Self::disabled()
    }
}

/// Verifies the edge's origin proof against the current and previous
/// ten-minute bucket and key.  The canonical target is the URI's exact
/// path-and-query spelling, including any percent-encoding.
pub fn verify_origin_proof(
    headers: &HeaderMap,
    method: &Method,
    uri: &Uri,
    keys: &OriginProofKeyRing,
    now: u64,
) -> bool {
    let Some(raw) = headers.get(ORIGIN_PROOF_HEADER).and_then(|value| value.to_str().ok()) else {
        return false;
    };
    let fields = raw.split('.').collect::<Vec<_>>();
    if fields.len() != 3 || fields[0] != "v1" || fields[1].is_empty() || fields[2].len() != 64 {
        return false;
    }
    let Ok(bucket) = fields[1].parse::<u64>() else { return false };
    // The edge carries the bucket's aligned Unix timestamp. Accept exactly
    // the current and preceding bucket during one rotation overlap.
    let current_bucket = now / CAPABILITY_BUCKET * CAPABILITY_BUCKET;
    if bucket != current_bucket && bucket.saturating_add(CAPABILITY_BUCKET) != current_bucket {
        return false;
    }
    let target = uri
        .path_and_query()
        .map_or_else(|| uri.path().to_owned(), |value| value.as_str().to_owned());
    let message = format!("v1.{}.{}.{}", method.as_str().to_ascii_uppercase(), target, bucket);
    let presented = fields[2].as_bytes();
    if !presented.iter().all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase()) {
        return false;
    }
    let valid = |key: &[u8]| {
        constant_time_equal(hmac_sha256_hex(key, message.as_bytes()).as_bytes(), presented)
    };
    (!keys.current.is_empty() && valid(&keys.current))
        || keys.previous.as_deref().is_some_and(valid)
}

/// Mints an origin proof for integration tests and edge conformance tools.
/// Production callers receive this value from the sync edge, never from the
/// member API.
#[must_use]
pub fn origin_proof_for(method: &Method, target: &str, bucket: u64, key: &[u8]) -> String {
    let message = format!("v1.{}.{}.{}", method.as_str().to_ascii_uppercase(), target, bucket);
    format!("v1.{bucket}.{}", hmac_sha256_hex(key, message.as_bytes()))
}

/// Canonical SQL attach input. Legacy collection-condition fields are absent.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
pub struct AttachRequest {
    /// Durable database identity.
    #[serde(rename = "databaseId")]
    pub database_id: String,
    /// Read-only SQL statement.
    pub statement: String,
    /// Base64 encoded SQL parameter bytes.
    #[serde(default)]
    pub parameters: String,
    /// Base64 encoded opaque resume checkpoint.
    #[serde(default)]
    pub checkpoint: Option<String>,
    /// Optional client correlation identity.
    #[serde(default)]
    pub subscription: Option<String>,
    /// Entitlement epoch bound into the view identity.
    #[serde(default)]
    pub entitlement_epoch: u64,
}

/// Attach metadata returned to an SDK.
#[derive(Clone, Debug, Serialize)]
pub struct AttachResponse {
    /// Opaque shared view id.
    pub view: String,
    /// Current view generation.
    pub generation: u64,
    /// Immutable snapshot chunk URL(s).
    pub snapshot_chunks: Vec<String>,
    /// Immutable log URL.
    pub log: String,
    /// Base64 encoded opaque checkpoint.
    pub checkpoint: String,
    /// Capability for [`VIEW_CAPABILITY_HEADER`].
    pub capability: String,
    /// Fixed header spelling.
    pub capability_header: &'static str,
    /// Numeric log offset, separate from the opaque checkpoint.
    pub offset: u64,
}

#[derive(Clone)]
struct Frozen {
    bytes: Arc<[u8]>,
    created: SystemTime,
}

struct View {
    id: String,
    generation: u64,
    scope: String,
    capability_scope: String,
    entitlement_epoch: u64,
    region: terrabase::regions::RegionCode,
    /// Digest of the complete attach identity. This includes the durable
    /// database incarnation and all planner/policy generations.
    identity_digest: String,
    items: Vec<terrabase_sync_core::wire::ResultItem>,
    digest: String,
    head: u64,
    observed_sequence: u64,
    floor: u64,
    snapshot: BTreeMap<u64, Frozen>,
    log: BTreeMap<u64, Frozen>,
    materializer: Option<Materializer>,
    /// One query execution per shared view, independent of other views.
    refreshing: Arc<Mutex<()>>,
    refresh_after: Option<tokio::time::Instant>,
}

impl View {
    /// Retains the current snapshot even for an idle view so reattachment can
    /// always start from a complete result. Older cuts follow log retention.
    fn prune(&mut self, now: SystemTime) {
        while self.log.first_key_value().is_some_and(|(_, item)| {
            now.duration_since(item.created).unwrap_or_default() > RETENTION
        }) {
            if let Some((revision, _)) = self.log.pop_first() {
                self.floor = self.floor.max(revision);
            }
        }
        while self.snapshot.len() > 1
            && self.snapshot.first_key_value().is_some_and(|(_, item)| {
                now.duration_since(item.created).unwrap_or_default() > RETENTION
            })
        {
            self.snapshot.pop_first();
        }
    }
}

#[derive(Clone)]
struct Materialization {
    revision: u64,
    items: Vec<terrabase_sync_core::wire::ResultItem>,
    digest: String,
    /// Stable, length-delimited identity material (database incarnation,
    /// normalized plan/parameters, schema/index/rule versions, and Region).
    identity: Vec<u8>,
    materializer: Option<Materializer>,
}

#[derive(Clone)]
struct Materializer {
    host: Arc<terrabase_tbep::member_host::MemberHost>,
    plan: terrabase::query::QueryPlan,
    incremental: Option<incremental::SqlMaterializer>,
    tenant: terrabase::types::TenantId,
    changed: Arc<Notify>,
    watch: Option<Arc<terrabase::engine::PublicationWatch>>,
}

/// Process-wide canonical shared-view registry.
pub struct ViewStore {
    log_polls: Mutex<BTreeMap<polling::LogKey, Weak<polling::LogPoll>>>,
    #[cfg(test)]
    poll_starts: std::sync::atomic::AtomicUsize,
    initializing: Mutex<BTreeMap<String, Weak<Mutex<()>>>>,
    views: Mutex<BTreeMap<String, View>>,
    changed: Arc<Notify>,
}

fn attach_identity(
    request: &AttachRequest,
    scope: &str,
    region: terrabase::regions::RegionCode,
    materialization: Option<&Materialization>,
) -> Result<Vec<u8>, ApiError> {
    let parameters =
        decode_base64(&request.parameters).map_err(|()| invalid_attach("parameters"))?;
    if parameters.len() > MAX_ATTACH_BINARY_BYTES {
        return Err(invalid_attach("parameters"));
    }
    let checkpoint = request
        .checkpoint
        .as_deref()
        .map(decode_base64)
        .transpose()
        .map_err(|()| invalid_attach("checkpoint"))?;
    if checkpoint.as_ref().is_some_and(|value| value.is_empty() || value.len() > 4096) {
        return Err(invalid_attach("checkpoint"));
    }
    let statement = request.statement.trim();
    if statement.is_empty() || statement.len() > MAX_ATTACH_SQL_BYTES {
        return Err(invalid_attach("statement"));
    }
    let parsed = terrabase::query::sql::parse(statement, MAX_ATTACH_SQL_BYTES)
        .map_err(|_| sql_error("the statement is not valid read-only SQL"))?;
    if let terrabase::query::sql::LiveSubset::ExecuteOnly { operator } =
        terrabase::query::sql::classify_live_subset(&parsed)
    {
        return Err(ApiError { status: StatusCode::UNPROCESSABLE_ENTITY, code: "not_subscribable", message: format!("the SQL statement is execute-only ({operator})"), fix: "subscribe a single-collection SELECT with scalar predicates, ORDER BY, LIMIT, or MATCH".into(), placement: None });
    }
    let canonical_statement = canonical_sql(statement);
    let mut key = Vec::with_capacity(statement.len() + scope.len() + parameters.len() + 256);
    put_identity(&mut key, b"database", request.database_id.as_bytes());
    // A materialization identity is supplied by the engine path. The
    // fallback remains deterministic for conformance fixtures.
    if let Some(materialization) = &materialization {
        put_identity(&mut key, b"engine", &materialization.identity);
    }
    put_identity(&mut key, b"sql", canonical_statement.as_bytes());
    put_identity(&mut key, b"scope", scope.as_bytes());
    put_identity(&mut key, b"parameters", &parameters);
    put_identity(&mut key, b"entitlement", &request.entitlement_epoch.to_be_bytes());
    put_identity(&mut key, b"region", region.as_bytes().as_ref());
    Ok(key)
}

fn initial_view(
    request: &AttachRequest,
    scope: &str,
    region: terrabase::regions::RegionCode,
    materialization: Option<&Materialization>,
    key: &[u8],
    view_id: &str,
) -> View {
    let revision = materialization.as_ref().map_or(0, |value| value.revision);
    let digest =
        materialization.as_ref().map_or_else(|| digest_bytes(&[]), |value| value.digest.clone());
    let identity_digest = blake3::hash(key).to_hex().to_string();
    let bytes = materialization.as_ref().map_or_else(
        || snapshot_envelope(view_id, 1, revision, checkpoint_bytes(view_id, 1, revision)),
        |value| {
            snapshot_envelope_with_items(
                view_id,
                1,
                revision,
                value.items.clone(),
                digest.clone(),
                checkpoint_bytes_with_identity(view_id, 1, revision, &identity_digest),
            )
        },
    );
    let mut snapshot = BTreeMap::new();
    snapshot.insert(revision, Frozen { bytes: Arc::from(bytes), created: SystemTime::now() });
    View {
        id: view_id.to_owned(),
        generation: 1,
        scope: scope.to_owned(),
        capability_scope: capability_scope(scope),
        entitlement_epoch: request.entitlement_epoch,
        region,
        identity_digest,
        items: materialization.as_ref().map_or_else(Vec::new, |value| value.items.clone()),
        digest,
        head: revision,
        observed_sequence: revision,
        floor: 0,
        snapshot,
        log: BTreeMap::new(),
        materializer: materialization.as_ref().and_then(|value| value.materializer.clone()),
        refreshing: Arc::new(Mutex::new(())),
        refresh_after: materialization
            .and_then(|value| value.materializer.as_ref())
            .map(|_| tokio::time::Instant::now() + EMISSION_CADENCE),
    }
}

fn attach_response(view: &View, capability_keys: &CapabilityKeyRing) -> AttachResponse {
    let offset = view.head;
    let checkpoint =
        checkpoint_bytes_with_identity(&view.id, view.generation, offset, &view.identity_digest);
    let capability = capability_for_region(
        &view.id,
        view.generation,
        &view.capability_scope,
        view.entitlement_epoch,
        view.region,
        now_seconds(),
        capability_keys,
    );
    AttachResponse {
        view: view.id.clone(),
        generation: view.generation,
        snapshot_chunks: vec![format!(
            "/api/v1/reactive/views/{}/{}/snapshot/0?cut={offset}",
            view.id, view.generation
        )],
        log: format!("/api/v1/reactive/views/{}/{}/log?offset={offset}", view.id, view.generation),
        checkpoint: encode_base64(&checkpoint),
        capability,
        capability_header: VIEW_CAPABILITY_HEADER,
        offset,
    }
}

impl ViewStore {
    fn new() -> Self {
        Self {
            log_polls: Mutex::new(BTreeMap::new()),
            #[cfg(test)]
            poll_starts: std::sync::atomic::AtomicUsize::new(0),
            initializing: Mutex::new(BTreeMap::new()),
            views: Mutex::new(BTreeMap::new()),
            changed: Arc::new(Notify::new()),
        }
    }

    /// Attaches request metadata without resolving a database. The HTTP
    /// handler performs database existence and authorization checks first.
    ///
    /// # Errors
    ///
    /// Returns invalid-input, unsupported-SQL, view-identity, or storage errors.
    pub fn attach(
        &self,
        request: &AttachRequest,
        scope: &str,
    ) -> Result<(AttachResponse, String), ApiError> {
        let keys = CapabilityKeyRing::conformance();
        self.attach_materialized(
            request,
            scope,
            terrabase::regions::RegionCode::GRU,
            None,
            &keys,
            None,
        )
    }

    /// Attaches one query-backed view. `materialization` is produced from a
    /// single immutable engine snapshot before this lock is acquired, so the
    /// registry never installs the old empty revision-0 placeholder for a
    /// real HTTP attach.
    fn attach_materialized(
        &self,
        request: &AttachRequest,
        scope: &str,
        region: terrabase::regions::RegionCode,
        materialization: Option<&Materialization>,
        capability_keys: &CapabilityKeyRing,
        guards: Option<&crate::guards::AbuseGuards>,
    ) -> Result<(AttachResponse, String), ApiError> {
        let key = attach_identity(request, scope, region, materialization)?;
        let view_id = format!("v-{}", blake3::hash(&key).to_hex());
        let initializing = {
            let mut pending = self.initializing.lock().map_err(|_| ApiError::storage_failure())?;
            pending.retain(|_, lock| lock.strong_count() > 0);
            if let Some(lock) = pending.get(&view_id).and_then(Weak::upgrade) {
                lock
            } else {
                let lock = Arc::new(Mutex::new(()));
                pending.insert(view_id.clone(), Arc::downgrade(&lock));
                lock
            }
        };
        let _initializing = initializing.lock().map_err(|_| ApiError::storage_failure())?;
        // Reattachment must publish changes through the same log as existing
        // readers. Installing a newer snapshot directly would skip their delta.
        let existing =
            self.views.lock().map_err(|_| ApiError::storage_failure())?.contains_key(&view_id);
        if existing {
            self.refresh_view(&view_id)?;
        }
        let initialized = if existing {
            None
        } else {
            materialization.map(Materialization::initialize).transpose()?
        };
        let materialization = initialized.as_ref().or(materialization);
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        if views.get(&view_id).is_some_and(|view| {
            view.scope != scope || view.entitlement_epoch != request.entitlement_epoch
        }) {
            return Err(ApiError {
                status: StatusCode::CONFLICT,
                code: "view_identity_conflict",
                message: "the shared view identity has a conflicting authorization binding".into(),
                fix: "attach with the current authorization scope and entitlement epoch".into(),
                placement: None,
            });
        }
        // Serialize quota admission and publication. A rejected request never
        // installs a view and therefore never needs to delete a shared entry.
        if let Some(guards) = guards {
            guards
                .attach_view(&request.database_id, &view_id)
                .map_err(|error| ApiError::abuse_guard(&error))?;
        }
        let view = views.entry(view_id.clone()).or_insert_with(|| {
            initial_view(request, scope, region, materialization, &key, &view_id)
        });
        Ok((attach_response(view, capability_keys), view.id.clone()))
    }

    /// Appends one canonical manifest message exactly once.
    ///
    /// # Errors
    ///
    /// Returns an encoding, missing-view, or storage error. A retry must match
    /// retained bytes at its revision; conflicting or expired retries are refused.
    pub fn append(
        &self,
        view_id: &str,
        revision: u64,
        message: terrabase_sync_core::wire::Message,
    ) -> Result<(), ApiError> {
        let bytes =
            terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
                features: 0,
                correlation: 0,
                causation: revision,
                message,
            })
            .map_err(|_| frame_error())?;
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let Some(view) = views.get_mut(view_id) else {
            return Err(unknown_view());
        };
        view.prune(SystemTime::now());
        if revision <= view.head {
            return if view.log.get(&revision).is_some_and(|frozen| frozen.bytes.as_ref() == bytes) {
                Ok(())
            } else {
                Err(ApiError {
                    status: StatusCode::CONFLICT,
                    code: "reactive_revision_conflict",
                    message: "the revision cannot be verified against the retained log bytes"
                        .into(),
                    fix: "re-attach and resume from the current checkpoint".into(),
                    placement: None,
                })
            };
        }
        let created = SystemTime::now();
        view.head = revision;
        view.log.insert(revision, Frozen { bytes: Arc::from(bytes), created });
        drop(views);
        self.changed.notify_waiters();
        Ok(())
    }

    fn read_log(
        &self,
        view_id: &str,
        generation: u64,
        offset: u64,
    ) -> Result<ReadResult, ApiError> {
        match self.read_retained_log(view_id, generation, offset)? {
            ReadResult::Empty => {}
            retained => return Ok(retained),
        }
        self.refresh_view(view_id)?;
        match self.read_retained_log(view_id, generation, offset)? {
            ReadResult::Empty => {
                Ok(self.refresh_deadline(view_id)?.map_or(ReadResult::Empty, ReadResult::Deferred))
            }
            retained => Ok(retained),
        }
    }

    /// Replay is independent of the materializer: an existing immutable
    /// response or reset must not wait for a newer query execution.
    fn read_retained_log(
        &self,
        view_id: &str,
        generation: u64,
        offset: u64,
    ) -> Result<ReadResult, ApiError> {
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let view = views.get_mut(view_id).ok_or_else(unknown_view)?;
        if generation != view.generation {
            return Err(unknown_view());
        }
        view.prune(SystemTime::now());
        if offset < view.floor {
            return Ok(ReadResult::Reset(reset_bytes(view)));
        }
        if offset > view.head {
            return Err(offset_gap());
        }
        if let Some((revision, frozen)) =
            view.log.range((std::ops::Bound::Excluded(offset), std::ops::Bound::Unbounded)).next()
        {
            return Ok(ReadResult::Data { revision: *revision, frozen: frozen.clone() });
        }
        Ok(ReadResult::Empty)
    }

    fn view_refresh_lock(&self, view_id: &str) -> Result<Arc<Mutex<()>>, ApiError> {
        let views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        Ok(Arc::clone(&views.get(view_id).ok_or_else(unknown_view)?.refreshing))
    }

    fn refresh_deadline(&self, view_id: &str) -> Result<Option<tokio::time::Instant>, ApiError> {
        let views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        Ok(views
            .get(view_id)
            .ok_or_else(unknown_view)?
            .refresh_after
            .filter(|deadline| *deadline > tokio::time::Instant::now()))
    }

    /// Maintains bounded SQL views from the journal, refilling candidates
    /// from storage when needed. Readers at the same durable cut
    /// observe the same frozen `MessagePack` bytes.
    fn refresh_view(&self, view_id: &str) -> Result<(), ApiError> {
        let refreshing = self.view_refresh_lock(view_id)?;
        // Never wait for a query while holding the registry lock. Concurrent
        // refreshes of this view recheck its durable cut after acquiring it.
        let _singleflight = refreshing.lock().map_err(|_| ApiError::storage_failure())?;
        if self.refresh_deadline(view_id)?.is_some() {
            return Ok(());
        }
        self.refresh_view_now(view_id)
    }

    fn refresh_view_now(&self, view_id: &str) -> Result<(), ApiError> {
        let (materializer, previous, generation, identity_digest, observed_sequence) = {
            let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
            let view = views.get_mut(view_id).ok_or_else(unknown_view)?;
            view.prune(SystemTime::now());
            (
                view.materializer.clone(),
                view.items.clone(),
                view.generation,
                view.identity_digest.clone(),
                view.observed_sequence,
            )
        };
        let Some(mut materializer) = materializer else { return Ok(()) };
        let snapshot = materializer
            .host
            .engine()
            .read_snapshot(&materializer.host.engine().scope_for(&materializer.tenant))
            .map_err(|_| ApiError::storage_failure())?;
        if snapshot.sequence().get() <= observed_sequence {
            return Ok(());
        }
        let items = materializer.refresh_items(&snapshot, observed_sequence)?;
        let next_digest = digest_items(&items);
        let revision = snapshot.sequence().get();
        {
            let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
            let view = views.get_mut(view_id).ok_or_else(unknown_view)?;
            if next_digest == view.digest {
                // An irrelevant write advances the source watermark, not
                // the published result revision or its immutable snapshot URL.
                view.materializer = Some(materializer);
                view.observed_sequence = revision;
                view.refresh_after = Some(tokio::time::Instant::now() + EMISSION_CADENCE);
                return Ok(());
            }
        }
        let operations = terrabase_sync_core::view::plan::diff(&previous, &items);
        let checkpoint =
            checkpoint_bytes_with_identity(view_id, generation, revision, &identity_digest);
        let bytes =
            terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
                features: 0,
                correlation: 0,
                causation: revision,
                message: terrabase_sync_core::wire::Message::Delta {
                    subscription: view_id.to_owned(),
                    view_generation: generation,
                    from_revision: self.current_head(view_id)?,
                    to_revision: revision,
                    operations,
                    digest: next_digest.clone(),
                    checkpoint,
                },
            })
            .map_err(|_| frame_error())?;
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let view = views.get_mut(view_id).ok_or_else(unknown_view)?;
        if revision <= view.head {
            return Ok(());
        }
        view.log.insert(revision, Frozen { bytes: Arc::from(bytes), created: SystemTime::now() });
        view.snapshot.insert(
            revision,
            Frozen {
                bytes: Arc::from(snapshot_envelope_with_items(
                    view_id,
                    generation,
                    revision,
                    items.clone(),
                    next_digest.clone(),
                    checkpoint_bytes_with_identity(view_id, generation, revision, &identity_digest),
                )),
                created: SystemTime::now(),
            },
        );
        view.materializer = Some(materializer);
        view.observed_sequence = revision;
        view.items = items;
        view.digest = next_digest;
        view.head = revision;
        view.refresh_after = Some(tokio::time::Instant::now() + EMISSION_CADENCE);
        view.prune(SystemTime::now());
        drop(views);
        self.changed.notify_waiters();
        Ok(())
    }

    fn current_head(&self, view_id: &str) -> Result<u64, ApiError> {
        self.views
            .lock()
            .map_err(|_| ApiError::storage_failure())?
            .get(view_id)
            .map(|view| view.head)
            .ok_or_else(unknown_view)
    }

    fn read_snapshot(&self, view_id: &str, generation: u64, cut: u64) -> Result<Frozen, ApiError> {
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let view = views.get_mut(view_id).ok_or_else(unknown_view)?;
        if generation != view.generation {
            return Err(unknown_view());
        }
        view.prune(SystemTime::now());
        view.snapshot.get(&cut).cloned().ok_or_else(unknown_view)
    }

    fn capability_claims(
        &self,
        view_id: &str,
        generation: u64,
    ) -> Result<ViewCapabilityClaims, ApiError> {
        let views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let view = views.get(view_id).ok_or_else(unknown_view)?;
        if generation != view.generation {
            return Err(unknown_view());
        }
        Ok(ViewCapabilityClaims::new(
            &view.id,
            view.generation,
            &view.capability_scope,
            view.entitlement_epoch,
            view.region,
        ))
    }
}

static STORE: OnceLock<Arc<ViewStore>> = OnceLock::new();
fn store() -> &'static Arc<ViewStore> {
    STORE.get_or_init(|| Arc::new(ViewStore::new()))
}

/// Returns the process-shared view store for the journal integration seam.
#[must_use]
pub fn view_store() -> Arc<ViewStore> {
    Arc::clone(store())
}

/// Encodes an empty snapshot envelope for conformance fixtures.
#[must_use]
pub fn empty_snapshot_bytes(view: &str, generation: u64, revision: u64) -> Vec<u8> {
    snapshot_envelope(view, generation, revision, checkpoint_bytes(view, generation, revision))
}

/// Appends one canonical server message to a shared view's immutable log.
///
/// # Errors
///
/// Returns the encoding, view, storage, or revision refusal from [`ViewStore::append`].
pub fn append_message(
    view: &str,
    revision: u64,
    message: terrabase_sync_core::wire::Message,
) -> Result<(), ApiError> {
    store().append(view, revision, message)
}

/// Attaches to a canonical SQL result view and issues its capability.
#[utoipa::path(post, path = "/api/v1/reactive/views", responses((status = 201, description = "Canonical SQL view attached")))]
pub async fn attach_view(
    State(state): State<Arc<ApiState>>,
    axum::extract::Extension(principal): axum::extract::Extension<crate::api_keys::Principal>,
    body: String,
) -> Response {
    let request: AttachRequest = match serde_json::from_str(&body) {
        Ok(request) => request,
        Err(_) => return invalid_attach("request").into_response(),
    };
    if request.database_id.is_empty() || request.database_id.len() > 256 {
        return invalid_attach("databaseId").into_response();
    }
    if !principal.scope().admits(crate::api_keys::Verbs::READ, &request.database_id) {
        return ApiError::forbidden("read on the requested database").into_response();
    }
    let host = match crate::api::document_host(&state, &request.database_id) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = crate::api::check_read_guard(
        &state,
        &request.database_id,
        terrabase::observability::Operation::Subscribe,
    ) {
        return error.into_response();
    }
    let scope = format!("{}:{}:{}", state.scope.0, state.scope.1, request.database_id);
    let materialization = match prepare_view(&state, &host, &request, &scope, Some(&principal)) {
        Ok(materialization) => materialization,
        Err(error) => return error.into_response(),
    };
    let (response, _) = match store().attach_materialized(
        &request,
        &scope,
        state.region,
        Some(&materialization),
        &state.capability_keys,
        Some(&state.guards),
    ) {
        Ok(result) => result,
        Err(error) => {
            if error.code == "abuse_guard_exceeded" {
                state.metrics.record_admission_rejection(
                    terrabase::observability::Operation::Subscribe,
                    terrabase::observability::AdmissionReason::Capacity,
                );
            }
            return error.into_response();
        }
    };
    let Ok(mut body) = serde_json::to_vec(&response) else {
        return frame_error().into_response();
    };
    body.push(b'\n');
    let mut result = (StatusCode::CREATED, body).into_response();
    result
        .headers_mut()
        .insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static("application/json"));
    result.headers_mut().insert("x-content-type-options", HeaderValue::from_static("nosniff"));
    result
}

/// Serves an immutable snapshot chunk at an explicit journal cut.
#[utoipa::path(get, path = "/api/v1/reactive/views/{view}/{generation}/snapshot/{chunk}", responses((status = 200, description = "Immutable view snapshot")))]
pub async fn snapshot(
    State(state): State<Arc<ApiState>>,
    Path((view, generation, chunk)): Path<(String, u64, String)>,
    Query(query): Query<CutQuery>,
    headers: HeaderMap,
    proof: Option<axum::extract::Extension<OriginProofVerified>>,
) -> Response {
    if chunk != "0" {
        return unknown_view().into_response();
    }
    if let Err(error) =
        verify_view_header(&headers, &view, generation, proof.is_some(), &state.capability_keys)
    {
        return error.into_response();
    }
    match store().read_snapshot(&view, generation, query.cut.unwrap_or(0)) {
        Ok(frozen) => {
            immutable_response(&frozen, &format!("\"{view}-snapshot-{}\"", query.cut.unwrap_or(0)))
        }
        Err(error) => error.into_response(),
    }
}

/// Serves the next immutable log revision, waiting at most 25 seconds.
#[utoipa::path(get, path = "/api/v1/reactive/views/{view}/{generation}/log", responses((status = 200, description = "Immutable view delta")))]
pub async fn view_log(
    State(state): State<Arc<ApiState>>,
    Path((view, generation)): Path<(String, u64)>,
    Query(query): Query<OffsetQuery>,
    headers: HeaderMap,
    proof: Option<axum::extract::Extension<OriginProofVerified>>,
) -> Response {
    let Some(offset) = query.offset else {
        return invalid_offset().into_response();
    };
    if let Err(error) =
        verify_view_header(&headers, &view, generation, proof.is_some(), &state.capability_keys)
    {
        return error.into_response();
    }
    match store().poll_log(&view, generation, offset).await {
        Ok(ReadResult::Data { revision, frozen }) => {
            immutable_response(&frozen, &format!("\"{view}-{generation}-{revision}\""))
        }
        Ok(ReadResult::Reset(bytes)) => reset_response(bytes),
        Ok(ReadResult::Empty | ReadResult::Deferred(_)) => empty_timeout(),
        Err(error) => error.into_response(),
    }
}

/// Optional WebSocket transport using the same indexed manifest envelopes.
#[utoipa::path(get, path = "/api/v1/databases/{name}/reactive", responses((status = 101, description = "Reactive WebSocket connected")))]
pub async fn websocket(
    State(state): State<Arc<ApiState>>,
    axum::extract::Extension(principal): axum::extract::Extension<crate::api_keys::Principal>,
    Path(name): Path<String>,
    upgrade: WebSocketUpgrade,
) -> Response {
    if let Err(error) = crate::api::document_host(&state, &name) {
        return error.into_response();
    }
    if let Err(error) =
        crate::api::check_read_guard(&state, &name, terrabase::observability::Operation::Subscribe)
    {
        return error.into_response();
    }
    if let Err(rejection) = state.guards.acquire_ws(&name) {
        state.metrics.record_admission_rejection(
            terrabase::observability::Operation::Subscribe,
            terrabase::observability::AdmissionReason::Capacity,
        );
        return ApiError::abuse_guard(&rejection).into_response();
    }
    let permit = WsPermit { guards: Arc::clone(&state.guards), database: name.clone() };
    upgrade.on_upgrade(move |socket| serve_socket(socket, state, name, principal, permit))
}

struct WsPermit {
    guards: Arc<crate::guards::AbuseGuards>,
    database: String,
}

impl Drop for WsPermit {
    fn drop(&mut self) {
        self.guards.release_ws(&self.database);
    }
}

async fn serve_socket(
    mut socket: WebSocket,
    state: Arc<ApiState>,
    database: String,
    principal: crate::api_keys::Principal,
    _permit: WsPermit,
) {
    // Shared views outlive individual transports. Their quota stays charged
    // while their snapshots and logs remain in the store.
    // Keep the upgraded transport alive for successive subscribe frames. A
    // WebSocket is a sustained transport; one initial snapshot must not turn
    // it into an expensive one-shot request.
    loop {
        let Some(Ok(Message::Binary(bytes))) = socket.recv().await else {
            return;
        };
        let Ok(envelope) = terrabase_sync_core::wire::decode_envelope(&bytes) else {
            let _ = socket
                .send(Message::Binary(
                    error_bytes(terrabase_sync_core::wire::ErrorCode::MalformedFrame).into(),
                ))
                .await;
            return;
        };
        let terrabase_sync_core::wire::Message::Subscribe {
            subscription,
            query,
            parameters,
            checkpoint,
        } = envelope.message
        else {
            let _ = socket
                .send(Message::Binary(
                    error_bytes(terrabase_sync_core::wire::ErrorCode::MalformedFrame).into(),
                ))
                .await;
            return;
        };
        let request = AttachRequest {
            database_id: database.clone(),
            statement: query,
            parameters: encode_base64(&parameters),
            checkpoint: checkpoint.map(|value| encode_base64(&value)),
            subscription: Some(subscription),
            entitlement_epoch: 0,
        };
        let scope = format!("{}:{}:{}", state.scope.0, state.scope.1, database);
        let Ok(host) = crate::api::document_host(&state, &database) else {
            let _ = socket
                .send(Message::Binary(
                    error_bytes(terrabase_sync_core::wire::ErrorCode::PermissionDenied).into(),
                ))
                .await;
            return;
        };
        let Ok(materialization) = prepare_view(&state, &host, &request, &scope, Some(&principal))
        else {
            let _ = socket
                .send(Message::Binary(
                    error_bytes(terrabase_sync_core::wire::ErrorCode::PermissionDenied).into(),
                ))
                .await;
            return;
        };
        let Ok((response, view_id)) = store().attach_materialized(
            &request,
            &scope,
            state.region,
            Some(&materialization),
            &state.capability_keys,
            Some(&state.guards),
        ) else {
            let _ = socket
                .send(Message::Binary(
                    error_bytes(terrabase_sync_core::wire::ErrorCode::Unavailable).into(),
                ))
                .await;
            return;
        };
        if let Ok(snapshot) = store().read_snapshot(&view_id, 1, response.offset) {
            let _ = socket.send(Message::Binary(snapshot.bytes.to_vec().into())).await;
        }
    }
}

#[derive(Clone)]
enum ReadResult {
    Data { revision: u64, frozen: Frozen },
    Reset(Vec<u8>),
    Empty,
    Deferred(tokio::time::Instant),
}
/// Log query fields.
#[derive(Debug, Deserialize)]
pub struct OffsetQuery {
    /// Log offset.
    #[serde(default)]
    pub offset: Option<u64>,
}
/// Snapshot query fields.
#[derive(Debug, Deserialize)]
pub struct CutQuery {
    /// Snapshot cut.
    #[serde(default)]
    pub cut: Option<u64>,
}

fn immutable_response(frozen: &Frozen, etag: &str) -> Response {
    let remaining = frozen
        .created
        .elapsed()
        .ok()
        .map_or(0, |elapsed| RETENTION.saturating_sub(elapsed).as_secs());
    let mut response = (StatusCode::OK, frozen.bytes.to_vec()).into_response();
    let headers = response.headers_mut();
    headers.insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static(MSGPACK_MEDIA_TYPE));
    headers.insert("cache-control", HeaderValue::from_static("private, max-age=0"));
    if let Ok(value) = HeaderValue::from_str(&format!("public, max-age={remaining}, immutable")) {
        headers.insert("cloudflare-cdn-cache-control", value);
    }
    if let Ok(value) = HeaderValue::from_str(etag) {
        headers.insert(axum::http::header::ETAG, value);
    }
    headers.insert("x-content-type-options", HeaderValue::from_static("nosniff"));
    response
}

/// Runs the attach query exactly once against the Engine's immutable snapshot
/// and turns the result into the generated wire item shape. The same engine
/// snapshot sequence becomes the first view revision, tying the result to the
/// durable journal rather than a process-local revision-0 placeholder.
fn prepare_view(
    state: &ApiState,
    host: &std::sync::Arc<terrabase_tbep::member_host::MemberHost>,
    request: &AttachRequest,
    scope: &str,
    principal: Option<&crate::api_keys::Principal>,
) -> Result<Materialization, ApiError> {
    let statement = terrabase::SqlQueryText::try_new(request.statement.trim())
        .map_err(|_| sql_error("the statement is not valid read-only SQL"))?;
    let parameter_bytes =
        decode_base64(&request.parameters).map_err(|()| invalid_attach("parameters"))?;
    let parameters = view_parameters(&parameter_bytes)?;
    let deadline = terrabase::Deadline::after(Duration::from_secs(30))
        .map_err(|_| ApiError::query(terrabase::QueryError::Deadline))?;
    let cancellation = terrabase::Cancellation::new();
    let engine = host.engine();
    let changed = Arc::new(Notify::new());
    let plan = engine
        .queries()
        .compile(terrabase::QueryInput::sql_bounded_with_parameters(
            statement,
            deadline,
            cancellation,
            parameters,
        ))
        .map_err(ApiError::query)?;
    if let Some(principal) = principal
        && let Some(collection) = plan.source().collection()
        && !principal.scope().admits_collection(
            crate::api_keys::Verbs::READ,
            &request.database_id,
            collection.as_str(),
        )
    {
        return Err(ApiError::forbidden("read on the requested collection"));
    }
    if plan.sql_execute_only_operator().is_some() {
        return Err(ApiError {
            status: StatusCode::UNPROCESSABLE_ENTITY,
            code: "not_subscribable",
            message: "the SQL statement is execute-only".into(),
            fix: "subscribe a single-collection SELECT with scalar predicates, ORDER BY, LIMIT, or MATCH".into(),
            placement: None,
        });
    }
    let tenant = terrabase::types::TenantId::try_new(&state.scope.0)
        .map_err(|_| ApiError::storage_failure())?;
    let incremental = incremental::SqlMaterializer::for_plan(&plan)?;
    let mut identity = Vec::new();
    put_identity(&mut identity, b"database-incarnation", &engine.database_id().as_bytes());
    put_identity(
        &mut identity,
        b"normalized-sql",
        canonical_sql(request.statement.trim()).as_bytes(),
    );
    put_identity(&mut identity, b"parameters", &parameter_bytes);
    put_identity(&mut identity, b"schema-version", b"1");
    // Bind the compiled index requirements before the seed can create an
    // automatic index. Its build/usage lifecycle cannot change SQL semantics
    // or split an existing view's revision line on the next attachment.
    put_identity(&mut identity, b"index-plan-version", b"1");
    put_identity(
        &mut identity,
        b"index-requirements",
        format!("{:?}", plan.index_requirements()).as_bytes(),
    );
    // Explicit index definitions remain part of the catalog identity.
    let mut indexes = engine.queries().registered_indexes(&tenant);
    indexes.sort_by(|left, right| left.name().cmp(right.name()));
    for index in indexes
        .into_iter()
        .filter(|index| index.kind() == terrabase::query::optimizer::IndexKind::Explicit)
    {
        put_identity(&mut identity, b"index-name", index.name().as_bytes());
        put_identity(&mut identity, b"index-collection", index.collection().as_bytes());
        put_identity(
            &mut identity,
            b"index-definition",
            format!("{:?}:{:?}:{:?}", index.kind(), index.fields(), index.state()).as_bytes(),
        );
    }
    put_identity(&mut identity, b"rule-version", scope.as_bytes());
    put_identity(&mut identity, b"entitlement-version", &request.entitlement_epoch.to_be_bytes());
    put_identity(&mut identity, b"region", &state.region.as_bytes());
    Ok(Materialization {
        revision: 0,
        items: Vec::new(),
        digest: digest_items(&[]),
        identity,
        materializer: Some(Materializer {
            host: Arc::clone(host),
            plan,
            incremental,
            tenant,
            changed,
            watch: None,
        }),
    })
}

impl Materialization {
    fn initialize(&self) -> Result<Self, ApiError> {
        let mut value = self.clone();
        let Some(source) = &mut value.materializer else { return Ok(value) };
        let engine = source.host.engine();
        let signal = Arc::clone(&source.changed);
        source.watch = Some(Arc::new(
            engine
                .watch_publications(move || signal.notify_waiters())
                .map_err(|_| ApiError::storage_failure())?,
        ));
        let snapshot = engine
            .read_snapshot(&engine.scope_for(&source.tenant))
            .map_err(|_| ApiError::storage_failure())?;
        value.items = if let Some(incremental) = &mut source.incremental {
            incremental.seed(engine, &snapshot)?;
            incremental.items()
        } else {
            result_items(
                &engine.queries().execute(&source.plan, &snapshot).map_err(ApiError::query)?,
            )
        };
        value.revision = snapshot.sequence().get();
        value.digest = digest_items(&value.items);
        Ok(value)
    }
}

impl Materializer {
    fn refresh_items(
        &mut self,
        snapshot: &terrabase::types::ReadSnapshot,
        observed_sequence: u64,
    ) -> Result<Vec<terrabase_sync_core::wire::ResultItem>, ApiError> {
        let engine = self.host.engine();
        if let Some(incremental) = &mut self.incremental {
            incremental.refresh(
                engine,
                &engine.scope_for(&self.tenant),
                snapshot,
                observed_sequence,
            )
        } else {
            Ok(result_items(
                &engine
                    .queries()
                    .execute(
                        &self.plan.clone().with_execution_bounds(
                            view_refresh_deadline()?,
                            terrabase::Cancellation::new(),
                        ),
                        snapshot,
                    )
                    .map_err(ApiError::query)?,
            ))
        }
    }
}

fn view_refresh_deadline() -> Result<terrabase::Deadline, ApiError> {
    terrabase::Deadline::after(Duration::from_secs(30))
        .map_err(|_| ApiError::query(terrabase::QueryError::Deadline))
}

fn view_parameters(bytes: &[u8]) -> Result<terrabase::query::sql::SqlParameters, ApiError> {
    let mut parameters = terrabase::query::sql::SqlParameters::new();
    if !bytes.is_empty() {
        let value: serde_json::Value =
            serde_json::from_slice(bytes).map_err(|_| invalid_attach("parameters"))?;
        let Some(object) = value.as_object() else {
            return Err(invalid_attach("parameters"));
        };
        for (name, value) in object {
            parameters
                .insert(name, document_value(value).ok_or_else(|| invalid_attach("parameters"))?)
                .map_err(|_| invalid_attach("parameters"))?;
        }
    }
    Ok(parameters)
}

fn result_items(
    result: &terrabase::query::QueryResult,
) -> Vec<terrabase_sync_core::wire::ResultItem> {
    result.rows().iter().map(incremental::item).collect()
}

fn document_value(value: &serde_json::Value) -> Option<terrabase::types::DocumentValue> {
    Some(match value {
        serde_json::Value::Null => terrabase::types::DocumentValue::Null,
        serde_json::Value::Bool(value) => terrabase::types::DocumentValue::Boolean(*value),
        serde_json::Value::Number(value) => value
            .as_i64()
            .map(terrabase::types::DocumentValue::Integer)
            .or_else(|| value.as_f64().map(terrabase::types::DocumentValue::Number))?,
        serde_json::Value::String(value) => terrabase::types::DocumentValue::String(value.clone()),
        serde_json::Value::Array(values) => terrabase::types::DocumentValue::Array(
            values.iter().map(document_value).collect::<Option<Vec<_>>>()?,
        ),
        serde_json::Value::Object(values) => terrabase::types::DocumentValue::Object(
            values
                .iter()
                .map(|(key, value)| Some((key.clone(), document_value(value)?)))
                .collect::<Option<BTreeMap<_, _>>>()?,
        ),
    })
}

fn digest_items(items: &[terrabase_sync_core::wire::ResultItem]) -> String {
    let mut bytes = Vec::new();
    for item in items {
        put_identity(&mut bytes, b"identity", item.identity.as_bytes());
        for field in &item.fields {
            put_identity(&mut bytes, field.path.as_bytes(), &field.value);
        }
    }
    digest_bytes(&bytes)
}

fn empty_timeout() -> Response {
    let mut response = StatusCode::NO_CONTENT.into_response();
    let headers = response.headers_mut();
    headers.insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static(MSGPACK_MEDIA_TYPE));
    headers.insert("cache-control", HeaderValue::from_static("no-store"));
    headers.insert("cloudflare-cdn-cache-control", HeaderValue::from_static("no-store"));
    response
}
fn reset_response(bytes: Vec<u8>) -> Response {
    let mut response = (StatusCode::OK, bytes).into_response();
    let headers = response.headers_mut();
    headers.insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static(MSGPACK_MEDIA_TYPE));
    headers.insert("cache-control", HeaderValue::from_static("no-store"));
    headers.insert("cloudflare-cdn-cache-control", HeaderValue::from_static("no-store"));
    response
}
fn verify_view_header(
    headers: &HeaderMap,
    view: &str,
    generation: u64,
    origin_proof: bool,
    capability_keys: &CapabilityKeyRing,
) -> Result<(), ApiError> {
    // A verified edge proof attests that the edge already checked the
    // capability and strips it before forwarding. Direct regional callers
    // still need the view capability below.
    if origin_proof && !headers.contains_key(VIEW_CAPABILITY_HEADER) {
        return Ok(());
    }
    let Some(raw) = headers.get(VIEW_CAPABILITY_HEADER).and_then(|value| value.to_str().ok())
    else {
        return Err(capability_error());
    };
    let expected = store().capability_claims(view, generation)?;
    capability_keys
        .verifier()
        .ok_or_else(capability_error)?
        .verify_claims(raw, &expected, now_seconds())
        .map(|_| ())
        .map_err(|_| capability_error())
}

/// Deterministically mints a D-14 capability for conformance tests.
#[must_use]
pub fn capability_for(
    view: &str,
    generation: u64,
    scope: &str,
    entitlement_epoch: u64,
    now: u64,
) -> String {
    let expiry = (now / CAPABILITY_BUCKET + 2) * CAPABILITY_BUCKET;
    let body =
        format!("v1.{view}.{generation}.{}.{}.{}", encode_scope(scope), entitlement_epoch, expiry);
    format!("{body}.{}", hmac_sha256_hex(CONFORMANCE_CAPABILITY_KEY, body.as_bytes()))
}

/// Mints the production capability, binding the serving Region into the
/// authenticated claim set. Kept separate from the historical fixture helper
/// above so generated conformance artifacts remain byte-stable.
fn capability_for_region(
    view: &str,
    generation: u64,
    scope: &str,
    entitlement_epoch: u64,
    region: terrabase::regions::RegionCode,
    now: u64,
    capability_keys: &CapabilityKeyRing,
) -> String {
    let Some(issuer) = capability_keys.issuer() else { return String::new() };
    issuer.issue(view, generation, scope, entitlement_epoch, region, now).unwrap_or_default()
}
fn encode_scope(scope: &str) -> String {
    if scope.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b':'))
    {
        scope.to_owned()
    } else {
        format!("b64:{}", encode_base64(scope.as_bytes()))
    }
}

fn capability_scope(scope: &str) -> String {
    format!("scope-{}", blake3::hash(scope.as_bytes()).to_hex())
}
fn checkpoint_bytes(view: &str, generation: u64, revision: u64) -> Vec<u8> {
    let mut bytes = b"terrascale.sync.v1\0".to_vec();
    bytes.extend_from_slice(&blake3::hash(view.as_bytes()).as_bytes()[..16]);
    bytes.extend_from_slice(&generation.to_be_bytes());
    bytes.extend_from_slice(&revision.to_be_bytes());
    bytes
}

fn checkpoint_bytes_with_identity(
    view: &str,
    generation: u64,
    revision: u64,
    identity: &str,
) -> Vec<u8> {
    let mut bytes = checkpoint_bytes(view, generation, revision);
    bytes.extend_from_slice(&blake3::hash(identity.as_bytes()).as_bytes()[..16]);
    bytes
}
fn snapshot_envelope(
    subscription: &str,
    generation: u64,
    revision: u64,
    checkpoint: Vec<u8>,
) -> Vec<u8> {
    let snapshot = terrabase_sync_core::wire::Snapshot {
        view_generation: generation,
        authorization_generation: 1,
        revision,
        items: Vec::new(),
        digest: digest_bytes(&[]),
        checkpoint,
    };
    terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
        features: 0,
        correlation: 0,
        causation: revision,
        message: terrabase_sync_core::wire::Message::Snapshot {
            subscription: subscription.to_owned(),
            snapshot,
        },
    })
    .expect("bounded empty snapshot encodes")
}

fn snapshot_envelope_with_items(
    subscription: &str,
    generation: u64,
    revision: u64,
    items: Vec<terrabase_sync_core::wire::ResultItem>,
    digest: String,
    checkpoint: Vec<u8>,
) -> Vec<u8> {
    terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
        features: 0,
        correlation: 0,
        causation: revision,
        message: terrabase_sync_core::wire::Message::Snapshot {
            subscription: subscription.to_owned(),
            snapshot: terrabase_sync_core::wire::Snapshot {
                view_generation: generation,
                authorization_generation: 1,
                revision,
                items,
                digest,
                checkpoint,
            },
        },
    })
    .expect("bounded materialized snapshot encodes")
}

/// Whitespace normalization for identity only. Quoted literals and comments
/// remain byte-preserving, preventing equivalent formatting from creating
/// separate materializers while never changing query semantics.
fn canonical_sql(statement: &str) -> String {
    let mut result = String::with_capacity(statement.len());
    let mut quoted = None;
    let mut pending_space = false;
    for character in statement.trim().chars() {
        if let Some(delimiter) = quoted {
            result.push(character);
            if character == delimiter {
                quoted = None;
            }
            continue;
        }
        if matches!(character, '\'' | '"' | '`') {
            if pending_space && !result.is_empty() {
                result.push(' ');
            }
            pending_space = false;
            result.push(character);
            quoted = Some(character);
        } else if character.is_whitespace() {
            pending_space = true;
        } else {
            if pending_space && !result.is_empty() {
                result.push(' ');
            }
            pending_space = false;
            result.push(character.to_ascii_lowercase());
        }
    }
    result
}

fn put_identity(out: &mut Vec<u8>, label: &[u8], value: &[u8]) {
    let label_len = u32::try_from(label.len()).unwrap_or(u32::MAX);
    let value_len = u64::try_from(value.len()).unwrap_or(u64::MAX);
    out.extend_from_slice(&label_len.to_be_bytes());
    out.extend_from_slice(label);
    out.extend_from_slice(&value_len.to_be_bytes());
    out.extend_from_slice(value);
}
fn reset_bytes(view: &View) -> Vec<u8> {
    let snapshot = terrabase_sync_core::wire::Snapshot {
        view_generation: view.generation,
        authorization_generation: view.entitlement_epoch,
        revision: view.head,
        items: Vec::new(),
        digest: digest_bytes(&[]),
        checkpoint: checkpoint_bytes_with_identity(
            &view.id,
            view.generation,
            view.head,
            &view.identity_digest,
        ),
    };
    terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
        features: 0,
        correlation: 0,
        causation: view.head,
        message: terrabase_sync_core::wire::Message::Reset {
            subscription: view.id.clone(),
            reason: terrabase_sync_core::wire::ResetReason::RetentionAdvanced,
            snapshot,
        },
    })
    .expect("bounded reset encodes")
}
fn error_bytes(code: terrabase_sync_core::wire::ErrorCode) -> Vec<u8> {
    terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
        features: 0,
        correlation: 0,
        causation: 0,
        message: terrabase_sync_core::wire::Message::Error { code, detail: None },
    })
    .expect("bounded error encodes")
}
fn digest_bytes(bytes: &[u8]) -> String {
    blake3::hash(bytes).to_hex().to_string()
}
fn now_seconds() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}
fn invalid_attach(field: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_reactive_attach",
        message: format!("the reactive attach {field} is invalid"),
        fix: "send databaseId, a bounded read-only SQL statement, and base64 parameters".into(),
        placement: None,
    }
}
fn sql_error(message: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_sql",
        message: message.into(),
        fix: "send one read-only SQLite SELECT in the subscribable live subset".into(),
        placement: None,
    }
}
fn capability_error() -> ApiError {
    ApiError {
        status: StatusCode::UNAUTHORIZED,
        code: "invalid_view_capability",
        message: "the view capability is missing, expired, or not bound to this view generation"
            .into(),
        fix: "attach the view again for a fresh capability".into(),
        placement: None,
    }
}
fn unknown_view() -> ApiError {
    ApiError {
        status: StatusCode::NOT_FOUND,
        code: "unknown_view",
        message: "the view generation is unknown or no longer retained".into(),
        fix: "POST the canonical SQL attach request for a fresh view".into(),
        placement: None,
    }
}
fn offset_gap() -> ApiError {
    ApiError {
        status: StatusCode::CONFLICT,
        code: "offset_gap",
        message: "the requested log offset is ahead of the immutable log".into(),
        fix: "re-attach for a fresh snapshot and log URL".into(),
        placement: None,
    }
}
fn invalid_offset() -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_reactive_offset",
        message: "log reads require a non-negative offset".into(),
        fix: "follow the offset in the attach response and increment it after each revision".into(),
        placement: None,
    }
}
fn frame_error() -> ApiError {
    ApiError {
        status: StatusCode::INTERNAL_SERVER_ERROR,
        code: "reactive_frame_encode_failed",
        message: "the canonical MessagePack frame could not be encoded".into(),
        fix: "retry the subscription".into(),
        placement: None,
    }
}

fn encode_base64(bytes: &[u8]) -> String {
    const A: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut output = String::new();
    for chunk in bytes.chunks(3) {
        let a = chunk[0];
        let b = *chunk.get(1).unwrap_or(&0);
        let c = *chunk.get(2).unwrap_or(&0);
        output.push(A[(a >> 2) as usize] as char);
        output.push(A[((a & 3) << 4 | b >> 4) as usize] as char);
        output.push(if chunk.len() > 1 {
            A[((b & 15) << 2 | c >> 6) as usize] as char
        } else {
            '='
        });
        output.push(if chunk.len() > 2 { A[(c & 63) as usize] as char } else { '=' });
    }
    output
}
fn decode_base64(input: &str) -> Result<Vec<u8>, ()> {
    if input.is_empty() {
        return Ok(Vec::new());
    }
    let values: Vec<u8> = input
        .bytes()
        .filter(|byte| !byte.is_ascii_whitespace())
        .map(|byte| match byte {
            b'A'..=b'Z' => Ok(byte - b'A'),
            b'a'..=b'z' => Ok(byte - b'a' + 26),
            b'0'..=b'9' => Ok(byte - b'0' + 52),
            b'+' => Ok(62),
            b'/' => Ok(63),
            b'=' => Ok(64),
            _ => Err(()),
        })
        .collect::<Result<_, _>>()?;
    if !values.len().is_multiple_of(4) {
        return Err(());
    }
    let mut output = Vec::new();
    for chunk in values.chunks(4) {
        if chunk[0] == 64 || chunk[1] == 64 || (chunk[2] == 64 && chunk[3] != 64) {
            return Err(());
        }
        output.push((chunk[0] << 2) | (chunk[1] >> 4));
        if chunk[2] != 64 {
            output.push((chunk[1] << 4) | (chunk[2] >> 2));
        }
        if chunk[3] != 64 {
            output.push((chunk[2] << 6) | chunk[3]);
        }
    }
    Ok(output)
}
fn constant_time_equal(left: &[u8], right: &[u8]) -> bool {
    aws_lc_rs::constant_time::verify_slices_are_equal(left, right).is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::http::{HeaderValue, Uri};

    #[test]
    fn retained_log_replay_does_not_wait_for_materialization() {
        let store = ViewStore::new();
        let (_, id) = store
            .attach(
                &AttachRequest {
                    database_id: "db".into(),
                    statement: "SELECT * FROM users".into(),
                    parameters: String::new(),
                    checkpoint: None,
                    subscription: None,
                    entitlement_epoch: 0,
                },
                "tenant",
            )
            .expect("attach");
        let bytes: Arc<[u8]> = Arc::from([1, 2, 3]);
        let (_, other) = store
            .attach(
                &AttachRequest {
                    database_id: "another-db".into(),
                    statement: "SELECT * FROM users".into(),
                    parameters: String::new(),
                    checkpoint: None,
                    subscription: None,
                    entitlement_epoch: 0,
                },
                "another-tenant",
            )
            .expect("unrelated view");
        {
            let mut views = store.views.lock().unwrap();
            let view = views.get_mut(&id).unwrap();
            view.floor = 1;
            view.head = 2;
            view.log.insert(2, Frozen { bytes: Arc::clone(&bytes), created: SystemTime::now() });
        }
        // Model a blocked materializer without needing a slow storage device.
        // Release it before asserting, so a regression fails rather than hangs.
        let refreshing = store.view_refresh_lock(&id).unwrap();
        let serial = refreshing.lock().unwrap();
        let (send, receive) = std::sync::mpsc::channel();
        std::thread::scope(|threads| {
            threads.spawn(|| {
                let results = (0..16).map(|_| store.read_log(&id, 1, 1)).collect::<Vec<_>>();
                let expired = store.read_log(&id, 1, 0);
                let generation = store.read_log(&id, 2, 1);
                let ahead = store.read_log(&id, 1, 3);
                let unrelated = store.read_log(&other, 1, 0);
                send.send((results, expired, generation, ahead, unrelated)).unwrap();
            });
            let outcome = receive.recv_timeout(Duration::from_secs(2));
            drop(serial);
            let (results, expired, generation, ahead, unrelated) =
                outcome.expect("retained reads must not acquire the materializer lock");
            for result in results {
                let ReadResult::Data { revision, frozen } = result.unwrap() else {
                    panic!("retained response");
                };
                assert_eq!(revision, 2);
                assert!(Arc::ptr_eq(&bytes, &frozen.bytes), "replay shares the encoded bytes");
            }
            assert!(matches!(expired.unwrap(), ReadResult::Reset(_)));
            assert_eq!(generation.err().unwrap().code, unknown_view().code);
            assert_eq!(ahead.err().unwrap().code, offset_gap().code);
            assert!(matches!(unrelated.unwrap(), ReadResult::Empty));
        });
    }

    #[test]
    fn retention_prunes_history_resets_old_readers_and_keeps_the_current_snapshot() {
        let store = ViewStore::new();
        let (_, id) = store
            .attach(
                &AttachRequest {
                    database_id: "db".into(),
                    statement: "SELECT * FROM users".into(),
                    parameters: String::new(),
                    checkpoint: None,
                    subscription: None,
                    entitlement_epoch: 0,
                },
                "tenant",
            )
            .expect("attach");
        let now = SystemTime::now();
        let expired = now - RETENTION - Duration::from_secs(1);
        {
            let mut views = store.views.lock().expect("views");
            let view = views.get_mut(&id).expect("view");
            view.head = 2;
            view.snapshot.get_mut(&0).expect("initial cut").created = expired;
            for (revision, created) in [(1, expired), (2, now)] {
                let frozen = Frozen {
                    bytes: Arc::from(vec![u8::try_from(revision).expect("small revision")]),
                    created,
                };
                view.log.insert(revision, frozen.clone());
                view.snapshot.insert(revision, frozen);
            }
        }
        assert!(matches!(store.read_log(&id, 1, 0).expect("expired reader"), ReadResult::Reset(_)));
        assert!(matches!(
            store.read_log(&id, 1, 1).expect("at replay floor"),
            ReadResult::Data { revision: 2, .. }
        ));
        assert!(store.read_snapshot(&id, 1, 0).is_err());
        assert!(store.read_snapshot(&id, 1, 1).is_err());
        let current = store.read_snapshot(&id, 1, 2).expect("current snapshot");
        assert_eq!(current.bytes.as_ref(), &[2]);
        {
            let mut views = store.views.lock().expect("views");
            let view = views.get_mut(&id).expect("view");
            assert_eq!(view.floor, 1);
            assert_eq!(view.snapshot.len(), 1);
            view.prune(now + RETENTION + Duration::from_secs(1));
            assert_eq!(view.floor, 2);
            assert!(view.log.is_empty());
            assert_eq!(view.snapshot.len(), 1, "idle views retain their latest snapshot");
            assert_eq!(view.snapshot[&2].bytes, current.bytes);
        }
    }

    #[test]
    fn poisoned_view_state_returns_errors_without_recovering_partial_state() {
        let store = ViewStore::new();
        let poisoned = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _views = store.views.lock().expect("initial lock");
            panic!("injected failure during a view mutation");
        }));
        assert!(poisoned.is_err());
        let request = AttachRequest {
            database_id: "db".into(),
            statement: "SELECT * FROM users".into(),
            parameters: String::new(),
            checkpoint: None,
            subscription: None,
            entitlement_epoch: 0,
        };
        let errors = [
            store.attach(&request, "tenant").err(),
            store.current_head("view").err(),
            store.read_snapshot("view", 1, 0).err(),
            store.read_log("view", 1, 0).err(),
            store.capability_claims("view", 1).err(),
            store
                .append(
                    "view",
                    1,
                    terrabase_sync_core::wire::Message::Error {
                        code: terrabase_sync_core::wire::ErrorCode::Unavailable,
                        detail: None,
                    },
                )
                .err(),
        ];
        for error in errors {
            assert_eq!(error.expect("poisoned state is unavailable").code, "storage_failure");
        }
        assert!(store.views.is_poisoned());
    }

    #[test]
    fn concurrent_admission_never_publishes_or_deletes_a_rejected_view() {
        let store = ViewStore::new();
        let guards = crate::guards::AbuseGuards::new();
        guards.set_plan(
            "db",
            crate::guards::Plan::Custom(crate::guards::AbuseLimits {
                read_query_rps: 100,
                direct_read_bandwidth_bytes_per_month: 1000,
                distinct_views: 1,
                ws_connections: 10,
                webhook_endpoints: 1,
            }),
        );
        let keys = CapabilityKeyRing::conformance();
        let barrier = std::sync::Barrier::new(16);
        let results = std::thread::scope(|threads| {
            let handles = (0..16)
                .map(|index| {
                    let store = &store;
                    let guards = &guards;
                    let keys = &keys;
                    let barrier = &barrier;
                    threads.spawn(move || {
                        let request = AttachRequest {
                            database_id: "db".into(),
                            statement: format!("SELECT * FROM users WHERE n = {}", index % 2),
                            parameters: String::new(),
                            checkpoint: None,
                            subscription: None,
                            entitlement_epoch: 0,
                        };
                        barrier.wait();
                        store.attach_materialized(
                            &request,
                            "tenant",
                            terrabase::regions::RegionCode::GRU,
                            None,
                            keys,
                            Some(guards),
                        )
                    })
                })
                .collect::<Vec<_>>();
            handles.into_iter().map(|handle| handle.join().expect("request")).collect::<Vec<_>>()
        });
        assert_eq!(results.iter().filter(|result| result.is_ok()).count(), 8);
        assert_eq!(guards.view_count("db"), 1);
        assert_eq!(store.views.lock().expect("views").len(), 1);
        for result in results {
            match result {
                Ok((response, view)) => {
                    assert!(
                        store.read_snapshot(&view, response.generation, response.offset).is_ok()
                    );
                }
                Err(error) => assert_eq!(error.code, "abuse_guard_exceeded"),
            }
        }
    }

    #[test]
    fn immutable_log_retries_verify_bytes_and_maximum_offset_does_not_overflow() {
        let store = ViewStore::new();
        let (_, view) = store
            .attach(
                &AttachRequest {
                    database_id: "db".into(),
                    statement: "SELECT * FROM users".into(),
                    parameters: String::new(),
                    checkpoint: None,
                    subscription: None,
                    entitlement_epoch: 0,
                },
                "tenant",
            )
            .expect("attach");
        let message = |detail| terrabase_sync_core::wire::Message::Error {
            code: terrabase_sync_core::wire::ErrorCode::PermissionDenied,
            detail: Some(detail),
        };
        store.append(&view, u64::MAX, message("original".into())).expect("append");
        store.append(&view, u64::MAX, message("original".into())).expect("identical retry");
        let original = store.views.lock().expect("views").get(&view).expect("view").log[&u64::MAX]
            .bytes
            .clone();
        let error =
            store.append(&view, u64::MAX, message("replacement".into())).expect_err("conflict");
        assert_eq!(error.code, "reactive_revision_conflict");
        assert!(matches!(store.read_log(&view, 1, u64::MAX).expect("at head"), ReadResult::Empty));
        let ReadResult::Data { frozen, .. } = store.read_log(&view, 1, 0).expect("original data")
        else {
            panic!("original log entry must remain");
        };
        assert_eq!(frozen.bytes, original);
        store.views.lock().expect("views").get_mut(&view).expect("view").log.clear();
        assert_eq!(
            store
                .append(&view, u64::MAX, message("original".into()))
                .expect_err("expired retry")
                .code,
            "reactive_revision_conflict"
        );
    }

    #[test]
    fn origin_proof_binds_log_target_and_rotation_overlap() {
        let current = b"origin-current-key-material";
        let previous = b"origin-previous-key-material";
        let keys = OriginProofKeyRing::new(current, Some(previous)).expect("valid key ring");
        let now = 1_800;
        let bucket = now / CAPABILITY_BUCKET * CAPABILITY_BUCKET;
        let target = "/api/v1/reactive/views/view-1/1/log?offset=0";
        let mut headers = HeaderMap::new();
        headers.insert(
            ORIGIN_PROOF_HEADER,
            HeaderValue::from_str(&origin_proof_for(&Method::GET, target, bucket, current))
                .expect("proof header"),
        );
        assert!(verify_origin_proof(
            &headers,
            &Method::GET,
            &target.parse::<Uri>().expect("target"),
            &keys,
            now,
        ));

        headers.insert(
            ORIGIN_PROOF_HEADER,
            HeaderValue::from_str(&origin_proof_for(
                &Method::GET,
                target,
                bucket.saturating_sub(CAPABILITY_BUCKET),
                previous,
            ))
            .expect("previous proof header"),
        );
        assert!(verify_origin_proof(
            &headers,
            &Method::GET,
            &target.parse::<Uri>().expect("target"),
            &keys,
            now,
        ));
    }
}
