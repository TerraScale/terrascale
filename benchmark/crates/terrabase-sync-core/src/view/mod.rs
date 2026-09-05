//! Production shared result views (D-14, D-38, D-46).
//!
//! A [`SharedView`] is the one result state for all clients whose planned
//! query and authorization are equivalent.  It owns no reader or
//! subscription state: snapshots and revisions are immutable, bounded
//! publications, and readers resume by `(generation, offset)` only.

#![allow(
    clippy::cast_possible_truncation,
    clippy::missing_errors_doc,
    clippy::missing_panics_doc,
    clippy::too_many_arguments
)]

use std::collections::{BTreeMap, VecDeque};
use std::fmt;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, Weak};
use std::time::{Duration, Instant};

use crate::wire::{self, Envelope, Message, ResetReason, ResultItem, ResultOperation};

mod delta;

/// The maximum result size for a live top-k view (D-46).
pub const MAX_TOP_K_ITEMS: usize = 2_000;
/// The maximum number of projected operations in one published revision.
pub const MAX_REVISION_OPERATIONS: usize = 1_024;
/// The maximum number of retained revisions in one view.
pub const MAX_RETAINED_REVISIONS: usize = 4_096;
/// The maximum number of chunks in one immutable snapshot.
pub const MAX_SNAPSHOT_CHUNKS: usize = 128;
/// The maximum encoded size of a snapshot chunk.
pub const MAX_SNAPSHOT_CHUNK_BYTES: usize = 256 * 1024;
/// Maximum encoded projected result bytes retained by one shared view.
pub const MAX_VIEW_BYTES: usize = 64 * 1024 * 1024;
/// The cadence at which data-bearing revisions may be emitted.
pub const DELTA_CADENCE: Duration = Duration::from_millis(100);
/// Maximum aggregate groups maintained by one shared view.
pub const MAX_AGGREGATE_GROUPS: usize = 2_048;
/// Maximum encoded aggregate state maintained by one shared view.
pub const MAX_AGGREGATE_BYTES: usize = 4 * 1024 * 1024;
/// Maximum simultaneous distinct `(generation, offset)` wait keys.
pub const MAX_SINGLEFLIGHT_KEYS: usize = 256;
/// Maximum size of one normalized query or authorization predicate.
pub const MAX_PLAN_TEXT_BYTES: usize = 262_144;
/// Maximum parameter bytes in a canonical view key.
pub const MAX_PARAMETER_BYTES: usize = wire::MAX_BINARY_BYTES;

/// A query's ordering component.  Ordering is part of a view identity.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct OrderKey {
    /// The selected field or expression's canonical path.
    pub field: String,
    /// Whether the ordering is descending.
    pub descending: bool,
    /// Whether null values sort after non-null values.
    pub nulls_last: bool,
}

/// The canonical identity of a shared result view.
///
/// Every field that can change membership, ordering, bytes, or authorization
/// is included.  In particular, authorization-equivalent clients have the
/// same `authorization_predicate`, rule revision, entitlement epoch, and
/// Region and therefore attach to the same registry entry.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SharedViewKey {
    /// Database identity including its incarnation.
    pub database_incarnation: String,
    /// Branch identity including its incarnation.
    pub branch_incarnation: String,
    /// Whitespace-normalized, parameterized SQL plan.
    pub normalized_sql: String,
    /// Canonical encoded SQL parameters.
    pub parameters: Vec<u8>,
    /// Projection paths in result order.
    pub projection: Vec<String>,
    /// Ordered result keys.
    pub order: Vec<OrderKey>,
    /// Top-k bound.
    pub limit: usize,
    /// Schema version used to compile the plan.
    pub schema_version: u64,
    /// Index-plan version used to compile the plan.
    pub index_plan_version: u64,
    /// Compiled authorization predicate.
    pub authorization_predicate: String,
    /// Authorization rule revision.
    pub rule_revision: u64,
    /// Entitlement epoch.
    pub entitlement_epoch: u64,
    /// Region where this view is maintained.
    pub region: String,
}

/// Errors raised while constructing a canonical view key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KeyError {
    /// A text component exceeded its protocol bound.
    TextTooLarge,
    /// Parameters exceeded the protocol bound.
    ParametersTooLarge,
    /// The top-k limit was zero or exceeded the hard cap.
    InvalidLimit,
    /// The key had more projection or order components than the bounded plan.
    TooManyPlanFields,
}

impl fmt::Display for KeyError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::TextTooLarge => "shared-view text component exceeds its bound",
            Self::ParametersTooLarge => "shared-view parameters exceed their bound",
            Self::InvalidLimit => "shared-view limit is outside the top-k bound",
            Self::TooManyPlanFields => "shared-view projection or order is too large",
        })
    }
}

impl std::error::Error for KeyError {}

impl SharedViewKey {
    /// Builds and validates a canonical view identity.
    pub fn new(
        database_incarnation: impl Into<String>,
        branch_incarnation: impl Into<String>,
        sql: impl AsRef<str>,
        parameters: Vec<u8>,
        projection: Vec<String>,
        order: Vec<OrderKey>,
        limit: usize,
        schema_version: u64,
        index_plan_version: u64,
        authorization_predicate: impl Into<String>,
        rule_revision: u64,
        entitlement_epoch: u64,
        region: impl Into<String>,
    ) -> Result<Self, KeyError> {
        if limit == 0 || limit > MAX_TOP_K_ITEMS {
            return Err(KeyError::InvalidLimit);
        }
        if parameters.len() > MAX_PARAMETER_BYTES {
            return Err(KeyError::ParametersTooLarge);
        }
        if projection.len() > 1_024 || order.len() > 1_024 {
            return Err(KeyError::TooManyPlanFields);
        }
        let key = Self {
            database_incarnation: database_incarnation.into(),
            branch_incarnation: branch_incarnation.into(),
            normalized_sql: normalize_sql(sql.as_ref()),
            parameters,
            projection,
            order,
            limit,
            schema_version,
            index_plan_version,
            authorization_predicate: normalize_sql(&authorization_predicate.into()),
            rule_revision,
            entitlement_epoch,
            region: region.into(),
        };
        if [
            &key.database_incarnation,
            &key.branch_incarnation,
            &key.normalized_sql,
            &key.authorization_predicate,
            &key.region,
        ]
        .iter()
        .any(|value| value.len() > MAX_PLAN_TEXT_BYTES)
            || key.projection.iter().any(|value| value.len() > 1_024)
            || key.order.iter().any(|value| value.field.len() > 1_024)
        {
            return Err(KeyError::TextTooLarge);
        }
        Ok(key)
    }

    /// Returns the canonical bytes used for stable identity and registry
    /// lookup diagnostics.  Length prefixes prevent concatenation ambiguity.
    #[must_use]
    pub fn canonical_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::new();
        push_text(&mut bytes, &self.database_incarnation);
        push_text(&mut bytes, &self.branch_incarnation);
        push_text(&mut bytes, &self.normalized_sql);
        push_bytes(&mut bytes, &self.parameters);
        bytes.extend_from_slice(&(self.projection.len() as u32).to_be_bytes());
        for value in &self.projection {
            push_text(&mut bytes, value);
        }
        bytes.extend_from_slice(&(self.order.len() as u32).to_be_bytes());
        for value in &self.order {
            push_text(&mut bytes, &value.field);
            bytes.push(u8::from(value.descending));
            bytes.push(u8::from(value.nulls_last));
        }
        bytes.extend_from_slice(&(self.limit as u64).to_be_bytes());
        bytes.extend_from_slice(&self.schema_version.to_be_bytes());
        bytes.extend_from_slice(&self.index_plan_version.to_be_bytes());
        push_text(&mut bytes, &self.authorization_predicate);
        bytes.extend_from_slice(&self.rule_revision.to_be_bytes());
        bytes.extend_from_slice(&self.entitlement_epoch.to_be_bytes());
        push_text(&mut bytes, &self.region);
        bytes
    }

    /// Returns a deterministic opaque id suitable for a wire subscription
    /// field.  It is not an authorization capability.
    #[must_use]
    pub fn stable_id(&self) -> String {
        hex_u64(fnv64(&self.canonical_bytes()))
    }
}

