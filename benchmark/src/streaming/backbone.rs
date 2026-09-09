//! The public change-event backbone (SE-03): journal-backed replay from an
//! arbitrary checkpoint and the bounded conflict-observation ingress.
//!
//! The backbone extends the W4-STREAM subscription seam to the V1 Changes
//! API surface without changing its delivery discipline:
//!
//! - **Replay is journal-driven and stateless.**
//!   [`StreamStore::replay_changes`](super::StreamStore::replay_changes)
//!   resolves every batch from the durable publication-history journal
//!   ([`Engine::tenant_commits`](crate::engine::Engine::tenant_commits)) —
//!   the same source of truth the live subscriptions deliver from — so any
//!   consumer, attached or not, reproduces the exact live event sequence
//!   from an arbitrary [`ChangeCheckpoint`]. A live subscription's
//!   acknowledged boundary is itself a replayable checkpoint through
//!   [`SubscriptionCheckpoint::journal_position`]
//!   (`super::SubscriptionCheckpoint::journal_position`).
//! - **Conflict observations are live notifications, never journal
//!   events.** A conflict is not a commit: the journal has no conflict
//!   record, so conflict events enter through the crate-internal
//!   [`notify_conflict`] ingress — bounded queues, `try_lock` only,
//!   counted drops, never a wait — and ride subscription batches as
//!   advisory [`SubscriptionBatch::conflicts`](super::SubscriptionBatch::conflicts)
//!   entries. They are not checkpointed and not replayable; that limit is
//!   the backbone's recorded accepted-difference from replaying them.
//! - **Tenant isolation is structural.** Replay pages the journal under
//!   the scope's validated tenant, and the conflict ingress fans out only
//!   to subscriptions of the same tenant. No event crosses a tenant scope.
//!
//! V1 parity selector mappings and their recorded limits live on
//! [`ChangeFilter`](super::ChangeFilter).
//!
//! - **Trigger notifications are public.**
//!   [`StreamStore::change_notifications`](super::StreamStore::change_notifications)
//!   generalizes the in-process notification hints into the public engine
//!   seam (PD-2026-08-26-13 D3): a
//!   [`ChangeNotification`] is a content-free two-identifier hint — the
//!   storage sequence and the operation identity of one published commit —
//!   that wakes a consumer to pull deltas from its checkpoint. The tap's
//!   queue is bounded with drop-oldest, every drop is counted and reported,
//!   and the ingress runs on the committing thread under `try_lock` only,
//!   so it never blocks a commit. Hints are engine-scoped and carry no
//!   tenant dimension; the tenant-facing fan-out lives behind the adapter
//!   seams (SE-08/SE-14 with the W5 authorization rules).

use std::{
    collections::{BTreeMap, VecDeque},
    sync::{
        Arc, Mutex, PoisonError,
        atomic::{AtomicU64, Ordering as AtomicOrdering},
    },
};

use super::conflict::ConflictRecord;
use crate::{
    engine::{CommitPublicationSink, Engine, SubscriptionGuard},
    identity::DatabaseUlid,
    origin_lane::OriginLaneId,
    storage::{CommitPublicationEvent, JournalKind, JournalProvenance, PublishedWrite},
    types::{
        Cancellation, CollectionName, DocumentId, DocumentValue, OperationId, StorageSequence,
        TenantId, TenantScope,
    },
};

use super::{
    ChangeEvent, ChangeFilter, StreamError, sources,
    subscription::{self, MAX_SUBSCRIPTION_SCAN_EVENTS},
};

/// The bound of one subscription's conflict-notification queue (SE-03).
///
/// Conflict observations ride the same bounded-queue discipline as commit
/// notifications: a consumer that has not drained this many conflict
/// events has the oldest dropped and counted, and the drop is reported
/// through [`SubscriptionBatch::dropped_notifications`](super::SubscriptionBatch::dropped_notifications).
/// A dropped conflict notification is lost as a notification — conflicts
/// are not journaled — so the count is a delivery fact, not only a latency
/// one. The value matches the commit-notification bound; making it
/// per-deployment configuration is a later disposition.
#[allow(dead_code, reason = "observed by the SE-03 conflict ingress; exercised in tests")]
pub const MAX_CONFLICT_NOTIFICATIONS: usize = 64;

/// A public position in the durable publication-history journal (SE-03).
///
/// The checkpoint names an exclusive journal cursor: everything at or
/// before `(sequence, write_index)` — the `write_index` writes of the
/// commit at `sequence`, and every earlier commit — counts as consumed.
/// [`ChangeReplayRequest::after`](ChangeReplayRequest::after) resumes
/// exactly after it. A checkpoint carries no engine binding: it is the
/// caller's position, and replay validates it against the scope it is
/// replayed under.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ChangeCheckpoint {
    sequence: u64,
    write_index: u32,
}

impl ChangeCheckpoint {
    /// Names the journal position after `write_index` writes of the commit
    /// at `sequence`.
    #[must_use]
    pub const fn after(sequence: u64, write_index: u32) -> Self {
        Self { sequence, write_index }
    }

