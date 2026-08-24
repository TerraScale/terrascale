//! Subscriptions over the publication registry: bounded notification
//! ingress, journal-driven delivery, checkpoints, acknowledgement, and
//! resend.
//!
//! Two paths cooperate, and the split is the delivery design:
//!
//! - **Notification ingress.** One streaming-owned sink per subscribed
//!   `Engine` is registered through
//!   [`Engine::subscribe_publications`](crate::engine::Engine::subscribe_publications).
//!   The sink runs synchronously on the committing thread, so it does four
//!   things only: clone two small identifiers, take the subscription's own
//!   mutex, push into a bounded drop-oldest queue, and return. It never
//!   blocks, never takes a storage lock, and never grows an unbounded
//!   queue. A subscription whose consumer has not drained its queue drops
//!   the oldest notification and counts the drop — the observable overflow
//!   policy. A dropped notification costs latency, never correctness,
//!   because delivery itself is journal-driven.
//! - **Delivery.** Batches are resolved on the consumer's thread from the
//!   durable publication-history journal
//!   ([`Engine::tenant_commits`](crate::engine::Engine::tenant_commits)),
//!   which contains only published commits. The consumer's lock order —
//!   subscription lock, then storage lock — cannot deadlock with the commit
//!   path, because the W3 publication order releases every storage lock
//!   before the event is emitted.
//!
//! The durable checkpoint advances only at the acknowledgement seam.
//! Unacknowledged batches are redelivered verbatim — delivery is
//! at-least-once, and every event carries stable operation identity plus
//! storage sequence so duplicates are identifiable.

use std::{
    collections::{BTreeMap, VecDeque},
    sync::{
        Arc, Mutex, MutexGuard, OnceLock, PoisonError,
        atomic::{AtomicU64, Ordering as AtomicOrdering},
    },
};

use crate::{
    engine::{CommitPublicationSink, Engine, SubscriptionGuard},
    operations::RequestValidationError,
    storage::{CommitPublicationEvent, PublishedCommit, PublishedWrite},
    types::{
        Cancellation, CollectionName, DocumentId, OperationId, StorageSequence, TenantId,
        TenantScope,
    },
};

use super::{
    ChangeEvent, ChangeFilter, ChangeKind, StreamCompletion, StreamError,
    SubscriptionAcknowledgement, SubscriptionBatch, SubscriptionCheckpoint, SubscriptionSpec,
    SubscriptionStatus, sources,
};

/// The bound of one subscription's notification queue.
///
/// This is the slow-consumer bound of the delivery path: when a consumer
/// has not drained this many notifications, the oldest is dropped and
/// counted, and the subscription's batches report the cumulative drop
/// count. The queue holds identifier pairs only. The value is the W4-STREAM
/// configured bound — making it per-deployment configuration is a later
/// disposition — and any positive bound keeps the contract's guarantees,
/// because a dropped notification costs latency, never correctness.
pub const MAX_SUBSCRIPTION_NOTIFICATIONS: usize = 64;

/// How many committed writes one subscription batch may examine before it
/// returns short.
///
/// The bound keeps an all-filtered prefix's rescan bounded per poll; a
/// prefix of filtered writes longer than this budget is reported as drops
/// and re-examined on the next poll. A tracer-scale limit, recorded with
/// its liveness consequence in the design record.
const MAX_SUBSCRIPTION_SCAN_EVENTS: usize = 4096;

/// The reserved family collection, mirrored from the W3 family contract
/// (`src/query/spec.rs` mirrors it the same way).
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// The opaque position one subscription checkpoint carries.
///
/// `sequence` is the global storage sequence of the last acknowledged
/// publication and `write_index` the write within that commit, so a batch
/// boundary may split one commit without acknowledging writes the consumer
/// never saw. `epoch` binds the checkpoint to one subscription incarnation
/// so a closed-and-reopened subscription never accepts a stale checkpoint.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CheckpointInner {
    sequence: StorageSequence,
    write_index: u32,
    epoch: u64,
}