fn normalize_sql(sql: &str) -> String {
    let mut output = String::with_capacity(sql.len());
    let mut quote = None;
    let mut pending_space = false;
    let mut comment = false;
    let mut chars = sql.trim().chars().peekable();
    while let Some(character) = chars.next() {
        if comment {
            if character == '\n' {
                comment = false;
                pending_space = !output.is_empty();
            }
            continue;
        }
        if quote.is_none() && character == '-' && chars.peek() == Some(&'-') {
            chars.next();
            comment = true;
            continue;
        }
        if let Some(delimiter) = quote {
            if character == delimiter {
                // SQL escapes a quote by doubling it. Keep both bytes and
                // remain in the quoted token after the first one.
                if chars.peek() == Some(&delimiter) {
                    output.push(character);
                    output.push(chars.next().expect("peeked quote"));
                    continue;
                }
                quote = None;
            }
            output.push(character);
            continue;
        }
        if matches!(character, '\'' | '"' | '`' | '[') {
            quote = Some(if character == '[' { ']' } else { character });
            output.push(character);
            continue;
        }
        if character.is_whitespace() {
            pending_space = !output.is_empty();
            continue;
        }
        if pending_space {
            output.push(' ');
            pending_space = false;
        }
        // SQL keywords and unquoted identifiers are case-insensitive in the
        // SQLite dialect. Quoted identifiers/literals were handled above.
        output.extend(character.to_lowercase());
    }
    output.trim_end().to_owned()
}

fn push_text(output: &mut Vec<u8>, value: &str) {
    output.extend_from_slice(&(value.len() as u32).to_be_bytes());
    output.extend_from_slice(value.as_bytes());
}

fn push_bytes(output: &mut Vec<u8>, value: &[u8]) {
    output.extend_from_slice(&(value.len() as u64).to_be_bytes());
    output.extend_from_slice(value);
}

fn fnv64(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

fn hex_u64(value: u64) -> String {
    format!("{value:016x}")
}

/// A bounded aggregate state for a shared view.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct AggregateState {
    values: BTreeMap<String, Vec<u8>>,
    bytes: usize,
}

impl AggregateState {
    /// Creates an empty aggregate state.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Inserts or replaces one bounded aggregate group.
    pub fn insert(&mut self, group: impl Into<String>, value: Vec<u8>) -> Result<(), ViewError> {
        let group = group.into();
        let old = self.values.get(&group).map_or(0, Vec::len) + group.len();
        let new_bytes =
            self.bytes.saturating_sub(old).saturating_add(group.len()).saturating_add(value.len());
        if !self.values.contains_key(&group) && self.values.len() >= MAX_AGGREGATE_GROUPS {
            return Err(ViewError::AggregateLimit);
        }
        if new_bytes > MAX_AGGREGATE_BYTES {
            return Err(ViewError::AggregateLimit);
        }
        self.bytes = new_bytes;
        self.values.insert(group, value);
        Ok(())
    }

    /// Removes one aggregate group.
    pub fn remove(&mut self, group: &str) -> Option<Vec<u8>> {
        let value = self.values.remove(group)?;
        self.bytes = self.bytes.saturating_sub(group.len()).saturating_sub(value.len());
        Some(value)
    }

    /// Number of aggregate groups.
    #[must_use]
    pub fn len(&self) -> usize {
        self.values.len()
    }

    /// Reports whether no aggregate groups are present.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    /// Number of bytes retained by aggregate state.
    #[must_use]
    pub fn bytes(&self) -> usize {
        self.bytes
    }

    /// Reads one aggregate group.
    #[must_use]
    pub fn get(&self, group: &str) -> Option<&[u8]> {
        self.values.get(group).map(Vec::as_slice)
    }
}

/// Errors raised by a bounded shared view.
#[derive(Debug)]
pub enum ViewError {
    /// The result or operation count exceeded a bound.
    ResultLimit,
    /// An operation could not be applied to the current ordered result.
    InvalidOperation,
    /// A revision would exceed the immutable log bound.
    RevisionLimit,
    /// Aggregate state exceeded one of its bounds.
    AggregateLimit,
    /// A wire envelope could not be encoded.
    Wire(wire::WireError),
    /// Registry has reached its distinct-view bound.
    RegistryLimit,
}

impl fmt::Display for ViewError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::ResultLimit => "shared-view result exceeds its bound",
            Self::InvalidOperation => "invalid shared-view operation",
            Self::RevisionLimit => "shared-view revision log is full",
            Self::AggregateLimit => "shared-view aggregate state exceeds its bound",
            Self::Wire(_) => "shared-view wire encoding failed",
            Self::RegistryLimit => "shared-view registry is full",
        })
    }
}

impl std::error::Error for ViewError {}

impl From<wire::WireError> for ViewError {
    fn from(error: wire::WireError) -> Self {
        Self::Wire(error)
    }
}

/// An immutable snapshot chunk, encoded with the existing indexed wire codec.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SnapshotChunk {
    /// View generation represented by this chunk.
    pub generation: u64,
    /// Zero-based chunk number.
    pub index: usize,
    /// Total chunk count.
    pub total: usize,
    /// Revision represented by this chunk.
    pub revision: u64,
    /// Deterministic encoded envelope bytes.
    pub bytes: Arc<[u8]>,
    /// Strong deterministic `ETag` for `bytes`.
    pub etag: String,
}

/// One immutable revision in the shared delta log.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RevisionEntry {
    /// View generation represented by this revision.
    pub generation: u64,
    /// Revision immediately before this entry.
    pub from_revision: u64,
    /// Revision reached by this entry.
    pub to_revision: u64,
    /// Wire operations in canonical order.
    pub operations: Arc<[ResultOperation]>,
    /// Deterministic encoded envelope bytes.
    pub bytes: Arc<[u8]>,
    /// Strong deterministic `ETag` for `bytes`.
    pub etag: String,
    /// Opaque resume checkpoint after this revision.
    pub checkpoint: Arc<[u8]>,
}

/// A data-bearing poll outcome.  Empty outcomes are intentionally not bytes
/// and should be sent with `no-store` by the HTTP adapter.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PollResult {
    /// The first immutable revision after the requested offset.
    Data(RevisionEntry),
    /// The bounded wait elapsed without a new revision.
    Empty,
    /// Resume is no longer valid and a fresh snapshot is required.
    Reset {
        /// Why replay was refused.
        reason: ResetReason,
        /// The current immutable snapshot chunks.
        snapshot: Vec<SnapshotChunk>,
    },
}

/// Why a resume request cannot replay the shared log.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ResumeDecision {
    /// Offset can be replayed from the current revision line.
    Continue,
    /// Caller must replace local state from a snapshot.
    Reset(ResetReason),
}

#[derive(Default)]
struct WakeState {
    generation: u64,
    offset: u64,
}

/// Shared immutable-publication view state.
pub struct SharedView {
    key: SharedViewKey,
    view_id: String,
    inner: Mutex<ViewInner>,
    wake: Condvar,
    flights: Mutex<BTreeMap<(u64, u64), Arc<SingleFlight>>>,
    clients: AtomicU64,
    aggregate: Mutex<AggregateState>,
}

struct SingleFlight {
    ready: Mutex<bool>,
    wake: Condvar,
}

struct ViewInner {
    generation: u64,
    authorization_generation: u64,
    revision: u64,
    retention_generation: u64,
    oldest_revision: u64,
    retention: usize,
    items: Vec<ResultItem>,
    log: VecDeque<RevisionEntry>,
    snapshots: Vec<SnapshotChunk>,
    pending: Vec<ResultOperation>,
    pending_since: Option<Instant>,
    last_emission: Option<Instant>,
    wake: WakeState,
}

impl SharedView {
    /// Creates an empty shared view at generation one.
    #[must_use]
    pub fn new(key: SharedViewKey) -> Self {
        let view_id = key.stable_id();
        Self {
            key,
            view_id,
            inner: Mutex::new(ViewInner {
                generation: 1,
                authorization_generation: 1,
                revision: 0,
                retention_generation: 1,
                oldest_revision: 0,
                retention: MAX_RETAINED_REVISIONS,
                items: Vec::new(),
                log: VecDeque::new(),
                snapshots: Vec::new(),
                pending: Vec::new(),
                pending_since: None,
                last_emission: None,
                wake: WakeState::default(),
            }),
            wake: Condvar::new(),
            flights: Mutex::new(BTreeMap::new()),
            clients: AtomicU64::new(0),
            aggregate: Mutex::new(AggregateState::new()),
        }
    }