    /// Returns the storage sequence of the checkpoint's commit.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Returns the write count within that commit the checkpoint has
    /// consumed.
    #[must_use]
    pub const fn write_index(&self) -> u32 {
        self.write_index
    }
}

/// One journal-replay request (SE-03).
///
/// The request carries the V1-parity selector, the checkpoint to resume
/// after (`None` names the servable beginning — the oldest publication
/// history the journal can still serve), and the batch bound.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChangeReplayRequest {
    filter: ChangeFilter,
    after: Option<ChangeCheckpoint>,
    limit: u32,
}

impl ChangeReplayRequest {
    /// Creates a validated replay request.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidRange`](crate::operations::RequestValidationError::InvalidRange)
    /// for a zero or over-limit batch bound.
    pub fn try_new(
        filter: ChangeFilter,
        after: Option<ChangeCheckpoint>,
        limit: u32,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        if limit == 0 || limit > super::MAX_STREAM_BATCH_ITEMS {
            return Err(crate::operations::RequestValidationError::InvalidRange);
        }
        Ok(Self { filter, after, limit })
    }

    /// Returns the V1-parity selector.
    #[must_use]
    pub const fn filter(&self) -> &ChangeFilter {
        &self.filter
    }

    /// Returns the checkpoint this request resumes after, or `None` for the
    /// servable beginning.
    #[must_use]
    pub const fn after(&self) -> Option<ChangeCheckpoint> {
        self.after
    }

    /// Returns the replay batch bound.
    #[must_use]
    pub const fn limit(&self) -> u32 {
        self.limit
    }
}

/// One bounded journal-replay batch (SE-03).
///
/// The batch carries at most the request's limit in delivered events, the
/// checkpoint that resumes exactly after everything it examined —
/// delivered events and reported drops alike — the count of events the
/// selector dropped at or below that boundary, and whether the scan
/// reached the journal's end.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChangeReplayBatch {
    events: Vec<ChangeEvent>,
    next: Option<ChangeCheckpoint>,
    filtered_dropped: u64,
    drained: bool,
}

/// One document mutation reconstructed from the durable publication journal.
///
/// This is the content-bearing companion to [`ChangeEvent`], used by the
/// server's sync materializer. It is never emitted as a notification. A put
/// carries the exact published value, while a delete carries no body.
#[derive(Clone, Debug, PartialEq)]
pub enum SyncJournalMutation {
    /// The complete published document value.
    Put(DocumentValue),
    /// The published document deletion.
    Delete,
}

/// One document record reconstructed from the durable publication journal.
///
/// The record keeps both the storage sequence and the exact write position
/// within that commit, plus the commit's replication provenance (D-35): the
/// cross-region tailer filters on it so replicated rows never re-ship.
/// Consumers advance only after applying this record, so an in-memory
/// delivery cache can never create a revision on its own.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncJournalRecord {
    before: crate::storage::JournalBeforeImage,
    position: ChangeCheckpoint,
    operation_id: OperationId,
    collection: CollectionName,
    document: DocumentId,
    mutation: SyncJournalMutation,
    version: u64,
    provenance: JournalProvenance,
}

impl SyncJournalRecord {
    /// Returns the predecessor supplied by view replay. Ordinary replication
    /// replay leaves this unknown and pays no predecessor lookup cost.
    #[must_use]
    pub const fn before_image(&self) -> &crate::storage::JournalBeforeImage {
        &self.before
    }
    /// Returns the exclusive durable cursor after this write.
    #[must_use]
    pub const fn position(&self) -> ChangeCheckpoint {
        self.position
    }

    /// Returns the commit's global storage sequence.
    #[must_use]
    pub const fn storage_sequence(&self) -> u64 {
        self.position.sequence()
    }

    /// Returns the stable operation identity of the published commit.
    #[must_use]
    pub const fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the changed collection.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the changed document identity.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the published mutation.
    #[must_use]
    pub const fn mutation(&self) -> &SyncJournalMutation {
        &self.mutation
    }

    /// Reconstructs a published put with its durable document version. Delete
    /// records return `None`; their identity still names the row to remove.
    #[must_use]
    pub fn published_document(&self) -> Option<crate::types::Document> {
        let SyncJournalMutation::Put(value) = &self.mutation else { return None };
        let mut document = crate::types::Document::new(
            self.document.clone(),
            self.collection.clone(),
            value.clone(),
        );
        document.set_version(self.version);
        Some(document)
    }

    /// Returns the commit's replication provenance (D-35).
    #[must_use]
    pub const fn provenance(&self) -> &JournalProvenance {
        &self.provenance
    }
}

/// One bounded page of document records reconstructed from the durable
/// publication journal.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncJournalBatch {
    records: Vec<SyncJournalRecord>,
    next: Option<ChangeCheckpoint>,
    drained: bool,
}

/// The immutable view identity bound to a multi-lane sync cursor.
///
/// A cursor is accepted only for the exact admitted database view that
/// created it. Route changes therefore cannot silently reinterpret a cursor
/// against a different set of physical lanes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SyncJournalBinding {
    tenant: TenantId,
    project: String,
    database: String,
    database_identity: DatabaseUlid,
    database_incarnation: u128,
    route_generation: u64,
    selector_digest: [u8; 32],
}