impl CheckpointInner {
    /// Reports whether one write position is at or below this checkpoint.
    fn covers(&self, sequence: StorageSequence, write_index: u32) -> bool {
        if sequence.get() < self.sequence.get() {
            return true;
        }
        sequence.get() == self.sequence.get() && write_index <= self.write_index
    }
}

/// One compact publication notification.
#[derive(Clone, Debug, Eq, PartialEq)]
struct Notification {
    sequence: StorageSequence,
    operation: OperationId,
}

/// The private state of one live subscription.
struct SubscriptionCore {
    tenant: TenantId,
    engine_identity: u128,
    filter: ChangeFilter,
    max_events: u32,
    state: SubscriptionState,
    checkpoint: CheckpointInner,
    pending: Option<SubscriptionBatch>,
    notifications: VecDeque<Notification>,
    dropped_notifications: u64,
}

/// The private negotiation states; `Closed` is terminal and removes the
/// core, so it never lives here.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum SubscriptionState {
    /// The negotiated opening phase. The synchronous W4-STREAM open
    /// completes it before `subscribe` returns, so no path constructs it;
    /// it exists because the public status vocabulary names it and a later
    /// transport-async open will occupy it.
    #[allow(
        dead_code,
        reason = "named by the public status vocabulary; the synchronous open completes it"
    )]
    Opening,
    Active,
    AwaitingAcknowledgement,
    Reconnecting,
}

impl SubscriptionState {
    /// Maps onto the public status vocabulary.
    fn status(self) -> SubscriptionStatus {
        match self {
            Self::Opening => SubscriptionStatus::Opening,
            Self::Active => SubscriptionStatus::Active,
            Self::AwaitingAcknowledgement => SubscriptionStatus::AwaitingAcknowledgement,
            Self::Reconnecting => SubscriptionStatus::Reconnecting,
        }
    }
}

/// Every live subscription of one `Engine`, plus its sink registration.
struct EngineSubscriptions {
    sink_guard: Option<SubscriptionGuard>,
    cores: BTreeMap<(String, String), Arc<Mutex<SubscriptionCore>>>,
}

/// The process-wide index of engine subscription sets, keyed by the
/// never-reused Engine identity.
///
/// The registry exists because the frozen `Engine` owns no streaming
/// state: the key comes from a scope that
/// [`Engine::read_snapshot`](crate::engine::Engine::read_snapshot) has
/// validated against this Engine. An engine's entry is removed as soon as
/// its last subscription closes, which detaches the sink; entries
/// therefore live only while subscriptions are live. An engine that is
/// dropped with open subscriptions retains its entry until process end —
/// bounded by the number of abandoned subscriptions and recorded as a
/// limit with a proposed collision action in the design record.
static ENGINE_SUBSCRIPTIONS: OnceLock<Mutex<BTreeMap<u128, Arc<Mutex<EngineSubscriptions>>>>> =
    OnceLock::new();

/// The monotonic incarnation counter for subscription checkpoints.
static CHECKPOINT_EPOCHS: AtomicU64 = AtomicU64::new(1);