    /// Canonical identity of this view.
    #[must_use]
    pub fn key(&self) -> &SharedViewKey {
        &self.key
    }

    /// Opaque stable view id.
    #[must_use]
    pub fn id(&self) -> &str {
        &self.view_id
    }

    /// Number of attached clients (not number of readers waiting on a poll).
    #[must_use]
    pub fn refcount(&self) -> u64 {
        self.clients.load(Ordering::Acquire)
    }

    fn attach(&self) {
        self.clients.fetch_add(1, Ordering::AcqRel);
    }
    fn detach(&self) {
        self.clients.fetch_sub(1, Ordering::AcqRel);
    }

    /// Sets the immutable snapshot and resets the revision line to zero.
    pub fn install_snapshot(
        &self,
        items: Vec<ResultItem>,
    ) -> Result<Vec<SnapshotChunk>, ViewError> {
        let mut inner = self.inner.lock().expect("shared view mutex is not poisoned");
        validate_items(&items, self.key.limit)?;
        inner.items = items;
        inner.revision = 0;
        inner.oldest_revision = 0;
        inner.log.clear();
        inner.pending.clear();
        inner.pending_since = None;
        let chunks = encode_snapshot(&self.view_id, &inner)?;
        inner.snapshots.clone_from(&chunks);
        inner.wake.offset = 0;
        inner.wake.generation = inner.generation;
        self.wake.notify_all();
        self.notify_waiters(inner.generation, inner.revision);
        Ok(chunks)
    }

    /// Changes the view generation and authorization generation, requiring a
    /// new snapshot before any old checkpoint can resume.
    pub fn advance_generation(
        &self,
        authorization_generation: u64,
    ) -> Result<Vec<SnapshotChunk>, ViewError> {
        let mut inner = self.inner.lock().expect("shared view mutex is not poisoned");
        inner.generation = inner.generation.saturating_add(1);
        inner.authorization_generation = authorization_generation;
        inner.revision = 0;
        inner.oldest_revision = 0;
        inner.retention_generation = inner.retention_generation.saturating_add(1);
        inner.log.clear();
        inner.pending.clear();
        inner.pending_since = None;
        let chunks = encode_snapshot(&self.view_id, &inner)?;
        inner.snapshots.clone_from(&chunks);
        inner.wake.generation = inner.generation;
        inner.wake.offset = 0;
        self.wake.notify_all();
        self.notify_waiters(inner.generation, inner.revision);
        Ok(chunks)
    }

    /// Queues operations for the next 100 ms cadence window.
    pub fn enqueue(&self, operations: Vec<ResultOperation>, now: Instant) -> Result<(), ViewError> {
        if operations.len() > MAX_REVISION_OPERATIONS {
            return Err(ViewError::RevisionLimit);
        }
        let mut inner = self.inner.lock().expect("shared view mutex is not poisoned");
        if inner.pending.len().saturating_add(operations.len()) > MAX_REVISION_OPERATIONS {
            return Err(ViewError::RevisionLimit);
        }
        inner.pending.extend(operations);
        inner.pending_since.get_or_insert(now);
        Ok(())
    }

    /// Publishes queued operations when their cadence window is due.
    pub fn flush_due(&self, now: Instant) -> Result<Option<RevisionEntry>, ViewError> {
        let mut inner = self.inner.lock().expect("shared view mutex is not poisoned");
        let Some(since) = inner.pending_since else { return Ok(None) };
        if now.duration_since(since) < DELTA_CADENCE
            || inner.last_emission.is_some_and(|last| now.duration_since(last) < DELTA_CADENCE)
        {
            return Ok(None);
        }
        let operations = std::mem::take(&mut inner.pending);
        inner.pending_since = None;
        let entry = publish_locked(&self.view_id, self.key.limit, &mut inner, operations)?;
        inner.last_emission = Some(now);
        self.wake.notify_all();
        self.notify_waiters(entry.generation, entry.to_revision);
        Ok(Some(entry))
    }

    /// Publishes operations immediately.  Hosts should prefer [`Self::enqueue`]
    /// and [`Self::flush_due`] for write-triggered delivery.
    pub fn publish(&self, operations: Vec<ResultOperation>) -> Result<RevisionEntry, ViewError> {
        let mut inner = self.inner.lock().expect("shared view mutex is not poisoned");
        let entry = publish_locked(&self.view_id, self.key.limit, &mut inner, operations)?;
        self.wake.notify_all();
        self.notify_waiters(entry.generation, entry.to_revision);
        Ok(entry)
    }

    /// Returns a copy of the current immutable snapshot chunks.
    #[must_use]
    pub fn snapshot_chunks(&self) -> Vec<SnapshotChunk> {
        self.inner.lock().expect("shared view mutex is not poisoned").snapshots.clone()
    }

    /// Returns the current generation and revision.
    #[must_use]
    pub fn position(&self) -> (u64, u64) {
        let inner = self.inner.lock().expect("shared view mutex is not poisoned");
        (inner.generation, inner.revision)
    }

    /// Returns the retained revision count.
    #[must_use]
    pub fn retained_revisions(&self) -> usize {
        self.inner.lock().expect("shared view mutex is not poisoned").log.len()
    }

    /// Returns the current opaque checkpoint for a resume request.
    #[must_use]
    pub fn checkpoint(&self) -> Vec<u8> {
        let inner = self.inner.lock().expect("shared view mutex is not poisoned");
        checkpoint(inner.generation, inner.revision, inner.retention_generation)
    }

    /// Number of currently coalesced wait keys, useful for admission metrics.
    #[must_use]
    pub fn singleflight_keys(&self) -> usize {
        self.flights.lock().expect("singleflight mutex is not poisoned").len()
    }

    /// Sets a lower retention bound, immediately dropping older immutable
    /// entries and invalidating checkpoints that point into the dropped gap.
    pub fn set_retention(&self, revisions: usize) -> Result<(), ViewError> {
        if revisions == 0 || revisions > MAX_RETAINED_REVISIONS {
            return Err(ViewError::RevisionLimit);
        }
        let mut inner = self.inner.lock().expect("shared view mutex is not poisoned");
        inner.retention = revisions;
        trim_log(&mut inner);
        Ok(())
    }

    /// Validates a generation/offset/checkpoint resume request.
    #[must_use]
    pub fn validate_resume(
        &self,
        generation: u64,
        offset: u64,
        checkpoint: &[u8],
    ) -> ResumeDecision {
        let inner = self.inner.lock().expect("shared view mutex is not poisoned");
        validate_resume_locked(&inner, generation, offset, checkpoint)
    }

    /// Polls for the first revision after `offset`, waiting on one shared
    /// condition variable.  No reader-specific buffers or tasks are created.
    pub fn poll(
        &self,
        generation: u64,
        offset: u64,
        checkpoint: &[u8],
        timeout: Duration,
    ) -> PollResult {
        let deadline = Instant::now().checked_add(timeout);
        let mut guard = self.inner.lock().expect("shared view mutex is not poisoned");
        loop {
            match validate_resume_locked(&guard, generation, offset, checkpoint) {
                ResumeDecision::Reset(reason) => {
                    return PollResult::Reset { reason, snapshot: guard.snapshots.clone() };
                }
                ResumeDecision::Continue => {}
            }
            if let Some(entry) = guard.log.iter().find(|entry| entry.to_revision > offset) {
                return PollResult::Data(entry.clone());
            }
            let Some(deadline) = deadline else { return PollResult::Empty };
            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else {
                return PollResult::Empty;
            };
            if remaining.is_zero() {
                return PollResult::Empty;
            }
            let flight = {
                let mut flights = self.flights.lock().expect("singleflight mutex is not poisoned");
                if let Some(flight) = flights.get(&(generation, offset)) {
                    Some(flight.clone())
                } else if flights.len() < MAX_SINGLEFLIGHT_KEYS {
                    let flight =
                        Arc::new(SingleFlight { ready: Mutex::new(false), wake: Condvar::new() });
                    flights.insert((generation, offset), flight.clone());
                    Some(flight)
                } else {
                    None
                }
            };
            if let Some(flight) = flight {
                drop(guard);
                let ready = flight.ready.lock().expect("singleflight mutex is not poisoned");
                let (_ready, result) = flight
                    .wake
                    .wait_timeout(ready, remaining)
                    .expect("singleflight mutex is not poisoned");
                {
                    let mut flights =
                        self.flights.lock().expect("singleflight mutex is not poisoned");
                    if flights
                        .get(&(generation, offset))
                        .is_some_and(|current| Arc::ptr_eq(current, &flight))
                    {
                        flights.remove(&(generation, offset));
                    }
                }
                guard = self.inner.lock().expect("shared view mutex is not poisoned");
                if result.timed_out() {
                    return PollResult::Empty;
                }
            } else {
                let (next, result) = self
                    .wake
                    .wait_timeout(guard, remaining)
                    .expect("shared view mutex is not poisoned");
                guard = next;
                if result.timed_out() {
                    return PollResult::Empty;
                }
            }
        }
    }