impl SyncJournalBinding {
    /// Creates the binding supplied by the admitted serving owner.
    #[must_use]
    pub fn new(
        tenant: TenantId,
        project: String,
        database: String,
        database_identity: DatabaseUlid,
        database_incarnation: u128,
        route_generation: u64,
        selector_digest: [u8; 32],
    ) -> Self {
        Self {
            tenant,
            project,
            database,
            database_identity,
            database_incarnation,
            route_generation,
            selector_digest,
        }
    }

    /// Returns the bound tenant.
    #[must_use]
    pub const fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the bound Project name.
    #[must_use]
    pub fn project(&self) -> &str {
        &self.project
    }

    /// Returns the bound database name.
    #[must_use]
    pub fn database(&self) -> &str {
        &self.database
    }

    /// Returns the immutable database identity.
    #[must_use]
    pub const fn database_identity(&self) -> DatabaseUlid {
        self.database_identity
    }

    /// Returns the admitted database incarnation.
    #[must_use]
    pub const fn database_incarnation(&self) -> u128 {
        self.database_incarnation
    }

    /// Returns the route generation used to enumerate lanes.
    #[must_use]
    pub const fn route_generation(&self) -> u64 {
        self.route_generation
    }

    /// Returns the canonical shared-view selector digest.
    #[must_use]
    pub const fn selector_digest(&self) -> [u8; 32] {
        self.selector_digest
    }
}

/// Per-lane durable cursor for a multi-file sync replay.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SyncJournalCursor {
    binding: SyncJournalBinding,
    lanes: BTreeMap<OriginLaneId, ChangeCheckpoint>,
}

impl SyncJournalCursor {
    /// Creates a cursor at the beginning of every currently enumerated lane.
    #[must_use]
    pub fn beginning(
        binding: SyncJournalBinding,
        lanes: impl IntoIterator<Item = OriginLaneId>,
    ) -> Self {
        Self {
            binding,
            lanes: lanes.into_iter().map(|lane| (lane, ChangeCheckpoint::after(0, 0))).collect(),
        }
    }

    /// Creates a cursor from accepted per-lane checkpoints.
    #[must_use]
    pub fn from_lanes(
        binding: SyncJournalBinding,
        lanes: BTreeMap<OriginLaneId, ChangeCheckpoint>,
    ) -> Self {
        Self { binding, lanes }
    }

    /// Returns the immutable cursor binding.
    #[must_use]
    pub const fn binding(&self) -> &SyncJournalBinding {
        &self.binding
    }

    /// Returns the accepted checkpoint for each physical lane.
    #[must_use]
    pub const fn lanes(&self) -> &BTreeMap<OriginLaneId, ChangeCheckpoint> {
        &self.lanes
    }

    /// Validates this cursor against the currently admitted view binding.
    /// Route changes and any scope/selector mismatch require a fresh snapshot
    /// rather than silently migrating independent lane positions.
    pub(crate) fn validate_binding(
        &self,
        expected: &SyncJournalBinding,
    ) -> Result<(), StreamError> {
        (self.binding == *expected).then_some(()).ok_or(StreamError::InvalidCursor)
    }
}

/// One record tagged with the physical lane that supplied it.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncJournalLaneRecord {
    lane: OriginLaneId,
    record: SyncJournalRecord,
}

impl SyncJournalLaneRecord {
    /// Creates one lane-tagged record from an admitted lane page.
    #[must_use]
    pub fn new(lane: OriginLaneId, record: SyncJournalRecord) -> Self {
        Self { lane, record }
    }

    /// Returns the physical lane identity.
    #[must_use]
    pub const fn lane(&self) -> &OriginLaneId {
        &self.lane
    }

    /// Returns the durable record.
    #[must_use]
    pub const fn record(&self) -> &SyncJournalRecord {
        &self.record
    }
}

/// A bounded merge result over the pages currently available from each lane.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncJournalLaneBatch {
    records: Vec<SyncJournalLaneRecord>,
    cursor: SyncJournalCursor,
    drained: bool,
}

/// One bounded page read from a physical lane, including its lane-local
/// continuation so no-op or provenance-filtered rows still advance safely.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncJournalLanePage {
    records: Vec<SyncJournalRecord>,
    next: Option<ChangeCheckpoint>,
    drained: bool,
}

impl SyncJournalLanePage {
    /// Creates one lane page from the participant replay seam.
    #[must_use]
    pub fn new(
        records: Vec<SyncJournalRecord>,
        next: Option<ChangeCheckpoint>,
        drained: bool,
    ) -> Self {
        Self { records, next, drained }
    }
}

impl SyncJournalLaneBatch {
    /// Returns records in deterministic order within the available pages.
    #[must_use]
    pub fn records(&self) -> &[SyncJournalLaneRecord] {
        &self.records
    }

    /// Returns the cursor after the records returned by this batch.
    #[must_use]
    pub const fn cursor(&self) -> &SyncJournalCursor {
        &self.cursor
    }

