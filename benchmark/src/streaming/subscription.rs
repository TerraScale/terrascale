//! Subscriptions over the publication registry: bounded notification
//! ingress, journal-driven delivery, checkpoints, acknowledgement, and
//! resend.
//!
//! Two paths cooperate, and the split is the delivery design:
//!
//! - **Notification ingress.** One streaming-owned sink per subscribed
//!   `Engine` is registered through
//!   [`Engine::subscribe_publications`](crate::engine::Engine::subscribe_publications).
//!   The sink runs synchronously on the committing thread, so it takes
//!   every lock with `try_lock` only: a busy lock — a consumer mid-poll
//!   holding its subscription lock, or a concurrent open or close — drops
//!   the notification and counts the drop in a lock-free counter. It never
//!   blocks, never waits, never takes a storage lock, and never grows an
//!   unbounded queue. A subscription whose consumer has not drained its
//!   queue drops the oldest notification and counts that drop too. Both
//!   drop kinds are the observable overflow policy, and both cost latency
//!   only, never correctness, because delivery itself is journal-driven.
//! - **Delivery.** Batches are resolved on the consumer's thread from the
//!   durable publication-history journal
//!   ([`Engine::tenant_commits`](crate::engine::Engine::tenant_commits)),
//!   which contains only published commits. The consumer's lock order —
//!   subscription lock, then storage lock — cannot deadlock with the commit
//!   path, because the W3 publication order releases every storage lock
//!   before the event is emitted, and the sink never waits on either.
//!
//! The durable checkpoint advances only at the acknowledgement seam.
//! Unacknowledged batches are redelivered verbatim — delivery is
//! at-least-once, and every event carries stable operation identity plus
//! storage sequence so duplicates are identifiable. A batch's *recorded
//! boundary* may cover events its filter dropped, because the batch
//! reports those drops as progress facts: the advance is reported, never
//! silent, and the durable checkpoint still moves only when the caller
//! acknowledges the boundary.

use std::{
    collections::{BTreeMap, VecDeque},
    sync::{
        Arc, Mutex, MutexGuard, OnceLock, PoisonError,
        atomic::{AtomicU64, Ordering as AtomicOrdering},
    },
};

#[cfg(test)]
use std::{
    sync::atomic::AtomicBool,
    time::{Duration, Instant},
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

/// The bound of one engine's simultaneously live subscriptions.
///
/// The sink fans every publication of an engine into each of its live
/// subscriptions, so the per-commit ingress cost grows with the
/// subscription count. This bound caps that collective cost: a subscribe
/// beyond it is refused with [`StreamError::InvalidSource`], the same
/// typed refusal the engine's own sink registry uses at
/// [`MAX_PUBLICATION_SINKS`](crate::engine::MAX_PUBLICATION_SINKS).
pub const MAX_SUBSCRIPTIONS_PER_ENGINE: usize = 64;

/// How many committed writes one subscription batch may examine before it
/// returns short.
///
/// The scan pages the journal within this budget so an accepted event
/// beyond a run of filtered events is reachable; a filtered run longer
/// than the budget is reported as drops in partial batches and continues
/// on the next poll, because each batch's boundary covers the drops it
/// reported. A tracer-scale limit, recorded with its semantics in the
/// design record.
pub const MAX_SUBSCRIPTION_SCAN_EVENTS: usize = 4096;

/// The optional storage retention consumer shared by live change streams.
///
/// Storage exposes one optional sync frontier. The stream registry therefore
/// owns the fan-in and publishes the minimum floor of every live subscription
/// rather than letting the most recently acknowledged reader release history
/// still needed by another reader.
const RETENTION_CONSUMER: &str = "sync";

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
        position_covered(self.sequence.get(), self.write_index, sequence, write_index)
    }

    /// Returns the journal cursor this checkpoint records: the sequence
    /// and consumed write count a public
    /// [`ChangeCheckpoint`](super::ChangeCheckpoint) names for replay
    /// (SE-03).
    pub(crate) fn journal_cursor(&self) -> (u64, u32) {
        (self.sequence.get(), self.write_index)
    }
}