    fn notify_waiters(&self, generation: u64, revision: u64) {
        let flights = self.flights.lock().expect("singleflight mutex is not poisoned");
        for ((flight_generation, offset), flight) in flights.iter() {
            if *flight_generation == generation && *offset < revision {
                *flight.ready.lock().expect("singleflight mutex is not poisoned") = true;
                flight.wake.notify_all();
            }
        }
    }

    /// Adds or replaces one bounded aggregate group.
    pub fn aggregate_insert(
        &self,
        group: impl Into<String>,
        value: Vec<u8>,
    ) -> Result<(), ViewError> {
        self.aggregate.lock().expect("aggregate mutex is not poisoned").insert(group, value)
    }

    /// Returns aggregate-state bounds and current usage.
    #[must_use]
    pub fn aggregate_usage(&self) -> (usize, usize) {
        let aggregate = self.aggregate.lock().expect("aggregate mutex is not poisoned");
        (aggregate.len(), aggregate.bytes())
    }
}

fn validate_items(items: &[ResultItem], limit: usize) -> Result<(), ViewError> {
    if items.len() > limit || items.len() > MAX_TOP_K_ITEMS {
        return Err(ViewError::ResultLimit);
    }
    let mut bytes = 0usize;
    for item in items {
        if item.identity.len() > 256 || item.fields.len() > 1_024 {
            return Err(ViewError::ResultLimit);
        }
        bytes = bytes.saturating_add(item.identity.len());
        for field in &item.fields {
            if field.path.is_empty()
                || field.path.len() > 256
                || field.value.len() > wire::MAX_BINARY_BYTES
            {
                return Err(ViewError::ResultLimit);
            }
            bytes = bytes.saturating_add(field.path.len()).saturating_add(field.value.len());
        }
    }
    if bytes > MAX_VIEW_BYTES {
        return Err(ViewError::ResultLimit);
    }
    Ok(())
}

fn apply_operation(
    items: &mut Vec<ResultItem>,
    operation: &ResultOperation,
) -> Result<(), ViewError> {
    match operation {
        ResultOperation::Insert { identity, position, fields } => {
            let position = usize::try_from(*position).map_err(|_| ViewError::InvalidOperation)?;
            if items.len() >= MAX_TOP_K_ITEMS
                || items.iter().any(|item| item.identity == *identity)
                || position > items.len()
            {
                return Err(ViewError::InvalidOperation);
            }
            items.insert(
                position,
                ResultItem { identity: identity.clone(), fields: fields.clone() },
            );
        }
        ResultOperation::Update { identity, fields } => {
            let item = items
                .iter_mut()
                .find(|item| item.identity == *identity)
                .ok_or(ViewError::InvalidOperation)?;
            item.fields.clone_from(fields);
        }
        ResultOperation::Patch { identity, changes } => {
            let item = items
                .iter_mut()
                .find(|item| item.identity == *identity)
                .ok_or(ViewError::InvalidOperation)?;
            for change in changes {
                if let Some(field) = item.fields.iter_mut().find(|field| field.path == change.path)
                {
                    if let Some(value) = &change.value {
                        field.value.clone_from(value);
                    } else {
                        item.fields.retain(|field| field.path != change.path);
                    }
                } else if let Some(value) = &change.value {
                    item.fields.push(crate::wire::ProjectedField {
                        path: change.path.clone(),
                        value: value.clone(),
                    });
                }
            }
            item.fields.sort_by(|left, right| left.path.cmp(&right.path));
        }
        ResultOperation::Remove { identity } => {
            let position = items
                .iter()
                .position(|item| item.identity == *identity)
                .ok_or(ViewError::InvalidOperation)?;
            items.remove(position);
        }
        ResultOperation::Move { identity, position } => {
            let position = usize::try_from(*position).map_err(|_| ViewError::InvalidOperation)?;
            let old = items
                .iter()
                .position(|item| item.identity == *identity)
                .ok_or(ViewError::InvalidOperation)?;
            if position >= items.len() {
                return Err(ViewError::InvalidOperation);
            }
            let item = items.remove(old);
            items.insert(position, item);
        }
    }
    Ok(())
}

fn publish_locked(
    view_id: &str,
    limit: usize,
    inner: &mut ViewInner,
    operations: Vec<ResultOperation>,
) -> Result<RevisionEntry, ViewError> {
    if operations.is_empty() || operations.len() > MAX_REVISION_OPERATIONS {
        return Err(if operations.is_empty() {
            ViewError::InvalidOperation
        } else {
            ViewError::RevisionLimit
        });
    }
    let mut next = inner.items.clone();
    for operation in &operations {
        apply_operation(&mut next, operation)?;
    }
    validate_items(&next, limit)?;
    // Retention advances before the new checkpoint is minted.  Thus the
    // checkpoint carried by the newest revision is immediately resumable,
    // even when publishing this revision evicts the oldest one.
    while inner.log.len() >= inner.retention {
        if let Some(entry) = inner.log.pop_front() {
            inner.oldest_revision = entry.to_revision;
        }
        inner.retention_generation = inner.retention_generation.saturating_add(1);
    }
    let from_revision = inner.revision;
    inner.revision = inner.revision.saturating_add(1);
    inner.items = next;
    let checkpoint = checkpoint(inner.generation, inner.revision, inner.retention_generation);
    let message = Message::Delta {
        subscription: view_id.to_owned(),
        view_generation: inner.generation,
        from_revision,
        to_revision: inner.revision,
        operations: operations.clone(),
        digest: digest_items(&inner.items),
        checkpoint: checkpoint.clone(),
    };
    let bytes = encode(view_id, message)?;
    let entry = RevisionEntry {
        generation: inner.generation,
        from_revision,
        to_revision: inner.revision,
        operations: Arc::from(operations),
        etag: etag(&bytes),
        bytes: Arc::from(bytes),
        checkpoint: Arc::from(checkpoint),
    };
    inner.log.push_back(entry.clone());
    // Replace the reset baseline. Existing chunk values remain immutable
    // because each publication owns its own encoded byte allocation.
    inner.snapshots = encode_snapshot(view_id, inner)?;
    inner.wake.generation = inner.generation;
    inner.wake.offset = inner.revision;
    Ok(entry)
}

fn trim_log(inner: &mut ViewInner) {
    while inner.log.len() > inner.retention {
        if let Some(entry) = inner.log.pop_front() {
            inner.oldest_revision = entry.to_revision;
        }
        inner.retention_generation = inner.retention_generation.saturating_add(1);
    }
}