    /// Reports whether every supplied lane page was exhausted.
    #[must_use]
    pub const fn drained(&self) -> bool {
        self.drained
    }
}

/// Merges bounded pages from independent physical lanes.
///
/// The ordering is deterministic for the pages available in this call. It is
/// deliberately not presented as a global chronology across records that may
/// arrive from later lane reads. Replicated staging rows are excluded; their
/// original provenance remains eligible on the originating lane exactly once.
pub fn merge_sync_journal_pages(
    cursor: &SyncJournalCursor,
    pages: BTreeMap<OriginLaneId, SyncJournalLanePage>,
    limit: u32,
) -> Result<SyncJournalLaneBatch, StreamError> {
    if limit == 0 || limit > super::MAX_STREAM_BATCH_ITEMS {
        return Err(StreamError::InvalidSource);
    }
    let mut candidates: Vec<SyncJournalLaneRecord> = pages
        .iter()
        .flat_map(|(lane, page)| {
            let checkpoint =
                cursor.lanes.get(&lane).copied().unwrap_or(ChangeCheckpoint::after(0, 0));
            page.records.iter().filter_map(move |record| {
                if record.provenance().kind() != JournalKind::Origin
                    || subscription::position_covered(
                        checkpoint.sequence(),
                        checkpoint.write_index(),
                        StorageSequence::new(record.storage_sequence()),
                        record.position().write_index().saturating_sub(1),
                    )
                {
                    return None;
                }
                Some(SyncJournalLaneRecord::new(lane.clone(), record.clone()))
            })
        })
        .collect();
    candidates.sort_by(|left, right| {
        (
            left.record.provenance().hlc(),
            left.lane.as_str(),
            left.record.provenance().origin_sequence(),
            left.record.storage_sequence(),
            left.record.position().write_index(),
        )
            .cmp(&(
                right.record.provenance().hlc(),
                right.lane.as_str(),
                right.record.provenance().origin_sequence(),
                right.record.storage_sequence(),
                right.record.position().write_index(),
            ))
    });
    candidates.truncate(usize::try_from(limit).unwrap_or(usize::MAX));
    let mut next = cursor.clone();
    let mut selected_lanes = std::collections::BTreeSet::new();
    for candidate in &candidates {
        next.lanes.insert(candidate.lane.clone(), candidate.record.position());
        selected_lanes.insert(candidate.lane.clone());
    }
    for (lane, page) in &pages {
        if !selected_lanes.contains(lane) && page.next.is_some() {
            next.lanes.insert(lane.clone(), page.next.expect("checked above"));
        }
    }
    let drained = pages.values().all(|page| page.drained);
    Ok(SyncJournalLaneBatch { records: candidates, cursor: next, drained })
}

#[cfg(test)]
mod lane_merge_tests {
    use super::*;
    use crate::{
        identity::DatabaseUlid,
        types::{CollectionName, DocumentId, OperationId, RegionId, TenantId},
    };

    fn lane(region: &str, incarnation: u128) -> OriginLaneId {
        let region = RegionId::try_new(region).expect("valid Region");
        OriginLaneId::try_new(region, incarnation).expect("valid lane")
    }

    fn record(sequence: u64, hlc: u64, operation: &str) -> SyncJournalRecord {
        SyncJournalRecord {
            before: crate::storage::JournalBeforeImage::Unknown,
            position: ChangeCheckpoint::after(sequence, 1),
            operation_id: OperationId::try_new(operation).expect("valid operation"),
            collection: CollectionName::try_new("users").expect("valid collection"),
            document: DocumentId::try_new(operation).expect("valid document"),
            mutation: SyncJournalMutation::Delete,
            version: 0,
            provenance: JournalProvenance::new(
                JournalKind::Origin,
                format!("lane-{operation}"),
                sequence,
                hlc,
            ),
        }
    }

    fn binding() -> SyncJournalBinding {
        SyncJournalBinding::new(
            TenantId::try_new("tenant").expect("valid tenant"),
            "project".into(),
            "database".into(),
            DatabaseUlid::generate(),
            1,
            0,
            [0; 32],
        )
    }

    #[test]
    fn equal_local_sequences_from_independent_lanes_are_both_applied() {
        let first = lane("GRU", 1);
        let second = lane("SFO", 2);
        let cursor = SyncJournalCursor::beginning(binding(), [first.clone(), second.clone()]);
        let pages = BTreeMap::from([
            (
                first.clone(),
                SyncJournalLanePage::new(
                    vec![record(1, 10, "one")],
                    Some(ChangeCheckpoint::after(1, 1)),
                    true,
                ),
            ),
            (
                second.clone(),
                SyncJournalLanePage::new(
                    vec![record(1, 11, "two")],
                    Some(ChangeCheckpoint::after(1, 1)),
                    true,
                ),
            ),
        ]);
        let merged = merge_sync_journal_pages(&cursor, pages, 2).expect("merge succeeds");
        assert_eq!(merged.records().len(), 2);
        assert_eq!(merged.cursor().lanes()[&first], ChangeCheckpoint::after(1, 1));
        assert_eq!(merged.cursor().lanes()[&second], ChangeCheckpoint::after(1, 1));
    }