/// Reports whether the write at `sequence`/`write_index` is consumed by the
/// journal cursor `(cursor_sequence, cursor_write_index)`.
///
/// A cursor consuming `cursor_write_index` writes of its commit covers
/// exactly the write indices strictly below it — never the next
/// unexamined write, so a batch boundary that splits one commit resumes
/// without skipping or double-serving a write. Shared by the live
/// subscription scan and the SE-03 journal replay.
pub(crate) fn position_covered(
    cursor_sequence: u64,
    cursor_write_index: u32,
    sequence: StorageSequence,
    write_index: u32,
) -> bool {
    if sequence.get() < cursor_sequence {
        return true;
    }
    sequence.get() == cursor_sequence && write_index < cursor_write_index
}

/// One compact publication notification.
#[derive(Clone, Debug, Eq, PartialEq)]
struct Notification {
    sequence: StorageSequence,
    operation: OperationId,
}

/// The private state of one live subscription.
///
/// The state sits behind the handle's mutex; the handle's lock-free
/// contention counter lives beside it so the committing thread can count a
/// dropped notification without ever waiting for the mutex.
struct SubscriptionCore {
    #[cfg(test)]
    name: String,
    tenant: TenantId,
    filter: ChangeFilter,
    max_events: u32,
    state: SubscriptionState,
    checkpoint: CheckpointInner,
    pending: Option<SubscriptionBatch>,
    notifications: VecDeque<Notification>,
    queue_full_drops: u64,
    /// Bounded conflict-observation queue (SE-03): advisory live events,
    /// drained into batches at build time.
    conflicts: VecDeque<ChangeEvent>,
    /// Conflict notifications dropped because the conflict queue was full.
    conflict_drops: u64,
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

/// One engine's live subscriptions: the guarded core map, the sink
/// registration, and the lock-free ingress counters.
///
/// The counters live outside `inner` so the committing thread can count a
/// notification it had to drop on lock contention without taking any lock.
/// An engine-level count is attributed to every subscription of that
/// engine, which is exactly what a dropped ingress means: no subscription
/// received the notification.
pub(crate) struct EngineSubscriptions {
    inner: Mutex<EngineSubscriptionsInner>,
    /// Notifications dropped because `inner` was busy at emit time.
    contention_drops: AtomicU64,
}

/// The guarded part of one engine's subscription set.
struct EngineSubscriptionsInner {
    sink_guard: Option<SubscriptionGuard>,
    cores: BTreeMap<(String, String), Arc<SubscriptionHandle>>,
}

/// One subscription's lockable state plus its lock-free contention
/// counter.
struct SubscriptionHandle {
    state: Mutex<SubscriptionCore>,
    /// Notifications dropped because `state` was busy at emit time —
    /// typically a consumer mid-poll, which holds its lock across the
    /// journal read.
    contention_drops: AtomicU64,
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
static ENGINE_SUBSCRIPTIONS: OnceLock<Mutex<BTreeMap<u128, Arc<EngineSubscriptions>>>> =
    OnceLock::new();

/// The monotonic incarnation counter for subscription checkpoints.
static CHECKPOINT_EPOCHS: AtomicU64 = AtomicU64::new(1);

// ---------------------------------------------------------------------------
// Test-only poll wedge (repair S1)
// ---------------------------------------------------------------------------

/// Requests that the next poll of the named subscription wedge while
/// holding its lock. Test-only.
#[cfg(test)]
static WEDGE_REQUESTED: AtomicBool = AtomicBool::new(false);

/// Signals that a poll has entered the wedge. Test-only.
#[cfg(test)]
static WEDGE_ENTERED: AtomicBool = AtomicBool::new(false);

/// The name of the subscription whose polls wedge. Test-only. Unlike the
/// process-wide counters, the name is replaceable so consecutive wedge
/// sessions may address different subscriptions.
#[cfg(test)]
static WEDGE_NAME: Mutex<Option<String>> = Mutex::new(None);

/// Serializes wedge sessions. Tests run in parallel and the wedge is
/// process-global, so two wedge tests must not hold wedged polls at the
/// same time; each takes this session lock for its whole arm-to-join
/// procedure. Test-only.
#[cfg(test)]
static WEDGE_SESSION: Mutex<()> = Mutex::new(());

/// Begins one exclusive wedge session. The returned guard serializes the
/// session's arm, wedge, and join; dropping it ends the session. Test-only.
#[cfg(test)]
pub(crate) fn begin_wedge_session() -> MutexGuard<'static, ()> {
    WEDGE_SESSION.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Arms the poll wedge for the named subscription and clears the entered
/// flag. Test-only.
#[cfg(test)]
pub(crate) fn arm_poll_wedge(name: &str) {
    WEDGE_ENTERED.store(false, AtomicOrdering::Release);
    *lock_recovered(&WEDGE_NAME) = Some(name.to_owned());
    WEDGE_REQUESTED.store(true, AtomicOrdering::Release);
}

/// Reports whether a poll has entered the wedge. Test-only.
#[cfg(test)]
pub(crate) fn wedge_entered() -> bool {
    WEDGE_ENTERED.load(AtomicOrdering::Acquire)
}

/// Releases the wedge. Test-only.
#[cfg(test)]
pub(crate) fn release_poll_wedge() {
    WEDGE_REQUESTED.store(false, AtomicOrdering::Release);
}

/// The wedge point itself: sleeps while requested, with a hard deadline so
/// a forgotten release can never hang a build. Test-only.
///
/// The caller invokes this while holding the subscription's state lock, so
/// a wedged poll is exactly the contention the sink must survive without
/// waiting.
#[cfg(test)]
fn wedge_point(core: &SubscriptionCore) {
    let wedged = {
        let name = lock_recovered(&WEDGE_NAME);
        WEDGE_REQUESTED.load(AtomicOrdering::Acquire)
            && name.as_deref().is_some_and(|name| core.name == name)
    };
    if wedged {
        WEDGE_ENTERED.store(true, AtomicOrdering::Release);
        let deadline = Instant::now() + Duration::from_secs(5);
        while WEDGE_REQUESTED.load(AtomicOrdering::Acquire) && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(2));
        }
    }
}