fn encode_snapshot(view_id: &str, inner: &ViewInner) -> Result<Vec<SnapshotChunk>, ViewError> {
    let mut chunks = Vec::new();
    let mut current = Vec::new();
    for item in &inner.items {
        let mut candidate = current.clone();
        candidate.push(item.clone());
        let snapshot = snapshot_message(view_id, inner, candidate.clone());
        let encoded = encode(view_id, snapshot)?;
        if !current.is_empty() && encoded.len() > MAX_SNAPSHOT_CHUNK_BYTES {
            let snapshot = snapshot_message(view_id, inner, current.clone());
            let encoded = encode(view_id, snapshot)?;
            chunks.push((current, encoded));
            current = vec![item.clone()];
        } else {
            current = candidate;
        }
    }
    if current.is_empty() {
        let encoded = encode(view_id, snapshot_message(view_id, inner, Vec::new()))?;
        chunks.push((Vec::new(), encoded));
    } else {
        let encoded = encode(view_id, snapshot_message(view_id, inner, current.clone()))?;
        chunks.push((current, encoded));
    }
    // Chunk accounting is deterministic; each encoded byte allocation is
    // immutable once returned to the caller.
    let total = chunks.len();
    if total > MAX_SNAPSHOT_CHUNKS {
        return Err(ViewError::ResultLimit);
    }
    Ok(chunks
        .into_iter()
        .enumerate()
        .map(|(index, (_, bytes))| SnapshotChunk {
            generation: inner.generation,
            index,
            total,
            revision: inner.revision,
            etag: etag(&bytes),
            bytes: Arc::from(bytes),
        })
        .collect())
}

fn snapshot_message(view_id: &str, inner: &ViewInner, items: Vec<ResultItem>) -> Message {
    Message::Snapshot {
        subscription: view_id.to_owned(),
        snapshot: wire::Snapshot {
            view_generation: inner.generation,
            authorization_generation: inner.authorization_generation,
            revision: inner.revision,
            // Every chunk carries the complete-result digest; a client can
            // reassemble chunks and verify one view-wide content identity.
            digest: digest_items(&inner.items),
            checkpoint: checkpoint(inner.generation, inner.revision, inner.retention_generation),
            items,
        },
    }
}

fn digest_items(items: &[ResultItem]) -> String {
    let mut bytes = Vec::new();
    for item in items {
        push_text(&mut bytes, &item.identity);
        for field in &item.fields {
            push_text(&mut bytes, &field.path);
            push_bytes(&mut bytes, &field.value);
        }
    }
    hex_u64(fnv64(&bytes))
}

fn checkpoint(generation: u64, revision: u64, retention_generation: u64) -> Vec<u8> {
    let mut value = Vec::with_capacity(32);
    value.extend_from_slice(b"tsv1\0");
    value.extend_from_slice(&generation.to_be_bytes());
    value.extend_from_slice(&revision.to_be_bytes());
    value.extend_from_slice(&retention_generation.to_be_bytes());
    let integrity = fnv64(&value);
    value.extend_from_slice(&integrity.to_be_bytes());
    value
}

fn valid_checkpoint(
    value: &[u8],
    generation: u64,
    revision: u64,
    retention_generation: u64,
) -> bool {
    if value.len() != 37 || &value[..5] != b"tsv1\0" {
        return false;
    }
    let read = |offset| {
        u64::from_be_bytes(value[offset..offset + 8].try_into().expect("checkpoint length"))
    };
    read(5) == generation
        && read(13) == revision
        && read(21) == retention_generation
        && fnv64(&value[..29]) == read(29)
}

fn validate_resume_locked(
    inner: &ViewInner,
    generation: u64,
    offset: u64,
    checkpoint: &[u8],
) -> ResumeDecision {
    if generation != inner.generation {
        return ResumeDecision::Reset(ResetReason::GenerationChanged);
    }
    if offset < inner.oldest_revision {
        return ResumeDecision::Reset(ResetReason::CheckpointExpired);
    }
    if !valid_checkpoint(checkpoint, generation, offset, inner.retention_generation) {
        return ResumeDecision::Reset(ResetReason::InvalidCheckpoint);
    }
    ResumeDecision::Continue
}

fn encode(_view_id: &str, message: Message) -> Result<Vec<u8>, ViewError> {
    Ok(wire::encode_envelope(&Envelope { features: 0, correlation: 0, causation: 0, message })?)
}

fn etag(bytes: &[u8]) -> String {
    // Two independently seeded 64-bit lanes provide a stable 128-bit strong
    // validator without adding a hashing dependency to this tiny codec crate.
    format!("\"{:016x}{:016x}\"", fnv64(bytes), fnv64_seed(bytes, 0x8422_2325_0100_01b3))
}

fn fnv64_seed(bytes: &[u8], seed: u64) -> u64 {
    let mut hash = seed;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

/// Distinct shared-view registry.  Registry entries are removed as soon as
/// their last attached handle drops, so idle views do not retain result state.
pub struct SharedViewRegistry {
    inner: Arc<RegistryInner>,
}

struct RegistryInner {
    views: Mutex<BTreeMap<SharedViewKey, Arc<SharedView>>>,
    max_views: usize,
}

/// A reference-counted attachment to one shared view.
pub struct SharedViewHandle {
    view: Arc<SharedView>,
    registry: Weak<RegistryInner>,
    key: SharedViewKey,
    detached: AtomicBool,
}

impl Clone for SharedViewHandle {
    fn clone(&self) -> Self {
        self.view.attach();
        Self {
            view: Arc::clone(&self.view),
            registry: self.registry.clone(),
            key: self.key.clone(),
            detached: AtomicBool::new(false),
        }
    }
}

impl Drop for SharedViewHandle {
    fn drop(&mut self) {
        if self.detached.swap(true, Ordering::AcqRel) {
            return;
        }
        self.view.detach();
        if self.view.refcount() == 0
            && let Some(registry) = self.registry.upgrade()
        {
            let mut views = registry.views.lock().expect("view registry mutex is not poisoned");
            if views
                .get(&self.key)
                .is_some_and(|view| Arc::ptr_eq(view, &self.view) && view.refcount() == 0)
            {
                views.remove(&self.key);
            }
        }
    }
}

impl std::ops::Deref for SharedViewHandle {
    type Target = SharedView;
    fn deref(&self) -> &Self::Target {
        &self.view
    }
}

/// Registry errors.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RegistryError {
    /// The registry's distinct-view bound has been reached.
    Full,
}

impl Default for SharedViewRegistry {
    fn default() -> Self {
        Self::new(1_024)
    }
}

impl SharedViewRegistry {
    /// Creates a registry with a distinct-view bound.
    #[must_use]
    pub fn new(max_views: usize) -> Self {
        Self { inner: Arc::new(RegistryInner { views: Mutex::new(BTreeMap::new()), max_views }) }
    }

    /// Attaches to an existing equivalent view or creates it atomically.
    pub fn attach(&self, key: SharedViewKey) -> Result<SharedViewHandle, RegistryError> {
        let mut views = self.inner.views.lock().expect("view registry mutex is not poisoned");
        let view = if let Some(view) = views.get(&key) {
            Arc::clone(view)
        } else {
            if views.len() >= self.inner.max_views {
                return Err(RegistryError::Full);
            }
            let view = Arc::new(SharedView::new(key.clone()));
            views.insert(key.clone(), Arc::clone(&view));
            view
        };
        view.attach();
        Ok(SharedViewHandle {
            view,
            registry: Arc::downgrade(&self.inner),
            key,
            detached: AtomicBool::new(false),
        })
    }

    /// Number of distinct live registry entries.
    #[must_use]
    pub fn len(&self) -> usize {
        self.inner.views.lock().expect("view registry mutex is not poisoned").len()
    }

    /// Reports whether no shared view is registered.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Number of attached clients over all views.
    #[must_use]
    pub fn attached(&self) -> u64 {
        self.inner
            .views
            .lock()
            .expect("view registry mutex is not poisoned")
            .values()
            .map(|view| view.refcount())
            .sum()
    }
}

/// The small, transport-independent incremental plan used by live views.
///
/// This is deliberately separate from `SQLite`'s executor.  It describes only
/// the launch subset whose incremental form is linear (`filter`, `projection`,
/// FTS) or bounded (`ORDER BY … LIMIT`).  An execute-only statement is rejected
/// before it can create a shared view.
#[allow(missing_docs)]
pub mod plan {
    pub use super::delta::diff;
    use super::{MAX_TOP_K_ITEMS, OrderKey, ResultItem, SharedViewKey, normalize_sql};
    use std::cmp::Ordering;
    use std::collections::{BTreeMap, BTreeSet};
    use std::fmt;