    #[test]
    fn bounded_merge_leaves_unselected_lane_at_its_prior_cursor() {
        let first = lane("GRU", 3);
        let second = lane("SFO", 4);
        let cursor = SyncJournalCursor::beginning(binding(), [first.clone(), second.clone()]);
        let pages = BTreeMap::from([
            (
                first.clone(),
                SyncJournalLanePage::new(
                    vec![record(1, 10, "one")],
                    Some(ChangeCheckpoint::after(1, 1)),
                    true,
                ),
            ),
            (
                second.clone(),
                SyncJournalLanePage::new(
                    vec![record(1, 20, "two")],
                    Some(ChangeCheckpoint::after(1, 1)),
                    true,
                ),
            ),
        ]);
        let merged = merge_sync_journal_pages(&cursor, pages, 1).expect("merge succeeds");
        assert_eq!(merged.records().len(), 1);
        let advanced = merged.records()[0].lane().clone();
        let retained = if advanced == first { second } else { first };
        assert_eq!(merged.cursor().lanes()[&retained], ChangeCheckpoint::after(0, 0));
    }

    #[test]
    fn sync_merge_throughput_probe() {
        const LANES: usize = 8;
        const RECORDS_PER_LANE: u64 = 1_024;
        let lanes: Vec<_> = (0..LANES)
            .map(|index| lane(if index % 2 == 0 { "GRU" } else { "SFO" }, index as u128 + 1))
            .collect();
        let cursor = SyncJournalCursor::beginning(binding(), lanes.clone());
        let pages = lanes
            .iter()
            .enumerate()
            .map(|(lane_index, lane_id)| {
                let records = (0..RECORDS_PER_LANE)
                    .map(|index| {
                        record(
                            index + 1,
                            index + (lane_index as u64 * RECORDS_PER_LANE),
                            &format!("probe-{lane_index}-{index}"),
                        )
                    })
                    .collect();
                (lane_id.clone(), SyncJournalLanePage::new(records, None, true))
            })
            .collect();
        let started = std::time::Instant::now();
        let merged =
            merge_sync_journal_pages(&cursor, pages, (LANES as u32) * (RECORDS_PER_LANE as u32))
                .expect("merge succeeds");
        let elapsed = started.elapsed().as_secs_f64();
        let records = (LANES as u64) * RECORDS_PER_LANE;
        assert_eq!(merged.records().len(), records as usize);
        println!(
            "sync merge probe: {:.0} records/s ({:.1} us/record)",
            records as f64 / elapsed,
            elapsed * 1e6 / records as f64
        );
    }
}

impl SyncJournalBatch {
    /// Returns the records in durable journal order.
    #[must_use]
    pub fn records(&self) -> &[SyncJournalRecord] {
        &self.records
    }

    /// Returns the cursor after every write this page examined.
    #[must_use]
    pub const fn next_checkpoint(&self) -> Option<ChangeCheckpoint> {
        self.next
    }

    /// Reports whether the scan reached the journal's current end.
    #[must_use]
    pub const fn drained(&self) -> bool {
        self.drained
    }
}

impl ChangeReplayBatch {
    /// Assembles one batch from its typed parts. Crate-only.
    pub(crate) fn new(
        events: Vec<ChangeEvent>,
        next: Option<ChangeCheckpoint>,
        filtered_dropped: u64,
        drained: bool,
    ) -> Self {
        Self { events, next, filtered_dropped, drained }
    }

    /// Returns the delivered events, at most the request's bound, strictly
    /// ordered by [`StorageSequence`](crate::types::StorageSequence).
    #[must_use]
    pub fn events(&self) -> &[ChangeEvent] {
        &self.events
    }

    /// Returns the checkpoint that resumes exactly after the last event
    /// this batch delivered or reported as a filtered drop, or `None` when
    /// the journal held nothing past the request's position.
    #[must_use]
    pub const fn next_checkpoint(&self) -> Option<ChangeCheckpoint> {
        self.next
    }

    /// Returns the number of events the selector dropped at or below this
    /// batch's boundary.
    #[must_use]
    pub const fn filtered_dropped(&self) -> u64 {
        self.filtered_dropped
    }

    /// Reports whether the scan reached the journal's end: every servable
    /// publication after the request's position was examined.
    #[must_use]
    pub const fn drained(&self) -> bool {
        self.drained
    }
}