// ---------------------------------------------------------------------------

/// Locks one mutex, recovering from poisoning.
///
/// Streaming state is advisory — correctness lives in the durable journal —
/// so a lock poisoned by a panicked consumer is recovered rather than
/// propagated. Only consumer threads use this rule: the sink never waits on
/// a lock at all, so a panic can never reach the committing thread.
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
fn engine_set(engine: &Engine, identity: u128) -> Result<Arc<EngineSubscriptions>, StreamError> {
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let mut engines = lock_recovered(registry);
    if let Some(set) = engines.get(&identity) {
        return Ok(Arc::clone(set));
    }
    let set = Arc::new(EngineSubscriptions {
        inner: Mutex::new(EngineSubscriptionsInner { sink_guard: None, cores: BTreeMap::new() }),
        contention_drops: AtomicU64::new(0),
    });
    let sink_set = Arc::clone(&set);
    let sink: CommitPublicationSink =
        Arc::new(move |event: &CommitPublicationEvent| deliver_notification(&sink_set, event));
    let guard = engine.subscribe_publications(sink);
    if !guard.is_attached() {
        // The registry is at its subscriber bound; the notification path
        // cannot be established for this engine.
        return Err(StreamError::InvalidSource);
    }
    lock_recovered(&set.inner).sink_guard = Some(guard);
    engines.insert(identity, Arc::clone(&set));
    Ok(set)
}