/// Locks one mutex, recovering from poisoning.
///
/// Streaming state is advisory — correctness lives in the durable journal —
/// so a lock poisoned by a panicked consumer is recovered rather than
/// propagated, and the sink uses the same rule so a panic can never reach
/// the committing thread.
fn lock_recovered<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Validates one subscription name under the shared identifier rules.
pub(crate) fn validate_spec_name(name: &str) -> Result<(), RequestValidationError> {
    if name.is_empty() || name.len() > 255 || name.chars().any(char::is_control) {
        return Err(RequestValidationError::EmptyField);
    }
    if name != name.trim() {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    Ok(())
}

/// Returns the subscription set of one Engine, registering its sink on
/// first use.
fn engine_set(
    engine: &Engine,
    identity: u128,
) -> Result<Arc<Mutex<EngineSubscriptions>>, StreamError> {
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let mut engines = lock_recovered(registry);
    if let Some(set) = engines.get(&identity) {
        return Ok(Arc::clone(set));
    }
    let set =
        Arc::new(Mutex::new(EngineSubscriptions { sink_guard: None, cores: BTreeMap::new() }));
    let sink_set = Arc::clone(&set);
    let sink: CommitPublicationSink =
        Arc::new(move |event: &CommitPublicationEvent| deliver_notification(&sink_set, event));
    let guard = engine.subscribe_publications(sink);
    if !guard.is_attached() {
        // The registry is at its subscriber bound; the notification path
        // cannot be established for this engine.
        return Err(StreamError::InvalidSource);
    }
    lock_recovered(&set).sink_guard = Some(guard);
    engines.insert(identity, Arc::clone(&set));
    Ok(set)
}

/// Removes one engine's set when its last subscription closed, detaching
/// the sink.
///
/// The registry lock is always taken before the set lock, the same order
/// every other path uses.
fn release_engine_set_if_empty(identity: u128, set: &Arc<Mutex<EngineSubscriptions>>) {
    let empty = lock_recovered(set).cores.is_empty();
    if !empty {
        return;
    }
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let mut engines = lock_recovered(registry);
    if engines
        .get(&identity)
        .is_some_and(|live| Arc::ptr_eq(live, set) && lock_recovered(live).cores.is_empty())
    {
        engines.remove(&identity);
    }
}

/// Fans one publication event into every subscription's bounded queue.
///
/// The committing thread runs this closure, so it is strictly
/// non-blocking: a lock that cannot be taken is skipped, the queue is
/// bounded with drop-oldest, and no storage lock is ever taken here.
fn deliver_notification(set: &Arc<Mutex<EngineSubscriptions>>, event: &CommitPublicationEvent) {
    let cores: Vec<Arc<Mutex<SubscriptionCore>>> = {
        let Ok(set) = set.lock() else {
            // An unreachable set lock drops this notification; delivery is
            // journal-driven, so the drop costs latency only.
            return;
        };
        set.cores.values().map(Arc::clone).collect()
    };
    for core in cores {
        let Ok(mut core) = core.lock() else {
            // A consumer holding its core lock never blocks the commit.
            continue;
        };
        if core.notifications.len() >= MAX_SUBSCRIPTION_NOTIFICATIONS {
            core.notifications.pop_front();
            core.dropped_notifications = core.dropped_notifications.saturating_add(1);
        }
        core.notifications.push_back(Notification {
            sequence: event.sequence(),
            operation: event.operation_id().clone(),
        });
    }
}

/// Validates the scope against the Engine and returns its identity.
fn validated_identity(engine: &Engine, scope: &TenantScope) -> Result<u128, StreamError> {
    engine.read_snapshot(scope).map_err(|error| sources::map_engine_error(&error))?;
    Ok(scope.engine_identity())
}

/// The located handle of one subscription: its engine identity, its
/// engine's set, and the core itself.
type LocatedCore = (u128, Arc<Mutex<EngineSubscriptions>>, Arc<Mutex<SubscriptionCore>>);

/// Looks up one subscription core under its engine, tenant, and name.
fn find_core(engine: &Engine, scope: &TenantScope, name: &str) -> Result<LocatedCore, StreamError> {
    let identity = validated_identity(engine, scope)?;
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let engines = lock_recovered(registry);
    let Some(set) = engines.get(&identity) else {
        return Err(StreamError::InvalidSource);
    };
    let set = Arc::clone(set);
    drop(engines);
    let key = (scope.tenant().as_str().to_owned(), name.to_owned());
    let Some(core) = lock_recovered(&set).cores.get(&key).map(Arc::clone) else {
        return Err(StreamError::InvalidSource);
    };
    Ok((identity, set, core))
}

/// Opens or reattaches one named subscription.
pub(crate) fn subscribe(
    engine: &Engine,
    scope: &TenantScope,
    spec: &SubscriptionSpec,
) -> Result<SubscriptionStatus, StreamError> {
    let identity = validated_identity(engine, scope)?;
    let set = engine_set(engine, identity)?;
    let key = (scope.tenant().as_str().to_owned(), spec.name().to_owned());
    let mut set_guard = lock_recovered(&set);
    if let Some(core) = set_guard.cores.get(&key).map(Arc::clone) {
        drop(set_guard);
        let mut core = lock_recovered(&core);
        if core.state == SubscriptionState::Reconnecting {
            // Reattach: the preserved checkpoint and any unacknowledged
            // recorded batch survive, so delivery resends exactly what the
            // consumer never acknowledged.
            core.state = if core.pending.is_some() {
                SubscriptionState::AwaitingAcknowledgement
            } else {
                SubscriptionState::Active
            };
            return Ok(core.state.status());
        }
        // Exactly one active consumer owns a subscription at a time.
        return Err(StreamError::InvalidSource);
    }
    let epoch = CHECKPOINT_EPOCHS.fetch_add(1, AtomicOrdering::Relaxed);
    let checkpoint = initial_checkpoint(engine, spec, epoch)?;
    let core = Arc::new(Mutex::new(SubscriptionCore {
        tenant: scope.tenant().clone(),
        engine_identity: identity,
        filter: spec.filter().clone(),
        max_events: spec.max_events(),
        state: SubscriptionState::Active,
        checkpoint,
        pending: None,
        notifications: VecDeque::new(),
        dropped_notifications: 0,
    }));
    set_guard.cores.insert(key, core);
    Ok(SubscriptionStatus::Active)
}

/// Computes the start checkpoint of one fresh subscription.
fn initial_checkpoint(
    engine: &Engine,
    spec: &SubscriptionSpec,
    epoch: u64,
) -> Result<CheckpointInner, StreamError> {
    let sequence = if spec.starts_from_beginning() {
        // Resume so the oldest servable history is the first delivery; a
        // cursor older than the servable window must not skip a gap.
        match engine.servable_history_start() {
            Ok(Some(start)) => StorageSequence::new(start.get().saturating_sub(1)),
            _ => StorageSequence::new(0),
        }
    } else {
        // Deliver publications from now on: the journal's durable
        // high-water mark, never the in-memory published state, so no
        // event is promised that the journal cannot serve.
        engine
            .journaled_high_water()
            .map_err(|error| sources::map_engine_error(&error))?
            .unwrap_or(StorageSequence::new(0))
    };
    Ok(CheckpointInner { sequence, write_index: 0, epoch })
}

/// Serves one bounded subscription batch and records its boundary.
pub(crate) fn next_batch(
    engine: &Engine,
    scope: &TenantScope,
    name: &str,
    cancellation: &Cancellation,
) -> Result<SubscriptionBatch, StreamError> {
    let (identity, set, core) = find_core(engine, scope, name)?;
    if cancellation.is_cancelled() {
        // Cancellation closes promptly: the core and its buffers are
        // released and the subscription reports Closed(Cancelled) from
        // then on.
        drop(core);
        remove_core(identity, &set, scope, name);
        return Err(StreamError::Cancelled);
    }
    let mut core = lock_recovered(&core);
    if core.state == SubscriptionState::Reconnecting {
        // A reconnecting subscription must reattach through subscribe.
        return Err(StreamError::InvalidSource);
    }
    if let Some(recorded) = &core.pending {
        // At-least-once: the recorded boundary is redelivered verbatim
        // until the caller acknowledges it.
        return Ok(recorded.clone());
    }
    let batch = build_batch(engine, &core)?;
    core.pending = Some(batch.clone());
    core.state = SubscriptionState::AwaitingAcknowledgement;
    Ok(batch)
}

/// Builds one bounded batch from the durable publication journal.
///
/// The scan walks commits after the checkpoint in storage-sequence order,
/// expands every reconstructable write into one change event, keeps the
/// events the filter accepts up to the spec's bound, and records as the
/// boundary the position of the last delivered event. Filtered events are
/// counted as progress facts and never move the boundary by themselves.
fn build_batch(engine: &Engine, core: &SubscriptionCore) -> Result<SubscriptionBatch, StreamError> {
    let checkpoint = core.checkpoint.clone();
    let after = if checkpoint.write_index > 0 {
        // Re-read the commit the checkpoint splits so its tail is served.
        StorageSequence::new(checkpoint.sequence.get() - 1)
    } else {
        checkpoint.sequence
    };
    let commits = engine
        .tenant_commits(&core.tenant, after, core.max_events.saturating_add(1))
        .map_err(|error| sources::map_engine_error(&error))?;
    let max_events = usize::try_from(core.max_events).unwrap_or(usize::MAX);
    let mut events: Vec<ChangeEvent> = Vec::new();
    let mut boundary = checkpoint.clone();
    let mut filtered_dropped = 0_u64;
    let mut examined = 0_usize;
    'scan: for commit in &commits {
        for (write_index, (kind, collection, event)) in
            expand_commit(core, commit).into_iter().enumerate()
        {
            if checkpoint.covers(commit.sequence(), u32::try_from(write_index).unwrap_or(u32::MAX))
            {
                continue;
            }
            examined += 1;
            if core.filter.accepts(kind, &collection) {
                if events.len() >= max_events {
                    break 'scan;
                }
                events.push(event);
                boundary = CheckpointInner {
                    sequence: commit.sequence(),
                    write_index: u32::try_from(write_index + 1).unwrap_or(u32::MAX),
                    epoch: checkpoint.epoch,
                };
            } else {
                // The drop is a progress fact. It never moves the
                // boundary by itself, so an event the filter rejected is
                // re-examined on the next poll until a delivered event
                // passes over it — the no-advance rule's liveness cost,
                // recorded in the design record.
                filtered_dropped += 1;
            }
            if examined >= MAX_SUBSCRIPTION_SCAN_EVENTS {
                break 'scan;
            }
        }
    }
    Ok(SubscriptionBatch::new(
        events,
        SubscriptionCheckpoint { inner: boundary },
        filtered_dropped,
        core.dropped_notifications,
    ))
}