    /// A typed value used by dependency ranges and live predicates.
    #[derive(Clone, Debug, PartialEq)]
    pub enum Value {
        Null,
        Bool(bool),
        Number(f64),
        Text(String),
        Bytes(Vec<u8>),
    }

    impl Eq for Value {}
    impl PartialOrd for Value {
        fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
            Some(self.cmp(other))
        }
    }
    impl Ord for Value {
        fn cmp(&self, other: &Self) -> Ordering {
            let rank = |value: &Self| match value {
                Self::Null => 0_u8,
                Self::Bool(_) => 1,
                Self::Number(_) => 2,
                Self::Text(_) => 3,
                Self::Bytes(_) => 4,
            };
            rank(self).cmp(&rank(other)).then_with(|| match (self, other) {
                (Self::Bool(left), Self::Bool(right)) => left.cmp(right),
                (Self::Number(left), Self::Number(right)) => left.total_cmp(right),
                (Self::Text(left), Self::Text(right)) => left.cmp(right),
                (Self::Bytes(left), Self::Bytes(right)) => left.cmp(right),
                _ => Ordering::Equal,
            })
        }
    }

    impl Value {
        fn bytes(&self) -> Vec<u8> {
            match self {
                Self::Null => b"null".to_vec(),
                Self::Bool(value) => value.to_string().into_bytes(),
                Self::Number(value) => value.to_string().into_bytes(),
                Self::Text(value) => value.as_bytes().to_vec(),
                Self::Bytes(value) => value.clone(),
            }
        }
    }

    /// An interval endpoint. Included and excluded endpoints are distinct.
    #[derive(Clone, Debug, Eq, PartialEq, Ord, PartialOrd)]
    pub enum RangeEnd {
        Unbounded,
        Included(Value),
        Excluded(Value),
    }

    /// A half-open/closed value interval used for dependency matching.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct ValueRange {
        pub start: RangeEnd,
        pub end: RangeEnd,
    }

    impl ValueRange {
        #[must_use]
        pub fn all() -> Self {
            Self { start: RangeEnd::Unbounded, end: RangeEnd::Unbounded }
        }
        #[must_use]
        pub fn equals(value: Value) -> Self {
            Self { start: RangeEnd::Included(value.clone()), end: RangeEnd::Included(value) }
        }
        #[must_use]
        pub fn contains(&self, value: &Value) -> bool {
            let lower = match &self.start {
                RangeEnd::Unbounded => true,
                RangeEnd::Included(bound) => value >= bound,
                RangeEnd::Excluded(bound) => value > bound,
            };
            let upper = match &self.end {
                RangeEnd::Unbounded => true,
                RangeEnd::Included(bound) => value <= bound,
                RangeEnd::Excluded(bound) => value < bound,
            };
            lower && upper
        }
    }

    /// A dependency on one physical/indexed value range.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct DependencyKey {
        pub collection: String,
        pub index_id: String,
        pub range: ValueRange,
    }

    /// Values extracted once by the write path for dependency matching.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct ExtractedRow {
        pub collection: String,
        pub identity: String,
        pub values: BTreeMap<String, Value>,
        pub fields: BTreeMap<String, Value>,
        pub fts: String,
    }

    impl ExtractedRow {
        #[must_use]
        pub fn new(collection: impl Into<String>, identity: impl Into<String>) -> Self {
            Self {
                collection: collection.into(),
                identity: identity.into(),
                values: BTreeMap::new(),
                fields: BTreeMap::new(),
                fts: String::new(),
            }
        }
        #[must_use]
        pub fn with_value(mut self, field: impl Into<String>, value: Value) -> Self {
            let field = field.into();
            self.values.insert(field.clone(), value.clone());
            self.fields.insert(field, value);
            self
        }
        #[must_use]
        pub fn with_fts(mut self, text: impl Into<String>) -> Self {
            self.fts = text.into();
            self
        }
    }

    /// The write-side index for `(collection, index_id, range)` dependencies.
    #[derive(Clone, Debug, Default)]
    pub struct DependencyIndex {
        entries: BTreeMap<(String, String), Vec<(ValueRange, u64)>>,
    }

    impl DependencyIndex {
        #[must_use]
        pub fn new() -> Self {
            Self::default()
        }
        pub fn register(&mut self, owner: u64, dependency: DependencyKey) {
            self.entries
                .entry((dependency.collection, dependency.index_id))
                .or_default()
                .push((dependency.range, owner));
        }
        pub fn retract(&mut self, owner: u64) {
            self.entries.retain(|_, values| {
                values.retain(|(_, candidate)| *candidate != owner);
                !values.is_empty()
            });
        }
        #[must_use]
        pub fn match_row(&self, row: &ExtractedRow) -> Vec<u64> {
            let mut matched = BTreeSet::new();
            for ((collection, index), ranges) in &self.entries {
                if collection != &row.collection {
                    continue;
                }
                let Some(value) = row.values.get(index) else { continue };
                for (range, owner) in ranges {
                    if range.contains(value) {
                        matched.insert(*owner);
                    }
                }
            }
            matched.into_iter().collect()
        }
        #[must_use]
        pub fn len(&self) -> usize {
            self.entries.values().map(Vec::len).sum()
        }
        #[must_use]
        pub fn is_empty(&self) -> bool {
            self.entries.is_empty()
        }
    }

    /// Scalar predicate supported by an incremental filter.
    #[derive(Clone, Copy, Debug, Eq, PartialEq)]
    pub enum Compare {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    /// One scalar predicate over an extracted field.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct Predicate {
        pub field: String,
        pub compare: Compare,
        pub value: Value,
    }

    impl Predicate {
        #[must_use]
        pub fn matches(&self, row: &ExtractedRow) -> bool {
            let Some(actual) = row.values.get(&self.field) else { return false };
            // SQL comparisons with NULL are unknown, so WHERE rejects them.
            // Explicit IS NULL / IS NOT NULL require distinct operators.
            if matches!(actual, Value::Null) || matches!(self.value, Value::Null) {
                return false;
            }
            let ordering = actual.cmp(&self.value);
            match self.compare {
                Compare::Equal => ordering == Ordering::Equal,
                Compare::NotEqual => ordering != Ordering::Equal,
                Compare::Less => ordering == Ordering::Less,
                Compare::LessOrEqual => ordering != Ordering::Greater,
                Compare::Greater => ordering == Ordering::Greater,
                Compare::GreaterOrEqual => ordering != Ordering::Less,
            }
        }
        #[must_use]
        pub fn dependency(&self, collection: &str, index_id: impl Into<String>) -> DependencyKey {
            let range = match self.compare {
                Compare::Equal => ValueRange::equals(self.value.clone()),
                Compare::NotEqual => ValueRange::all(),
                Compare::Less => ValueRange {
                    start: RangeEnd::Unbounded,
                    end: RangeEnd::Excluded(self.value.clone()),
                },
                Compare::LessOrEqual => ValueRange {
                    start: RangeEnd::Unbounded,
                    end: RangeEnd::Included(self.value.clone()),
                },
                Compare::Greater => ValueRange {
                    start: RangeEnd::Excluded(self.value.clone()),
                    end: RangeEnd::Unbounded,
                },
                Compare::GreaterOrEqual => ValueRange {
                    start: RangeEnd::Included(self.value.clone()),
                    end: RangeEnd::Unbounded,
                },
            };
            DependencyKey { collection: collection.to_owned(), index_id: index_id.into(), range }
        }
    }

    /// A full-text predicate evaluated against the maintained FTS delta.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct FtsPredicate {
        pub query: String,
    }
    impl FtsPredicate {
        #[must_use]
        pub fn matches(&self, row: &ExtractedRow) -> bool {
            let haystack = row.fts.to_ascii_lowercase();
            self.query.split_whitespace().all(|term| haystack.contains(&term.to_ascii_lowercase()))
        }
    }

    /// A live plan: one collection, scalar conjunction, projection, FTS, and
    /// an optional bounded ordered window.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct ViewPlan {
        pub collection: String,
        pub predicates: Vec<Predicate>,
        pub projection: Vec<String>,
        pub fts: Option<FtsPredicate>,
        pub order: Vec<OrderKey>,
        pub limit: usize,
    }

    /// The operator that made a plan execute-only.
    #[derive(Clone, Copy, Debug, Eq, PartialEq)]
    pub enum UnsupportedOperator {
        Join,
        Aggregate,
        Window,
        Subquery,
        MultiCollection,
        Unknown,
    }

    impl fmt::Display for UnsupportedOperator {
        fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.write_str(match self {
                Self::Join => "join",
                Self::Aggregate => "aggregate",
                Self::Window => "window",
                Self::Subquery => "subquery",
                Self::MultiCollection => "multi_collection",
                Self::Unknown => "unsupported",
            })
        }
    }

    /// Errors from planning a subscribable view.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub enum PlanError {
        Empty,
        Invalid,
        Unsupported(UnsupportedOperator),
        InvalidLimit,
        TooLarge,
    }

    impl fmt::Display for PlanError {
        fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            match self {
                Self::Empty => f.write_str("empty view plan"),
                Self::Invalid => f.write_str("invalid live view plan"),
                Self::Unsupported(op) => write!(f, "execute-only plan: {op}"),
                Self::InvalidLimit => f.write_str("view limit is outside the top-k bound"),
                Self::TooLarge => f.write_str("view plan exceeds its bound"),
            }
        }
    }
    impl std::error::Error for PlanError {}

    impl ViewPlan {
        /// Validates a manually lowered live plan.
        pub fn new(
            collection: impl Into<String>,
            predicates: Vec<Predicate>,
            projection: Vec<String>,
            fts: Option<FtsPredicate>,
            order: Vec<OrderKey>,
            limit: usize,
        ) -> Result<Self, PlanError> {
            let plan =
                Self { collection: collection.into(), predicates, projection, fts, order, limit };
            if plan.collection.is_empty()
                || plan.collection.len() > super::MAX_PLAN_TEXT_BYTES
                || plan.projection.iter().any(|field| field.is_empty() || field.len() > 1_024)
                || plan.predicates.len() > 1_024
                || plan.order.len() > 1_024
            {
                return Err(PlanError::TooLarge);
            }
            if limit == 0 || limit > MAX_TOP_K_ITEMS {
                return Err(PlanError::InvalidLimit);
            }
            Ok(plan)
        }

        /// Returns the row's projected result, or `None` when it is filtered.
        #[must_use]
        pub fn evaluate(&self, row: &ExtractedRow) -> Option<ResultItem> {
            if !self.matches(row) {
                return None;
            }
            let fields = if self.projection.is_empty() {
                row.fields
                    .iter()
                    .map(|(path, value)| crate::wire::ProjectedField {
                        path: path.clone(),
                        value: value.bytes(),
                    })
                    .collect()
            } else {
                self.projection
                    .iter()
                    .filter_map(|path| {
                        row.fields.get(path).map(|value| crate::wire::ProjectedField {
                            path: path.clone(),
                            value: value.bytes(),
                        })
                    })
                    .collect()
            };
            Some(ResultItem { identity: row.identity.clone(), fields })
        }

        fn matches(&self, row: &ExtractedRow) -> bool {
            row.collection == self.collection
                && self.predicates.iter().all(|predicate| predicate.matches(row))
                && self.fts.as_ref().is_none_or(|fts| fts.matches(row))
        }

        /// Creates the canonical shared-view identity for this plan.
        pub fn key(
            &self,
            database: impl Into<String>,
            branch: impl Into<String>,
            parameters: Vec<u8>,
            schema: u64,
            index_plan: u64,
            authorization: impl Into<String>,
            rule: u64,
            entitlement: u64,
            region: impl Into<String>,
        ) -> Result<SharedViewKey, super::KeyError> {
            let sql = format!(
                "select {} from {}",
                if self.projection.is_empty() { "*".to_owned() } else { self.projection.join(",") },
                self.collection
            );
            SharedViewKey::new(
                database,
                branch,
                sql,
                parameters,
                self.projection.clone(),
                self.order.clone(),
                self.limit,
                schema,
                index_plan,
                authorization,
                rule,
                entitlement,
                region,
            )
        }
    }

    /// One row with a signed Z-set multiplicity.
    #[derive(Clone, Debug, Eq, PartialEq)]
    pub struct ZSetEntry {
        pub row: ExtractedRow,
        pub weight: i32,
    }
    /// A batch of inserts/removals. A row's transition is represented by -1
    /// for the old value and +1 for the new value.
    #[derive(Clone, Debug, Eq, PartialEq, Default)]
    pub struct ZSetDelta {
        pub entries: Vec<ZSetEntry>,
    }
    impl ZSetDelta {
        #[must_use]
        pub fn new(entries: Vec<ZSetEntry>) -> Self {
            Self { entries }
        }
        #[must_use]
        pub fn insert(row: ExtractedRow) -> Self {
            Self { entries: vec![ZSetEntry { row, weight: 1 }] }
        }
        #[must_use]
        pub fn remove(row: ExtractedRow) -> Self {
            Self { entries: vec![ZSetEntry { row, weight: -1 }] }
        }
        #[must_use]
        pub fn transition(old: Option<ExtractedRow>, new: Option<ExtractedRow>) -> Self {
            let mut entries = Vec::with_capacity(2);
            if let Some(row) = old {
                entries.push(ZSetEntry { row, weight: -1 });
            }
            if let Some(row) = new {
                entries.push(ZSetEntry { row, weight: 1 });
            }
            Self { entries }
        }
    }

    /// A bounded incremental result materializer. Top-k plans retain at most
    /// `2 * limit` boundary candidates; unbounded plans retain at most `limit`.
    #[derive(Clone, Debug)]
    pub struct Materializer {
        plan: ViewPlan,
        rows: BTreeMap<String, ExtractedRow>,
        result: Vec<ResultItem>,
        boundary: usize,
    }

    impl Materializer {
        #[must_use]
        pub fn new(plan: ViewPlan) -> Self {
            let boundary = plan.limit.saturating_mul(2).min(MAX_TOP_K_ITEMS);
            Self { plan, rows: BTreeMap::new(), result: Vec::new(), boundary }
        }
        pub fn seed(&mut self, rows: impl IntoIterator<Item = ExtractedRow>) {
            self.rows.clear();
            for row in rows {
                if row.collection == self.plan.collection {
                    self.rows.insert(row.identity.clone(), row);
                }
            }
            self.rebuild();
        }
        /// Applies a Z-set delta and returns the deterministic result delta.
        pub fn apply(&mut self, delta: &ZSetDelta) -> Vec<crate::wire::ResultOperation> {
            let before = self.result.clone();
            for entry in &delta.entries {
                if entry.row.collection != self.plan.collection {
                    continue;
                }
                if entry.weight < 0 {
                    self.rows.remove(&entry.row.identity);
                } else if entry.weight > 0 {
                    self.rows.insert(entry.row.identity.clone(), entry.row.clone());
                }
            }
            self.rebuild();
            diff(&before, &self.result)
        }
        #[must_use]
        pub fn result(&self) -> &[ResultItem] {
            &self.result
        }
        #[must_use]
        pub fn boundary_len(&self) -> usize {
            self.rows.len().min(self.boundary)
        }
        fn rebuild(&mut self) {
            let mut rows: Vec<&ExtractedRow> =
                self.rows.values().filter(|row| self.plan.matches(row)).collect();
            rows.sort_by(|left, right| compare_rows(&self.plan, left, right));
            rows.truncate(self.boundary);
            self.result = rows
                .into_iter()
                .filter_map(|row| self.plan.evaluate(row))
                .take(self.plan.limit)
                .collect();
        }
    }

    fn compare_rows(plan: &ViewPlan, left: &ExtractedRow, right: &ExtractedRow) -> Ordering {
        for key in &plan.order {
            let l = left.values.get(&key.field).filter(|value| **value != Value::Null);
            let r = right.values.get(&key.field).filter(|value| **value != Value::Null);
            let ordering = match (l, r) {
                (None, None) => Ordering::Equal,
                (None, Some(_)) => {
                    if key.nulls_last {
                        Ordering::Greater
                    } else {
                        Ordering::Less
                    }
                }
                (Some(_), None) => {
                    if key.nulls_last {
                        Ordering::Less
                    } else {
                        Ordering::Greater
                    }
                }
                (Some(l), Some(r)) => {
                    if key.descending {
                        r.cmp(l)
                    } else {
                        l.cmp(r)
                    }
                }
            };
            if ordering != Ordering::Equal {
                return ordering;
            }
        }
        left.identity.cmp(&right.identity)
    }

    /// Classifies SQL text without pretending unsupported operators are live.
    pub fn classify_sql(sql: &str) -> Result<(), PlanError> {
        let normalized = normalize_sql(sql);
        if normalized.is_empty() {
            return Err(PlanError::Empty);
        }
        let words: Vec<&str> = normalized.split_whitespace().collect();
        let operator = if words.iter().any(|word| word.eq_ignore_ascii_case("join")) {
            Some(UnsupportedOperator::Join)
        } else if words.iter().any(|word| matches!(*word, "group" | "having"))
            || words.iter().any(|word| {
                ["count(", "sum(", "avg(", "min(", "max("]
                    .iter()
                    .any(|prefix| word.starts_with(prefix))
            })
        {
            Some(UnsupportedOperator::Aggregate)
        } else if normalized.contains(" over(") || normalized.contains(" over ") {
            Some(UnsupportedOperator::Window)
        } else if normalized.contains("select (select")
            || normalized.contains(" in (select")
            || normalized.contains(" exists (select")
        {
            Some(UnsupportedOperator::Subquery)
        } else {
            None
        };
        if let Some(operator) = operator {
            return Err(PlanError::Unsupported(operator));
        }
        if !normalized.starts_with("select ") {
            return Err(PlanError::Invalid);
        }
        if normalized.matches(" from ").count() != 1 {
            return Err(PlanError::Unsupported(UnsupportedOperator::MultiCollection));
        }
        Ok(())
    }
}