/// Removes one engine's set when its last subscription closed, detaching
/// the sink.
///
/// The registry lock is always taken before the set lock, the same order
/// every other path uses.
fn release_engine_set_if_empty(identity: u128, set: &Arc<EngineSubscriptions>) {
    let empty = lock_recovered(&set.inner).cores.is_empty();
    if !empty {
        return;
    }
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let mut engines = lock_recovered(registry);
    if engines
        .get(&identity)
        .is_some_and(|live| Arc::ptr_eq(live, set) && lock_recovered(&live.inner).cores.is_empty())
    {
        engines.remove(&identity);
    }
}

/// Converts a subscription cursor into a complete-publication retention floor.
///
/// A cursor in the middle of a commit still needs the preceding complete
/// publication boundary retained: SQLite retention removes whole commits and
/// cannot preserve only the unread suffix of a split commit.
fn retention_floor(checkpoint: &CheckpointInner) -> StorageSequence {
    StorageSequence::new(if checkpoint.write_index == 0 {
        checkpoint.sequence.get()
    } else {
        checkpoint.sequence.get().saturating_sub(1)
    })
}

/// Advances the shared sync frontier to the minimum accepted checkpoint of
/// every live subscription. The registry and each core are read under the
/// same lock order used by ingress and lookup paths.
fn advance_retention_floor(
    engine: &Engine,
    set: &Arc<EngineSubscriptions>,
) -> Result<(), StreamError> {
    let inner = lock_recovered(&set.inner);
    let Some(floor) = inner
        .cores
        .values()
        .map(|handle| retention_floor(&lock_recovered(&handle.state).checkpoint))
        .min_by_key(|sequence| sequence.get())
    else {
        return Ok(());
    };
    engine
        .advance_retention_frontier(RETENTION_CONSUMER, floor.get())
        .map_err(|error| sources::map_engine_error(&error))
}

/// Fans one publication event into every subscription's bounded queue.
///
/// The committing thread runs this closure, so it is strictly
/// non-blocking: every lock is taken with `try_lock`, and a busy lock
/// drops the notification and counts the drop in the lock-free contention
/// counter — engine-level for the core map, per-subscription for a
/// consumer mid-poll. The queue is bounded with drop-oldest, counted in
/// the guarded state. No path here ever waits or takes a storage lock;
/// every dropped notification is reported through
/// [`SubscriptionBatch::dropped_notifications`] and costs latency only,
/// because delivery is journal-driven.
fn deliver_notification(set: &Arc<EngineSubscriptions>, event: &CommitPublicationEvent) {
    let handles: Vec<Arc<SubscriptionHandle>> = {
        let Ok(inner) = set.inner.try_lock() else {
            // The core map is busy (a concurrent open, close, or lookup):
            // drop the notification for every subscription of this engine
            // and count the drop without waiting.
            set.contention_drops.fetch_add(1, AtomicOrdering::Relaxed);
            return;
        };
        inner.cores.values().map(Arc::clone).collect()
    };
    for handle in handles {
        let Ok(mut state) = handle.state.try_lock() else {
            // A consumer is mid-poll and holds its subscription lock across
            // the journal read: drop the notification for this
            // subscription and count the drop without waiting.
            handle.contention_drops.fetch_add(1, AtomicOrdering::Relaxed);
            continue;
        };
        if state.notifications.len() >= MAX_SUBSCRIPTION_NOTIFICATIONS {
            state.notifications.pop_front();
            state.queue_full_drops = state.queue_full_drops.saturating_add(1);
        }
        state.notifications.push_back(Notification {
            sequence: event.sequence(),
            operation: event.operation_id().clone(),
        });
    }
}