/// Replays one bounded batch of the change-event sequence from the
/// journal (SE-03).
///
/// The scan pages [`Engine::tenant_commits`] under the scope's validated
/// tenant in storage-sequence order, expands every reconstructable write
/// into one change event, and stops at the request's bound, the scan
/// budget, or the journal's end — mirroring the live subscription's
/// `build_batch` so both paths observe an identical sequence.
pub(crate) fn replay(
    engine: &Engine,
    scope: &TenantScope,
    request: &ChangeReplayRequest,
    cancellation: &Cancellation,
) -> Result<ChangeReplayBatch, StreamError> {
    // The caller's live signal is checked before any work: a cancelled
    // replay closes promptly without touching the journal.
    if cancellation.is_cancelled() {
        return Err(StreamError::Cancelled);
    }
    subscription::validated_identity(engine, scope)?;

    let (cursor_sequence, cursor_write_index) = match request.after {
        Some(checkpoint) => (checkpoint.sequence(), checkpoint.write_index()),
        None => {
            // The servable beginning: the same resume rule the live
            // subscription path applies, so both entry points serve the
            // identical first event.
            match engine
                .servable_history_start()
                .map_err(|error| sources::map_engine_error(&error))?
            {
                Some(start) => (start.get().saturating_sub(1), 0),
                _ => (0, 0),
            }
        }
    };

    let limit_index = usize::try_from(request.limit).unwrap_or(usize::MAX);
    let page_limit = request.limit.saturating_add(1);
    let mut events: Vec<ChangeEvent> = Vec::new();
    let mut filtered_dropped = 0_u64;
    let mut examined = 0_usize;
    let mut boundary: Option<ChangeCheckpoint> = None;
    let mut drained = false;
    // Resume mid-commit by re-reading the split commit; the cursor's write
    // count skips exactly its already-consumed prefix.
    let mut after = if cursor_write_index > 0 {
        StorageSequence::new(cursor_sequence.saturating_sub(1))
    } else {
        StorageSequence::new(cursor_sequence)
    };

    'pages: while examined < MAX_SUBSCRIPTION_SCAN_EVENTS {
        let page = engine
            .tenant_commits(scope.tenant(), after, page_limit)
            .map_err(|error| sources::map_engine_error(&error))?;
        let Some(last) = page.last() else {
            // The journal is drained past the cursor.
            drained = true;
            break;
        };
        for commit in &page {
            for (write_index, event) in
                subscription::expand_commit_for(scope, commit).into_iter().enumerate()
            {
                let index = u32::try_from(write_index).unwrap_or(u32::MAX);
                if subscription::position_covered(
                    cursor_sequence,
                    cursor_write_index,
                    commit.sequence(),
                    index,
                ) {
                    continue;
                }
                examined += 1;
                let position =
                    ChangeCheckpoint::after(commit.sequence().get(), index.saturating_add(1));
                if request.filter.accepts_event(&event) {
                    if events.len() >= limit_index {
                        // The batch is full at an accepted event: leave it
                        // for the next batch, with the boundary exactly at
                        // the last accounted event.
                        break 'pages;
                    }
                    events.push(event);
                    boundary = Some(position);
                } else {
                    // The drop is a reported progress fact, so the
                    // boundary may cover it.
                    filtered_dropped += 1;
                    boundary = Some(position);
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
    Ok(ChangeReplayBatch::new(events, boundary, filtered_dropped, drained))
}

/// Reconstructs one bounded page of document mutations from the durable
/// publication journal for the server's sync materializer.
pub(crate) fn replay_sync_journal(
    engine: &Engine,
    scope: &TenantScope,
    after: Option<ChangeCheckpoint>,
    limit: u32,
    cancellation: &Cancellation,
) -> Result<SyncJournalBatch, StreamError> {
    if limit == 0 || limit > super::MAX_STREAM_BATCH_ITEMS {
        return Err(StreamError::InvalidSource);
    }
    if cancellation.is_cancelled() {
        return Err(StreamError::Cancelled);
    }
    subscription::validated_identity(engine, scope)?;

    let (cursor_sequence, cursor_write_index) = match after {
        Some(checkpoint) => (checkpoint.sequence(), checkpoint.write_index()),
        None => match engine
            .servable_history_start()
            .map_err(|error| sources::map_engine_error(&error))?
        {
            Some(start) => (start.get().saturating_sub(1), 0),
            _ => (0, 0),
        },
    };
    let limit_index = usize::try_from(limit).unwrap_or(usize::MAX);
    let page_limit = limit.saturating_add(1);
    let mut records = Vec::new();
    let mut examined = 0_usize;
    let mut boundary = None;
    let mut drained = false;
    let mut journal_after = if cursor_write_index > 0 {
        StorageSequence::new(cursor_sequence.saturating_sub(1))
    } else {
        StorageSequence::new(cursor_sequence)
    };

    'pages: while examined < MAX_SUBSCRIPTION_SCAN_EVENTS {
        if cancellation.is_cancelled() {
            return Err(StreamError::Cancelled);
        }
        let page = engine
            .tenant_commits(scope.tenant(), journal_after, page_limit)
            .map_err(|error| sources::map_engine_error(&error))?;
        let Some(last) = page.last() else {
            drained = true;
            break;
        };
        for commit in &page {
            for (write_index, write) in commit.writes().iter().enumerate() {
                let index = u32::try_from(write_index).unwrap_or(u32::MAX);
                if subscription::position_covered(
                    cursor_sequence,
                    cursor_write_index,
                    commit.sequence(),
                    index,
                ) {
                    continue;
                }
                let position =
                    ChangeCheckpoint::after(commit.sequence().get(), index.saturating_add(1));
                let (collection, document, mutation, version) = match write {
                    PublishedWrite::Put { document } => (
                        document.collection().clone(),
                        document.id().clone(),
                        SyncJournalMutation::Put(document.value().clone()),
                        document.metadata().version(),
                    ),
                    PublishedWrite::Delete {
                        collection: Some(collection),
                        id,
                        version_at_delete,
                    } => (
                        collection.clone(),
                        id.clone(),
                        SyncJournalMutation::Delete,
                        *version_at_delete,
                    ),
                    PublishedWrite::Delete { collection: None, .. } => {
                        boundary = Some(position);
                        examined += 1;
                        continue;
                    }
                };
                if records.len() >= limit_index {
                    break 'pages;
                }
                records.push(SyncJournalRecord {
                    before: crate::storage::JournalBeforeImage::Unknown,
                    position,
                    operation_id: commit.operation_id().clone(),
                    collection,
                    document,
                    mutation,
                    version,
                    provenance: commit.provenance().clone(),
                });
                boundary = Some(position);
                examined += 1;
                if examined >= MAX_SUBSCRIPTION_SCAN_EVENTS {
                    break 'pages;
                }
            }
        }
        journal_after = last.sequence();
    }
    Ok(SyncJournalBatch { records, next: boundary, drained })
}

pub(crate) fn replay_view_journal(
    engine: &Engine,
    scope: &TenantScope,
    after: Option<ChangeCheckpoint>,
    limit: u32,
    cancellation: &Cancellation,
) -> Result<SyncJournalBatch, StreamError> {
    let mut batch = replay_sync_journal(engine, scope, after, limit, cancellation)?;
    for record in &mut batch.records {
        if cancellation.is_cancelled() {
            return Err(StreamError::Cancelled);
        }
        record.before = engine
            .journal_before_image(
                scope.tenant(),
                record.collection(),
                record.document(),
                StorageSequence::new(record.storage_sequence()),
            )
            .map_err(|error| sources::map_engine_error(&error))?;
    }
    Ok(batch)
}

/// Offers one conflict observation to the change-event backbone (SE-03).
///
/// The observation becomes a [`ChangeKind::Conflict`] event — carrying
/// only the record's content-free metadata — and enters the bounded
/// conflict-notification queue of every live subscription of this tenant
/// under the scope's engine. The ingress never waits: a busy queue bound
/// or a contended lock drops the notification and counts the drop. A
/// record without operation identities carries no stable identity and
/// produces no event.
///
/// Crate-only: conflict observations originate from the replication
/// seam's conflict recording; this function is the typed ingress that
/// ticket wires in.
#[allow(
    dead_code,
    reason = "the replication seam wires this ingress in its ticket; exercised in tests"
)]
pub(crate) fn notify_conflict(
    scope: &TenantScope,
    sequence: StorageSequence,
    record: ConflictRecord,
) {
    let Some(operation) = record.operations().first().cloned() else {
        // No conflicting operation identity: nothing stable identifies the
        // observation, so no event is minted. The replication seam records
        // the conflicting operations in every conflict it reports.
        return;
    };
    let Some(set) = subscription::conflict_set(scope.engine_identity()) else {
        // No live subscriptions under this engine: nothing to notify.
        return;
    };
    let event = ChangeEvent::conflict(scope.clone(), operation, sequence, record);
    subscription::fan_out_conflict(&set, scope.tenant(), &event);
}