/// Expands one published commit into candidate change events.
///
/// Each candidate is the event's kind, its collection, and the event
/// itself. A no-op delete — an identifier that existed nowhere — produces
/// no event: nothing changed.
fn expand_commit(
    core: &SubscriptionCore,
    commit: &PublishedCommit,
) -> Vec<(ChangeKind, CollectionName, ChangeEvent)> {
    let mut candidates = Vec::new();
    for write in commit.writes() {
        let (collection, document) = match write {
            PublishedWrite::Put { document } => {
                (document.collection().clone(), document.id().clone())
            }
            PublishedWrite::Delete { collection: Some(collection), id, .. } => {
                (collection.clone(), id.clone())
            }
            PublishedWrite::Delete { collection: None, .. } => continue,
        };
        let kind = change_kind_of(&collection, &document);
        let event = ChangeEvent::new(
            kind,
            TenantScope::from_parts(core.tenant.clone(), core.engine_identity),
            collection.clone(),
            document,
            commit.operation_id().clone(),
            commit.sequence(),
        );
        candidates.push((kind, collection, event));
    }
    candidates
}

/// Maps one changed key onto the closed change-kind set.
///
/// Counter and time-series family records report their own kinds; every
/// other write — user documents and the remaining family records — reports
/// `Document`, because the closed V1 shape set has no closer case. The
/// mapping table is recorded in the design record.
fn change_kind_of(collection: &CollectionName, document: &DocumentId) -> ChangeKind {
    if collection.as_str() == RESERVED_FAMILY_COLLECTION {
        let id = document.as_str();
        if id.starts_with("family:counters/") {
            return ChangeKind::Counter;
        }
        if id.starts_with("family:time-series/") {
            return ChangeKind::TimeSeries;
        }
    }
    ChangeKind::Document
}