/// Returns one engine's live subscription set for the conflict ingress,
/// or `None` when no subscriptions are live under that engine (SE-03).
#[allow(dead_code, reason = "observed by the SE-03 conflict ingress; exercised in tests")]
pub(crate) fn conflict_set(identity: u128) -> Option<Arc<EngineSubscriptions>> {
    let registry = ENGINE_SUBSCRIPTIONS.get_or_init(|| Mutex::new(BTreeMap::new()));
    let engines = lock_recovered(registry);
    engines.get(&identity).map(Arc::clone)
}

/// Fans one conflict observation into the bounded conflict queue of every
/// live subscription of the named tenant under one engine's set (SE-03).
///
/// The ingress discipline is the commit-notification discipline: every
/// lock is taken with `try_lock`, a busy lock drops the notification for
/// that subscription and counts the drop, and the queue is bounded with
/// drop-oldest counted drops. The tenant check is the isolation guard: a
/// conflict observed under one tenant's scope never enters another
/// tenant's subscription, even on the same engine.
#[allow(dead_code, reason = "observed by the SE-03 conflict ingress; exercised in tests")]
pub(crate) fn fan_out_conflict(
    set: &Arc<EngineSubscriptions>,
    tenant: &TenantId,
    event: &ChangeEvent,
) {
    let handles: Vec<Arc<SubscriptionHandle>> = {
        let Ok(inner) = set.inner.try_lock() else {
            set.contention_drops.fetch_add(1, AtomicOrdering::Relaxed);
            return;
        };
        inner.cores.values().map(Arc::clone).collect()
    };
    for handle in handles {
        let Ok(mut state) = handle.state.try_lock() else {
            handle.contention_drops.fetch_add(1, AtomicOrdering::Relaxed);
            continue;
        };
        if state.tenant != *tenant {
            // Tenant isolation: the observation never crosses the scope.
            continue;
        }
        if state.conflicts.len() >= super::backbone::MAX_CONFLICT_NOTIFICATIONS {
            state.conflicts.pop_front();
            state.conflict_drops = state.conflict_drops.saturating_add(1);
        }
        state.conflicts.push_back(event.clone());
    }
}

/// Validates the scope against the Engine and returns its identity.
///
/// Shared by the subscription path and the SE-03 journal replay: both
/// entry points bind every read to the scope's validated tenant before
/// the journal is paged.
pub(crate) fn validated_identity(
    engine: &Engine,
    scope: &TenantScope,
) -> Result<u128, StreamError> {
    engine.read_snapshot(scope).map_err(|error| sources::map_engine_error(&error))?;
    Ok(scope.engine_identity())
}

/// The located handle of one subscription: its engine identity, its
/// engine's set, and the handle itself.
type LocatedCore = (u128, Arc<EngineSubscriptions>, Arc<SubscriptionHandle>);