pub use plan::{
    Compare, DependencyIndex as ViewDependencyIndex, DependencyKey as ViewDependencyKey,
    ExtractedRow, FtsPredicate, Materializer, PlanError, Predicate, RangeEnd as ViewRangeEnd,
    UnsupportedOperator, Value, ValueRange as ViewValueRange, ViewPlan, ZSetDelta, ZSetEntry,
    classify_sql,
};

#[cfg(test)]
mod tests {
    use super::*;

    fn key(sql: &str) -> SharedViewKey {
        SharedViewKey::new(
            "db#1",
            "branch#1",
            sql,
            vec![1, 2],
            vec!["id".into()],
            vec![],
            10,
            1,
            2,
            "tenant = ?",
            3,
            4,
            "GRU",
        )
        .unwrap()
    }

    fn item(id: &str) -> ResultItem {
        ResultItem { identity: id.into(), fields: vec![] }
    }

    #[test]
    fn key_normalization_includes_every_authorization_dimension() {
        assert_eq!(key("select  id from x").stable_id(), key("select id from x").stable_id());
        assert_ne!(
            key("select id from x").stable_id(),
            SharedViewKey::new(
                "db#1",
                "branch#1",
                "select id from x",
                vec![1, 2],
                vec!["id".into()],
                vec![],
                10,
                1,
                2,
                "tenant = ?",
                4,
                4,
                "GRU"
            )
            .unwrap()
            .stable_id()
        );
    }