// ---------------------------------------------------------------------------
// SE-03: public trigger notifications (PD-2026-08-26-13 D3)
// ---------------------------------------------------------------------------

/// The largest queue bound one [`ChangeNotificationTap`] may request.
///
/// This is the tap's memory bound: a tap never holds more than this many
/// two-identifier hints, and a publication beyond it drops the oldest hint
/// and counts the drop. Any positive bound keeps the contract's guarantees
/// — a dropped hint costs one wake-up, never correctness, because the
/// journal serves the data — so the value is a configured tracer-scale
/// limit like the subscription bounds, not a correctness constant.
pub const MAX_CHANGE_NOTIFICATION_CAPACITY: u32 = 1024;

/// The public Change Notification (PD-2026-08-26-13 D3): the small, typed,
/// content-free trigger event the engine emits on commit publication.
///
/// The hint carries exactly two identifiers — the storage sequence and the
/// stable operation identity of one published commit — and nothing else:
/// there is no field through which document data could cross. A consumer
/// that receives one responds by pulling delta batches from its checkpoint
/// (the journal replay or its subscription), never by reading the hint as
/// data.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChangeNotification {
    sequence: StorageSequence,
    operation: OperationId,
}

impl ChangeNotification {
    /// Assembles one hint from a publication's facts. Crate-only: hints
    /// originate from commit publication alone.
    pub(crate) fn new(sequence: StorageSequence, operation: OperationId) -> Self {
        Self { sequence, operation }
    }

    /// Returns the storage sequence of the publication the hint names.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the stable operation identity of the publication the hint
    /// names.
    #[must_use]
    pub const fn operation_id(&self) -> &OperationId {
        &self.operation
    }
}

/// The bounded hint queue one tap's sink and consumer share.
///
/// `dropped` lives outside the mutex so the committing thread can count a
/// lock-contention drop without taking any lock.
struct NotificationQueue {
    inner: Mutex<VecDeque<ChangeNotification>>,
    capacity: usize,
    dropped: AtomicU64,
}