/// Looks up one subscription handle under its engine, tenant, and name.
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
    let Some(handle) = lock_recovered(&set.inner).cores.get(&key).map(Arc::clone) else {
        return Err(StreamError::InvalidSource);
    };
    Ok((identity, set, handle))
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
    let mut set_guard = lock_recovered(&set.inner);
    if let Some(handle) = set_guard.cores.get(&key).map(Arc::clone) {
        drop(set_guard);
        let mut state = lock_recovered(&handle.state);
        if state.state == SubscriptionState::Reconnecting {
            // Reattach: the preserved checkpoint and any unacknowledged
            // recorded batch survive, so delivery resends exactly what the
            // consumer never acknowledged.
            state.state = if state.pending.is_some() {
                SubscriptionState::AwaitingAcknowledgement
            } else {
                SubscriptionState::Active
            };
            return Ok(state.state.status());
        }
        // Exactly one active consumer owns a subscription at a time.
        return Err(StreamError::InvalidSource);
    }
    if set_guard.cores.len() >= MAX_SUBSCRIPTIONS_PER_ENGINE {
        // The per-commit ingress cost grows with the live subscription
        // count, so the count is bounded like the engine's own sink
        // registry.
        return Err(StreamError::InvalidSource);
    }
    let epoch = CHECKPOINT_EPOCHS.fetch_add(1, AtomicOrdering::Relaxed);
    let checkpoint = match initial_checkpoint(engine, spec, epoch) {
        Ok(checkpoint) => checkpoint,
        Err(error) => {
            drop(set_guard);
            release_engine_set_if_empty(identity, &set);
            return Err(error);
        }
    };
    // Registration precedes insertion into the live set. A concurrent
    // retention sweep therefore cannot observe a reader after it has already
    // allowed that reader's starting history to be pruned.
    if let Err(error) =
        engine.register_retention_consumer(RETENTION_CONSUMER, retention_floor(&checkpoint))
    {
        // Registration is part of opening the live set. Do not leave an empty
        // publication sink behind when durable lease admission fails.
        drop(set_guard);
        release_engine_set_if_empty(identity, &set);
        return Err(sources::map_engine_error(&error));
    }
    let handle = Arc::new(SubscriptionHandle {
        state: Mutex::new(SubscriptionCore {
            #[cfg(test)]
            name: spec.name().to_owned(),
            tenant: scope.tenant().clone(),
            filter: spec.filter().clone(),
            max_events: spec.max_events(),
            state: SubscriptionState::Active,
            checkpoint,
            pending: None,
            notifications: VecDeque::new(),
            queue_full_drops: 0,
            conflicts: VecDeque::new(),
            conflict_drops: 0,
        }),
        contention_drops: AtomicU64::new(0),
    });
    set_guard.cores.insert(key, handle);
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
        match engine.servable_history_start().map_err(|error| sources::map_engine_error(&error))? {
            Some(start) => StorageSequence::new(start.get().saturating_sub(1)),
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
    let (identity, set, handle) = find_core(engine, scope, name)?;
    if cancellation.is_cancelled() {
        // Cancellation closes promptly: the core and its buffers are
        // released and the subscription reports Closed(Cancelled) from
        // then on.
        drop(handle);
        remove_core(engine, identity, &set, scope, name);
        return Err(StreamError::Cancelled);
    }
    let mut state = lock_recovered(&handle.state);
    #[cfg(test)]
    wedge_point(&state);
    if state.state == SubscriptionState::Reconnecting {
        // A reconnecting subscription must reattach through subscribe.
        return Err(StreamError::InvalidSource);
    }
    if let Some(recorded) = &state.pending {
        // At-least-once: the recorded boundary is redelivered verbatim
        // until the caller acknowledges it.
        return Ok(recorded.clone());
    }
    // The ingress counters are read outside the durable state: they are
    // lock-free, and an engine-level drop applies to every subscription of
    // the engine because no subscription received the notification.
    let ingress_drops = handle.contention_drops.load(AtomicOrdering::Relaxed)
        + set.contention_drops.load(AtomicOrdering::Relaxed);
    let batch = build_batch(engine, scope, &mut state, ingress_drops)?;
    state.pending = Some(batch.clone());
    state.state = SubscriptionState::AwaitingAcknowledgement;
    Ok(batch)
}