/// Acknowledges one recorded boundary and advances the durable checkpoint.
pub(crate) fn acknowledge(
    engine: &Engine,
    scope: &TenantScope,
    acknowledgement: &SubscriptionAcknowledgement,
) -> Result<SubscriptionStatus, StreamError> {
    let (_identity, _set, core) = find_core_by_epoch(engine, scope, acknowledgement)?;
    let mut core = lock_recovered(&core);
    let confirmed = acknowledgement.confirmed().inner.clone();
    if let Some(recorded) = &core.pending
        && recorded.checkpoint().inner == confirmed
    {
        // The acknowledgement seam is the only place the durable
        // checkpoint advances.
        core.checkpoint = confirmed;
        core.pending = None;
        core.state = SubscriptionState::Active;
        return Ok(core.state.status());
    }
    if core.pending.is_none() && core.checkpoint == confirmed {
        // An idempotent re-acknowledgement of the current checkpoint.
        return Ok(SubscriptionStatus::Active);
    }
    Err(StreamError::InvalidCursor)
}

/// Resolves the core an acknowledgement addresses.
///
/// The acknowledgement carries an opaque checkpoint whose epoch is unique
/// per subscription incarnation, so the epoch alone identifies the core.
fn find_core_by_epoch(
    engine: &Engine,
    scope: &TenantScope,
    acknowledgement: &SubscriptionAcknowledgement,
) -> Result<LocatedCore, StreamError> {
    let identity = validated_identity(engine, scope)?;
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let engines = lock_recovered(registry);
    let Some(set) = engines.get(&identity) else {
        return Err(StreamError::InvalidSource);
    };
    let set = Arc::clone(set);
    drop(engines);
    let epoch = acknowledgement.confirmed().inner.epoch;
    let cores = lock_recovered(&set);
    for core in cores.cores.values() {
        let locked = lock_recovered(core);
        if locked.tenant == *scope.tenant() && locked.checkpoint.epoch == epoch {
            drop(locked);
            return Ok((identity, Arc::clone(&set), Arc::clone(core)));
        }
    }
    Err(StreamError::InvalidSource)
}