/// One consumer's bounded handle on the engine's Change Notifications
/// (PD-2026-08-26-13 D3).
///
/// The tap is engine-scoped: it is acquired through one
/// [`Engine`](crate::engine::Engine)'s
/// [`StreamStore`](super::StreamStore), receives that engine's
/// publications as content-free hints, and detaches its sink when dropped.
/// Hints carry no tenant dimension, so a tap is a trusted in-engine
/// consumer surface — the TBEP subscription layer and the live-view host
/// fan out per tenant behind their own authorization rules; a tap itself
/// is not a tenant-facing authorization boundary.
pub struct ChangeNotificationTap {
    queue: Arc<NotificationQueue>,
    /// Drop-only: detaches this tap's sink from the engine's publication
    /// registry. The leading underscore marks a guard whose `Drop` is its
    /// only effect.
    _guard: SubscriptionGuard,
}

/// Debug names the tap's delivery-health fact alone: the guard and queue
/// are machinery, and a tap's observable state is its drop count.
impl std::fmt::Debug for ChangeNotificationTap {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("ChangeNotificationTap")
            .field("dropped", &self.dropped())
            .finish_non_exhaustive()
    }
}

impl ChangeNotificationTap {
    /// Pops the oldest undelivered hint, or `None` when the queue is
    /// empty.
    ///
    /// Draining never blocks the engine: the sink takes this queue's lock
    /// with `try_lock` only, so a hint arriving mid-drain is dropped and
    /// counted rather than waited on.
    #[must_use]
    pub fn try_pop(&self) -> Option<ChangeNotification> {
        self.queue.inner.lock().unwrap_or_else(PoisonError::into_inner).pop_front()
    }

    /// Returns the cumulative number of hints this tap dropped: queue-full
    /// drops of the oldest hint beyond the bound and lock-contention drops
    /// at ingress alike.
    ///
    /// A dropped hint costs one wake-up, never correctness: the journal —
    /// not the hint — is the source of truth, so the count is a
    /// delivery-health statistic for the consumer's pull cadence.
    #[must_use]
    pub fn dropped(&self) -> u64 {
        self.queue.dropped.load(AtomicOrdering::Relaxed)
    }

    /// Takes the tap's queue lock and holds it until dropped. Test-only:
    /// the non-blocking probe needs one consumer holding the lock across a
    /// commit, which the public drain API cannot express.
    #[cfg(test)]
    pub(crate) fn hold_queue_for_test(
        &self,
    ) -> std::sync::MutexGuard<'_, VecDeque<ChangeNotification>> {
        self.queue.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Acquires one bounded Change Notification tap for one engine (SE-03).
///
/// The sink registered here runs synchronously on the committing thread,
/// so it is strictly non-blocking: the queue lock is taken with `try_lock`
/// only, a busy lock drops the hint and counts the drop without waiting,
/// and a full queue drops the oldest hint and counts that drop too. The
/// tap detaches its sink when dropped.
///
/// # Errors
///
/// Returns [`StreamError::InvalidSource`] for a zero or over-limit
/// capacity and when the engine's publication registry already holds its
/// subscriber bound.
pub(crate) fn open_tap(
    engine: &Engine,
    capacity: u32,
) -> Result<ChangeNotificationTap, StreamError> {
    if capacity == 0 || capacity > MAX_CHANGE_NOTIFICATION_CAPACITY {
        return Err(StreamError::InvalidSource);
    }
    let queue = Arc::new(NotificationQueue {
        inner: Mutex::new(VecDeque::new()),
        capacity: usize::try_from(capacity).unwrap_or(usize::MAX),
        dropped: AtomicU64::new(0),
    });
    let sink_queue = Arc::clone(&queue);
    let sink: CommitPublicationSink =
        Arc::new(move |event: &CommitPublicationEvent| deliver_hint(&sink_queue, event));
    let guard = engine.subscribe_publications(sink);
    if !guard.is_attached() {
        // The registry is at its subscriber bound; no hint can reach this
        // tap, so it is refused rather than silently deaf.
        return Err(StreamError::InvalidSource);
    }
    Ok(ChangeNotificationTap { queue, _guard: guard })
}

/// Offers one publication hint to one tap's bounded queue.
///
/// The committing thread runs this closure: `try_lock` only, counted drops
/// on a busy lock or a full queue, never a wait, never a storage lock.
fn deliver_hint(queue: &Arc<NotificationQueue>, event: &CommitPublicationEvent) {
    let Ok(mut hints) = queue.inner.try_lock() else {
        // A consumer is mid-drain and holds the queue lock: drop the hint
        // and count the drop without waiting.
        queue.dropped.fetch_add(1, AtomicOrdering::Relaxed);
        return;
    };
    if hints.len() >= queue.capacity {
        hints.pop_front();
        queue.dropped.fetch_add(1, AtomicOrdering::Relaxed);
    }
    hints.push_back(ChangeNotification::new(event.sequence(), event.operation_id().clone()));
}