/// Builds one bounded batch from the durable publication journal.
///
/// The scan pages the journal after the checkpoint in storage-sequence
/// order, expanding every reconstructable write into one change event,
/// until the batch is full, the journal is drained, or the scan budget is
/// exhausted. The recorded boundary is the position of the last event the
/// batch either delivered or reported as a filtered drop — the advance is
/// reported, never silent — so a poll always makes progress past a run of
/// filtered events, and the durable checkpoint still moves only when the
/// caller acknowledges the boundary. An accepted event that would exceed
/// the spec's bound stops the scan without moving the boundary past
/// itself, so it is delivered by the next batch.
///
/// Pending conflict observations are drained into the same bounded batch
/// (SE-03): delivered when the filter accepts them, counted as a reported
/// drop otherwise. They are consumed at build time and redelivered
/// verbatim with an unacknowledged batch.
fn build_batch(
    engine: &Engine,
    scope: &TenantScope,
    core: &mut SubscriptionCore,
    ingress_drops: u64,
) -> Result<SubscriptionBatch, StreamError> {
    let checkpoint = core.checkpoint.clone();
    let mut after = if checkpoint.write_index > 0 {
        // Re-read the commit the checkpoint splits so its tail is served.
        StorageSequence::new(checkpoint.sequence.get().saturating_sub(1))
    } else {
        checkpoint.sequence
    };
    let page_limit = core.max_events.saturating_add(1);
    let max_events = usize::try_from(core.max_events).unwrap_or(usize::MAX);
    let mut events: Vec<ChangeEvent> = Vec::new();
    let mut conflicts: Vec<ChangeEvent> = Vec::new();
    let mut boundary = checkpoint.clone();
    let mut filtered_dropped = 0_u64;
    let mut examined = 0_usize;

    // Drain the bounded conflict queue first: advisory events, not
    // journal-ordered, so they never interleave with the sequence-ordered
    // journal events.
    while conflicts.len() < max_events {
        let Some(front) = core.conflicts.front() else {
            break;
        };
        if core.filter.accepts_event(front) {
            conflicts.push(front.clone());
        } else {
            // The filtered conflict is a reported progress fact, consumed
            // with the boundary this batch records.
            filtered_dropped += 1;
        }
        core.conflicts.pop_front();
    }

    'pages: while examined < MAX_SUBSCRIPTION_SCAN_EVENTS {
        let page = engine
            .tenant_commits(&core.tenant, after, page_limit)
            .map_err(|error| sources::map_engine_error(&error))?;
        let Some(last) = page.last() else {
            // The journal is drained past the checkpoint.
            break;
        };
        for commit in &page {
            for (write_index, event) in expand_commit_for(scope, commit).into_iter().enumerate() {
                let index = u32::try_from(write_index).unwrap_or(u32::MAX);
                if checkpoint.covers(commit.sequence(), index) {
                    continue;
                }
                examined += 1;
                let position = CheckpointInner {
                    sequence: commit.sequence(),
                    write_index: index.saturating_add(1),
                    epoch: checkpoint.epoch,
                };
                if core.filter.accepts_event(&event) {
                    if events.len() >= max_events {
                        // The batch is full at an accepted event: leave it
                        // for the next batch, with the boundary exactly at
                        // the last accounted event.
                        break 'pages;
                    }
                    events.push(event);
                    boundary = position;
                } else {
                    // The drop is a reported progress fact, so the
                    // boundary may cover it: the advance is reported in
                    // this batch and the durable checkpoint still moves
                    // only at the acknowledgement seam.
                    filtered_dropped += 1;
                    boundary = position;
                }
                if examined >= MAX_SUBSCRIPTION_SCAN_EVENTS {
                    break 'pages;
                }
            }
        }
        // The page was exhausted without filling the batch: page the
        // journal forward and keep scanning within the budget.
        after = last.sequence();
    }
    Ok(SubscriptionBatch::new(
        events,
        conflicts,
        SubscriptionCheckpoint { inner: boundary },
        filtered_dropped,
        core.queue_full_drops.saturating_add(core.conflict_drops).saturating_add(ingress_drops),
    ))
}