/// Reports one subscription's negotiated state.
pub(crate) fn status(
    engine: &Engine,
    scope: &TenantScope,
    name: &str,
) -> Result<SubscriptionStatus, StreamError> {
    let (_identity, _set, core) = find_core(engine, scope, name)?;
    let core = lock_recovered(&core);
    Ok(core.state.status())
}

/// Marks one subscription reconnecting.
pub(crate) fn disconnect(
    engine: &Engine,
    scope: &TenantScope,
    name: &str,
) -> Result<SubscriptionStatus, StreamError> {
    let (_identity, _set, core) = find_core(engine, scope, name)?;
    let mut core = lock_recovered(&core);
    core.state = SubscriptionState::Reconnecting;
    Ok(core.state.status())
}

/// Closes one subscription terminally and releases its buffers.
pub(crate) fn close(
    engine: &Engine,
    scope: &TenantScope,
    name: &str,
) -> Result<SubscriptionStatus, StreamError> {
    let (identity, set, _core) = find_core(engine, scope, name)?;
    remove_core(identity, &set, scope, name);
    Ok(SubscriptionStatus::Closed(StreamCompletion::Cancelled))
}

/// Removes one core from its engine set, releasing the notification queue.
fn remove_core(
    identity: u128,
    set: &Arc<Mutex<EngineSubscriptions>>,
    scope: &TenantScope,
    name: &str,
) {
    let key = (scope.tenant().as_str().to_owned(), name.to_owned());
    lock_recovered(set).cores.remove(&key);
    release_engine_set_if_empty(identity, set);
}