    #[test]
    fn registry_shares_equivalent_views_and_releases_last_reference() {
        let registry = SharedViewRegistry::new(1);
        let first = registry.attach(key("select id from x")).unwrap();
        let second = registry.attach(key("select id from x")).unwrap();
        assert_eq!(registry.len(), 1);
        assert_eq!(first.refcount(), 2);
        drop(first);
        assert_eq!(registry.attached(), 1);
        drop(second);
        assert!(registry.is_empty());
    }

    #[test]
    fn snapshot_and_delta_bytes_are_deterministic_and_bounded() {
        let view = SharedView::new(key("select id from x"));
        let left = view.install_snapshot(vec![item("a"), item("b")]).unwrap();
        let right = view.install_snapshot(vec![item("a"), item("b")]).unwrap();
        assert_eq!(left[0].bytes, right[0].bytes);
        let entry = view
            .publish(vec![ResultOperation::Insert {
                identity: "c".into(),
                position: 2,
                fields: vec![],
            }])
            .unwrap();
        assert_eq!(
            entry.bytes,
            view.poll(1, 0, &checkpoint(1, 0, 1), Duration::ZERO).into_data().unwrap().bytes
        );
        assert!(entry.etag.starts_with('"'));
    }

    #[test]
    fn cadence_coalesces_operations() {
        let view = SharedView::new(key("select id from x"));
        let start = Instant::now();
        view.enqueue(
            vec![ResultOperation::Insert { identity: "a".into(), position: 0, fields: vec![] }],
            start,
        )
        .unwrap();
        view.enqueue(
            vec![ResultOperation::Insert { identity: "b".into(), position: 1, fields: vec![] }],
            start,
        )
        .unwrap();
        assert!(view.flush_due(start + Duration::from_millis(99)).unwrap().is_none());
        let entry = view.flush_due(start + DELTA_CADENCE).unwrap().unwrap();
        assert_eq!(entry.operations.len(), 2);
    }

    #[test]
    fn concurrent_readers_share_one_wait_key() {
        let view = Arc::new(SharedView::new(key("select id from x")));
        view.install_snapshot(Vec::new()).unwrap();
        let checkpoint = view.checkpoint();
        let reader = Arc::clone(&view);
        let worker =
            std::thread::spawn(move || reader.poll(1, 0, &checkpoint, Duration::from_secs(2)));
        for _ in 0..1_000 {
            if view.singleflight_keys() == 1 {
                break;
            }
            std::thread::yield_now();
        }
        assert_eq!(view.singleflight_keys(), 1);
        view.publish(vec![ResultOperation::Insert {
            identity: "a".into(),
            position: 0,
            fields: vec![],
        }])
        .unwrap();
        assert!(matches!(worker.join().expect("reader joins"), PollResult::Data(_)));
        assert_eq!(view.singleflight_keys(), 0);
    }

    #[test]
    fn retention_invalidates_evicted_checkpoints_and_bounds_log() {
        let view = SharedView::new(key("select id from x"));
        view.install_snapshot(Vec::new()).unwrap();
        view.set_retention(2).unwrap();
        let initial = view.checkpoint();
        view.publish(vec![ResultOperation::Insert {
            identity: "a".into(),
            position: 0,
            fields: vec![],
        }])
        .unwrap();
        let first = view.checkpoint();
        view.publish(vec![ResultOperation::Insert {
            identity: "b".into(),
            position: 1,
            fields: vec![],
        }])
        .unwrap();
        view.publish(vec![ResultOperation::Insert {
            identity: "c".into(),
            position: 2,
            fields: vec![],
        }])
        .unwrap();
        assert_eq!(view.retained_revisions(), 2);
        assert_eq!(
            view.validate_resume(1, 0, &initial),
            ResumeDecision::Reset(ResetReason::CheckpointExpired)
        );
        assert_eq!(
            view.validate_resume(1, 1, &first),
            ResumeDecision::Reset(ResetReason::InvalidCheckpoint)
        );
    }

    #[test]
    fn top_k_and_aggregate_bounds_are_enforced() {
        let view = SharedView::new(key("select id from x"));
        assert!(matches!(
            view.install_snapshot((0..=MAX_TOP_K_ITEMS).map(|i| item(&i.to_string())).collect()),
            Err(ViewError::ResultLimit)
        ));
        for i in 0..MAX_AGGREGATE_GROUPS {
            view.aggregate_insert(i.to_string(), vec![]).unwrap();
        }
        assert!(matches!(
            view.aggregate_insert("overflow", vec![]),
            Err(ViewError::AggregateLimit)
        ));
    }

    trait PollExt {
        fn into_data(self) -> Option<RevisionEntry>;
    }
    impl PollExt for PollResult {
        fn into_data(self) -> Option<RevisionEntry> {
            if let PollResult::Data(entry) = self { Some(entry) } else { None }
        }
    }
}