/// Expands one published commit into candidate change events (SE-03
/// generalization: the scope supplies the tenant and engine identity, so
/// the live subscription scan and journal replay share one expansion).
///
/// A no-op delete — an identifier that existed nowhere — produces no
/// event: nothing changed.
pub(crate) fn expand_commit_for(scope: &TenantScope, commit: &PublishedCommit) -> Vec<ChangeEvent> {
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
        let mut event = ChangeEvent::new(
            kind,
            scope.clone(),
            collection,
            document,
            commit.operation_id().clone(),
            commit.sequence(),
        );
        // The family change payload is derived here — the one expansion the
        // live scan and the journal replay share — so both paths carry the
        // identical body for the same journal position (GAP-F02).
        if let Some(payload) = super::payloads::derive(write) {
            event = event.with_payload(payload);
        }
        candidates.push(event);
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
    let (_identity, set, handle) = find_core_by_epoch(engine, scope, acknowledgement)?;
    let mut state = lock_recovered(&handle.state);
    let confirmed = acknowledgement.confirmed().inner.clone();
    if let Some(recorded) = &state.pending
        && recorded.checkpoint().inner == confirmed
    {
        // The acknowledgement seam is the only place the durable
        // checkpoint advances — over delivered events and reported drops
        // alike, because a reported drop was accounted to the consumer.
        state.checkpoint = confirmed;
        state.pending = None;
        state.state = SubscriptionState::Active;
        let status = state.state.status();
        drop(state);
        // The shared storage row is the minimum across all live readers. A
        // faster reader can acknowledge freely without releasing history the
        // slower reader still needs.
        advance_retention_floor(engine, &set)?;
        return Ok(status);
    }
    if state.pending.is_none() && state.checkpoint == confirmed {
        // An idempotent re-acknowledgement of the current checkpoint.
        return Ok(SubscriptionStatus::Active);
    }
    Err(StreamError::InvalidCursor)
}

/// Resolves the handle an acknowledgement addresses.
///
/// The acknowledgement carries an opaque checkpoint whose epoch is unique
/// per subscription incarnation, so the epoch alone identifies the handle.
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
    let inner = lock_recovered(&set.inner);
    for handle in inner.cores.values() {
        let state = lock_recovered(&handle.state);
        if state.tenant == *scope.tenant() && state.checkpoint.epoch == epoch {
            drop(state);
            return Ok((identity, Arc::clone(&set), Arc::clone(handle)));
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
    let (_identity, _set, handle) = find_core(engine, scope, name)?;
    let state = lock_recovered(&handle.state);
    Ok(state.state.status())
}

/// Marks one subscription reconnecting.
pub(crate) fn disconnect(
    engine: &Engine,
    scope: &TenantScope,
    name: &str,
) -> Result<SubscriptionStatus, StreamError> {
    let (_identity, _set, handle) = find_core(engine, scope, name)?;
    let mut state = lock_recovered(&handle.state);
    state.state = SubscriptionState::Reconnecting;
    Ok(state.state.status())
}

/// Closes one subscription terminally and releases its buffers.
///
/// The W4-STREAM close reason is conflated with cancellation
/// (`Closed(Cancelled)`) because the frozen `StreamCompletion` set has no
/// dedicated close distinction; recorded as a tracked parent decision in
/// the design record.
pub(crate) fn close(
    engine: &Engine,
    scope: &TenantScope,
    name: &str,
) -> Result<SubscriptionStatus, StreamError> {
    let (identity, set, _handle) = find_core(engine, scope, name)?;
    remove_core(engine, identity, &set, scope, name);
    Ok(SubscriptionStatus::Closed(StreamCompletion::Cancelled))
}

/// Removes one core from its engine set, releasing the notification queue.
fn remove_core(
    engine: &Engine,
    identity: u128,
    set: &Arc<EngineSubscriptions>,
    scope: &TenantScope,
    name: &str,
) {
    let key = (scope.tenant().as_str().to_owned(), name.to_owned());
    lock_recovered(&set.inner).cores.remove(&key);
    let has_readers = !lock_recovered(&set.inner).cores.is_empty();
    if has_readers {
        // A closing reader may have been the minimum. Recompute before the
        // next sweep can release its retained history.
        let _ = advance_retention_floor(engine, set);
    } else {
        // No live reader remains, so the optional lease must not pin history
        // indefinitely across a restart.
        let _ = engine.retire_retention_consumer(RETENTION_CONSUMER);
    }
    release_engine_set_if_empty(identity, set);
}
