//! Durable Engine adapter for the deterministic simulation harness.
//!
//! This module is intentionally an integration adapter, not another world
//! implementation.  Every mutation is committed through a real durable
//! [`terrabase::Engine`], every cross-region delivery crosses the real typed
//! [`terrabase_member_transport::InProcessLane`], and reconciliation uses the
//! production range-summary kernel.  The small amount of bookkeeping here
//! carries the simulation's causal context alongside the transport record;
//! the current public replication record does not yet expose that context.

use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase::engine::{CommitOutcome, Engine, EngineConfig, ReplicatedRecord};
use terrabase::types::{Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability};
use terrabase_member_transport::envelope::{
    Envelope, FrameKind, LaneValue, Payload, ReplicatedWrite, decode, encode,
};
use terrabase_member_transport::{InProcessLane, MemberId, MemberTransport};
use terrabase_replication_core::anti_entropy::{JournalRowId, RangeSummaryTable, RowSummary};

use crate::faults::FaultPlan;
use crate::model::{Frontier, Hlc, JournalRow, RegionId, SessionToken, ViewCut};
use crate::traits::{ReconcileStats, ReplicationEngine, SyncEngine, ViewRevision};

const TENANT: &str = "simulation-tenant";
const COLLECTION: &str = "simulation";
const GENERATION: u64 = 1;
// Engine origin HLCs contain wall-clock millis in their high bits.  The
// adapter's deterministic source stamp must therefore sit above that range,
// otherwise a remote record would be spuriously superseded by a local write
// on one Region and replicas could diverge.
const HLC_BASE: u64 = 1 << 63;
const MAX_JOURNAL_PAGE: u32 = 4_096;
const RECONCILIATION_LIMIT: usize = 1_000_000;
static TEMP_ROOT: AtomicU64 = AtomicU64::new(1);

/// Failure opening or operating a production simulation world.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionWorldError {
    /// The world needs at least one Region.
    EmptyWorld,
    /// A durable Engine or its storage could not be opened.
    EngineUnavailable,
    /// A required simulation identifier could not be validated.
    InvalidIdentifier,
    /// A durable operation was refused or returned an uncertain outcome.
    CommitRefused,
    /// A production transport frame failed to encode or decode.
    TransportFailure,
    /// A production reconciliation identity could not be constructed.
    ReconciliationFailure,
}

struct RegionState {
    id: RegionId,
    member: MemberId,
    location: PathBuf,
    engine: Option<Engine>,
    applied: BTreeSet<(RegionId, u64)>,
    next_sequence: u64,
}

/// A deterministic world backed by durable production engines and member
/// transport.
pub struct ProductionWorld {
    root: PathBuf,
    tenant: terrabase::types::TenantId,
    faults: FaultPlan,
    step: u64,
    regions: Vec<RegionState>,
    lane: InProcessLane,
    records: BTreeMap<(RegionId, u64), JournalRow>,
    deferred: BTreeMap<RegionId, Vec<JournalRow>>,
    cache: BTreeMap<(String, u64, u64), (u64, Vec<u8>)>,
}

impl ProductionWorld {
    /// Creates a durable world under a unique temporary directory.
    pub fn new(region_count: usize) -> Result<Self, ProductionWorldError> {
        let serial = TEMP_ROOT.fetch_add(1, Ordering::Relaxed);
        let root = std::env::temp_dir().join(format!("terrabase-sim-{serial}"));
        Self::with_root(region_count, FaultPlan::new(), root)
    }

    /// Creates a durable world under the supplied fault plan and root.
    pub fn with_faults(
        region_count: usize,
        faults: FaultPlan,
    ) -> Result<Self, ProductionWorldError> {
        let serial = TEMP_ROOT.fetch_add(1, Ordering::Relaxed);
        let root = std::env::temp_dir().join(format!("terrabase-sim-{serial}"));
        Self::with_root(region_count, faults, root)
    }

    /// Creates a world at an explicit durable root. The root must be owned by
    /// this world; an existing non-empty root is refused.
    pub fn with_root(
        region_count: usize,
        faults: FaultPlan,
        root: impl Into<PathBuf>,
    ) -> Result<Self, ProductionWorldError> {
        if region_count == 0 {
            return Err(ProductionWorldError::EmptyWorld);
        }
        let root = root.into();
        std::fs::create_dir_all(&root).map_err(|_| ProductionWorldError::EngineUnavailable)?;
        if std::fs::read_dir(&root)
            .map_err(|_| ProductionWorldError::EngineUnavailable)?
            .next()
            .is_some()
        {
            return Err(ProductionWorldError::EngineUnavailable);
        }
        let tenant = terrabase::types::TenantId::try_new(TENANT)
            .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
        let mut regions = Vec::with_capacity(region_count);
        for index in 0..region_count {
            let id = RegionId::from_index(index);
            let member = MemberId::try_new(&format!("sim-member-{index}"))
                .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
            let location = root.join(format!("region-{index}"));
            let config = EngineConfig::durable(&location)
                .map_err(|_| ProductionWorldError::EngineUnavailable)?;
            let engine =
                Engine::open(config).map_err(|_| ProductionWorldError::EngineUnavailable)?;
            regions.push(RegionState {
                id,
                member,
                location,
                engine: Some(engine),
                applied: BTreeSet::new(),
                next_sequence: 0,
            });
        }
        Ok(Self {
            root,
            tenant,
            faults,
            step: 0,
            regions,
            lane: InProcessLane::new(),
            records: BTreeMap::new(),
            deferred: BTreeMap::new(),
            cache: BTreeMap::new(),
        })
    }

    /// Returns the durable root used by this world.
    #[must_use]
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// Returns the active fault plan.
    #[must_use]
    pub fn faults(&self) -> &FaultPlan {
        &self.faults
    }

    /// Returns the current deterministic transport step.
    #[must_use]
    pub const fn step_count(&self) -> u64 {
        self.step
    }

    /// Returns the production transport used by this adapter for inspection
    /// and additional fault assertions.
    #[must_use]
    pub fn transport(&self) -> &InProcessLane {
        &self.lane
    }

    /// Returns mutable access to the production lane for focused transport
    /// fault probes.
    pub fn transport_mut(&mut self) -> &mut InProcessLane {
        &mut self.lane
    }

    /// Returns the Region identities in deterministic order.
    #[must_use]
    pub fn region_ids(&self) -> Vec<RegionId> {
        self.regions.iter().map(|region| region.id).collect()
    }

    /// Restarts one durable region by closing and reopening its Engine.
    pub fn restart_region(&mut self, region: RegionId) -> Result<(), ProductionWorldError> {
        let Some(state) = self.regions.iter_mut().find(|state| state.id == region) else {
            return Err(ProductionWorldError::EngineUnavailable);
        };
        if let Some(engine) = state.engine.take() {
            engine.shutdown().map_err(|_| ProductionWorldError::EngineUnavailable)?;
        }
        let config = EngineConfig::durable(&state.location)
            .map_err(|_| ProductionWorldError::EngineUnavailable)?;
        state.engine =
            Some(Engine::open(config).map_err(|_| ProductionWorldError::EngineUnavailable)?);
        Ok(())
    }

    /// Alias for [`Self::restart_region`] used by simulation fault drivers.
    pub fn restart(&mut self, region: RegionId) -> Result<(), ProductionWorldError> {
        self.restart_region(region)
    }

    /// Primes a view's production-backed cache and returns its revision.
    pub fn prime_cache(&mut self, view: &str, region: RegionId) -> Option<ViewRevision> {
        let revision = self.view_revision(view, region)?;
        let capability = production_capability_for(view, revision.generation);
        self.cache.insert(
            (view.to_owned(), revision.generation, 0),
            (capability, revision.bytes.clone()),
        );
        Some(revision)
    }

    fn region_position(&self, id: RegionId) -> Option<usize> {
        self.regions.iter().position(|region| region.id == id)
    }

    fn member(&self, id: RegionId) -> Option<&MemberId> {
        self.regions.iter().find(|region| region.id == id).map(|region| &region.member)
    }

    fn engine(&self, id: RegionId) -> Option<&Engine> {
        self.regions.iter().find(|region| region.id == id)?.engine.as_ref()
    }

    fn row_record(row: &JournalRow) -> Result<ReplicatedRecord, ProductionWorldError> {
        let collection = terrabase::types::CollectionName::try_new(COLLECTION)
            .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
        let id = DocumentId::try_new(&format!("key-{}", row.key))
            .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
        Ok(ReplicatedRecord {
            collection,
            id,
            body: if row.tombstone {
                DocumentValue::Null
            } else {
                DocumentValue::String(
                    String::from_utf8(row.value.clone())
                        .map_err(|_| ProductionWorldError::InvalidIdentifier)?,
                )
            },
            deleted: row.tombstone,
            commit_ulid: format!("sim-r{}-{}", row.origin.index(), row.seq),
            hlc: row.hlc.value(),
            origin: format!("region-{}", row.origin.index()),
            origin_sequence: row.seq,
        })
    }

    fn lane_record(row: &JournalRow) -> Result<ReplicatedWrite, ProductionWorldError> {
        let record = Self::row_record(row)?;
        Ok(ReplicatedWrite {
            collection: terrabase_member_transport::CollectionName::try_new(COLLECTION)
                .map_err(|_| ProductionWorldError::InvalidIdentifier)?,
            id: terrabase_member_transport::DocumentId::try_new(&format!("key-{}", row.key))
                .map_err(|_| ProductionWorldError::InvalidIdentifier)?,
            body: (!row.tombstone)
                .then(|| LaneValue::String(String::from_utf8_lossy(&row.value).into_owned())),
            commit_ulid: record.commit_ulid,
            hlc: record.hlc,
            origin: record.origin,
            origin_sequence: record.origin_sequence,
        })
    }

    fn send_rows(
        &mut self,
        source: RegionId,
        target: RegionId,
    ) -> Result<(), ProductionWorldError> {
        if source == target || !self.faults.link_open(source, target, self.step) {
            return Ok(());
        }
        let Some(source_member) = self.member(source).cloned() else {
            return Ok(());
        };
        let Some(target_member) = self.member(target).cloned() else {
            return Ok(());
        };
        let target_position =
            self.region_position(target).ok_or(ProductionWorldError::EngineUnavailable)?;
        let rows: Vec<_> = self
            .records
            .values()
            .filter(|row| row.origin == source)
            .filter(|row| !self.regions[target_position].applied.contains(&row.id()))
            .cloned()
            .collect();
        if rows.is_empty() {
            return Ok(());
        }
        let records = rows.iter().map(Self::lane_record).collect::<Result<Vec<_>, _>>()?;
        let envelope = Envelope {
            kind: FrameKind::ReplicationBatch,
            message_id: self.step.saturating_add(1),
            correlation_id: None,
            causation_id: None,
            idempotency_key: None,
            source: source_member.clone(),
            target: target_member.clone(),
            issued_at: self.lane.now(),
            expires_at: None,
            range: None,
            epoch: None,
            payload: Payload::ReplicationBatch {
                tenant: terrabase_member_transport::TenantId::try_new(TENANT)
                    .map_err(|_| ProductionWorldError::InvalidIdentifier)?,
                records,
            },
        };
        let frame = encode(&envelope).map_err(|_| ProductionWorldError::TransportFailure)?;
        self.lane
            .send(&source_member, &target_member, frame)
            .map_err(|_| ProductionWorldError::TransportFailure)
    }

    fn apply_row(
        &mut self,
        target: RegionId,
        row: &JournalRow,
    ) -> Result<(), ProductionWorldError> {
        let position =
            self.region_position(target).ok_or(ProductionWorldError::EngineUnavailable)?;
        if !self.regions[position].applied.contains(&row.id()) {
            let frontier = self.applied_frontier(target);
            if !frontier.covers(&row.context) {
                let waiting = self.deferred.entry(target).or_default();
                if !waiting.iter().any(|pending| pending.id() == row.id()) {
                    waiting.push(row.clone());
                }
                return Ok(());
            }
            let Some(engine) = self.regions[position].engine.as_ref() else {
                return Ok(());
            };
            let scope = engine.scope_for(&self.tenant);
            let record = Self::row_record(row)?;
            engine
                .apply_replicated(&scope, &record)
                .map_err(|_| ProductionWorldError::EngineUnavailable)?;
            self.regions[position].applied.insert(row.id());
        }
        Ok(())
    }

    fn refresh_view(&self, view: &str, region: RegionId) -> Option<ViewRevision> {
        if view != "all" && view != "even_keys" {
            return None;
        }
        let engine = self.engine(region)?;
        let scope = engine.scope_for(&self.tenant);
        let snapshot = engine.read_snapshot(&scope).ok()?;
        let collection = terrabase::types::CollectionName::try_new(COLLECTION).ok()?;
        let documents =
            engine.snapshot_documents_in_collection(&scope, &snapshot, &collection).ok()?;
        let mut items = Vec::new();
        for document in documents {
            let key = document.id().as_str().strip_prefix("key-")?.parse::<u64>().ok()?;
            if view == "even_keys" && key % 2 != 0 {
                continue;
            }
            let mut value = BTreeMap::new();
            if let DocumentValue::String(text) = document.value() {
                value.insert("value".to_owned(), text.as_bytes().to_vec());
            }
            items.push(terrabase_sync_core::native::ResultItem::new(
                document.id().as_str().to_owned(),
                value,
            ));
        }
        let mut state = terrabase_sync_core::native::ViewState::new(Default::default());
        let sequence = engine.published_sequence().ok()?.get();
        let snapshot = terrabase_sync_core::native::Snapshot::new(
            GENERATION,
            GENERATION,
            sequence,
            items,
            1,
            u64::MAX,
        );
        state.bootstrap(snapshot).ok()?;
        let visible = state.visible_items();
        let mut bytes = Vec::new();
        for item in &visible {
            bytes.extend_from_slice(item.identity.as_bytes());
            bytes.push(0);
            for (field, value) in &item.value {
                bytes.extend_from_slice(field.as_bytes());
                bytes.push(0);
                bytes.extend_from_slice(value);
                bytes.push(0xff);
            }
        }
        let payload_digest = fnv_bytes(0xcbf2_9ce4_8422_2325, &bytes);
        Some(ViewRevision::new(
            view.to_owned(),
            GENERATION,
            ViewCut::new(self.applied_frontier(region), sequence),
            payload_digest,
            bytes,
        ))
    }
}

impl Drop for ProductionWorld {
    fn drop(&mut self) {
        for region in &mut self.regions {
            if let Some(engine) = region.engine.take() {
                let _ = engine.shutdown();
            }
        }
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

impl ReplicationEngine for ProductionWorld {
    fn submit(&mut self, region: RegionId, key: u64, value: u64) -> Option<JournalRow> {
        if self.faults.region_down(region, self.step) {
            return None;
        }
        let position = self.region_position(region)?;
        self.regions[position].next_sequence =
            self.regions[position].next_sequence.saturating_add(1);
        let seq = self.regions[position].next_sequence;
        let operation =
            terrabase::types::OperationId::try_new(&format!("sim-r{}-{}", region.index(), seq))
                .ok()?;
        let context = self.applied_frontier(region);
        let engine = self.regions[position].engine.as_ref()?;
        let scope = engine.scope_for(&self.tenant);
        let snapshot = engine.read_snapshot(&scope).ok()?;
        let id = DocumentId::try_new(&format!("key-{key}")).ok()?;
        let collection = terrabase::types::CollectionName::try_new(COLLECTION).ok()?;
        let mut transaction = engine.begin(&scope, snapshot, operation).ok()?;
        let document = Document::new(id, collection, DocumentValue::String(value.to_string()));
        transaction.put(document, ExpectedVersion::Any).ok()?;
        if !matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)) {
            return None;
        }
        let row = JournalRow {
            origin: region,
            seq,
            context,
            hlc: Hlc::new(
                HLC_BASE
                    .saturating_add(seq)
                    .saturating_add_signed(self.faults.clock_offset_ms(region, self.step)),
            ),
            key,
            value: value.to_string().into_bytes(),
            tombstone: false,
        };
        self.regions[position].applied.insert(row.id());
        self.records.insert(row.id(), row.clone());
        Some(row)
    }

    fn delete(&mut self, region: RegionId, key: u64) -> Option<JournalRow> {
        if self.faults.region_down(region, self.step) {
            return None;
        }
        let position = self.region_position(region)?;
        self.regions[position].next_sequence =
            self.regions[position].next_sequence.saturating_add(1);
        let seq = self.regions[position].next_sequence;
        let operation =
            terrabase::types::OperationId::try_new(&format!("sim-r{}-{}", region.index(), seq))
                .ok()?;
        let context = self.applied_frontier(region);
        let engine = self.regions[position].engine.as_ref()?;
        let scope = engine.scope_for(&self.tenant);
        let snapshot = engine.read_snapshot(&scope).ok()?;
        let id = DocumentId::try_new(&format!("key-{key}")).ok()?;
        let collection = terrabase::types::CollectionName::try_new(COLLECTION).ok()?;
        let mut transaction = engine.begin(&scope, snapshot, operation).ok()?;
        transaction.delete_in_collection(&collection, id, ExpectedVersion::Any);
        if !matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)) {
            return None;
        }
        let row = JournalRow {
            origin: region,
            seq,
            context,
            hlc: Hlc::new(
                HLC_BASE
                    .saturating_add(seq)
                    .saturating_add_signed(self.faults.clock_offset_ms(region, self.step)),
            ),
            key,
            value: Vec::new(),
            tombstone: true,
        };
        self.regions[position].applied.insert(row.id());
        self.records.insert(row.id(), row.clone());
        Some(row)
    }

    fn step(&mut self) {
        for left in 0..self.regions.len() {
            for right in 0..self.regions.len() {
                let source = self.regions[left].id;
                let target = self.regions[right].id;
                {
                    let control =
                        self.lane.link(&self.regions[left].member, &self.regions[right].member);
                    control.partition = !self.faults.link_open(source, target, self.step);
                    let delay = self
                        .faults
                        .link_delay_steps(source, target, self.step)
                        .max(self.faults.reorder_window(source, target, self.step));
                    control.delay_pumps = u32::try_from(delay).unwrap_or(u32::MAX);
                    control.duplicate = self.faults.duplicate_copies(source, target, self.step) > 1;
                    control.reorder = self.faults.reorder_window(source, target, self.step) > 0;
                }
                let _ = self.send_rows(source, target);
            }
        }
        self.lane.pump();
        for target in self.regions.iter().map(|region| region.id).collect::<Vec<_>>() {
            let Some(member) = self.member(target).cloned() else { continue };
            let frames =
                self.lane.receive(&member, terrabase_member_transport::FrameSelector::Deliveries);
            for frame in frames {
                let Ok((envelope, _)) = decode(&frame.bytes) else { continue };
                let Payload::ReplicationBatch { records, .. } = envelope.payload else { continue };
                for record in records {
                    let Some(row) = self
                        .records
                        .values()
                        .find(|row| {
                            row.origin.index().to_string()
                                == record.origin.strip_prefix("region-").unwrap_or("")
                                && row.seq == record.origin_sequence
                        })
                        .cloned()
                    else {
                        continue;
                    };
                    let _ = self.apply_row(target, &row);
                }
            }
            let deferred = self.deferred.remove(&target).unwrap_or_default();
            for row in deferred {
                let _ = self.apply_row(target, &row);
            }
        }
        self.step = self.step.saturating_add(1);
    }

    fn applied_frontier(&self, region: RegionId) -> Frontier {
        let mut frontier = Frontier::new();
        if let Some(state) = self.regions.iter().find(|state| state.id == region) {
            for (origin, seq) in &state.applied {
                frontier.advance(*origin, *seq);
            }
        }
        frontier
    }

    fn fingerprint(&self, region: RegionId) -> u64 {
        let Some(engine) = self.engine(region) else { return 0 };
        let scope = engine.scope_for(&self.tenant);
        let Ok(snapshot) = engine.read_snapshot(&scope) else { return 0 };
        let Ok(collection) = terrabase::types::CollectionName::try_new(COLLECTION) else {
            return 0;
        };
        let Ok(documents) = engine.snapshot_documents_in_collection(&scope, &snapshot, &collection)
        else {
            return 0;
        };
        let mut hash = 0xcbf2_9ce4_8422_2325;
        for document in documents {
            hash = fnv_bytes(hash, document.id().as_str().as_bytes());
            if let DocumentValue::String(value) = document.value() {
                hash = fnv_bytes(hash, value.as_bytes());
            }
        }
        hash
    }

    fn reconcile(&mut self, left: RegionId, right: RegionId) -> ReconcileStats {
        // The Engine's journal endpoint proves that each adapter state is
        // durable.  Identity and hash are then taken from the adapter's
        // source-row map: replicated SQLite rows intentionally have a local
        // provenance string, while anti-entropy compares the source dot.
        let make_table = |state: Option<&RegionState>| {
            let mut table = RangeSummaryTable::new();
            let Some(state) = state else { return table };
            let Some(engine) = state.engine.as_ref() else { return table };
            let scope = engine.scope_for(&self.tenant);
            let Ok(durable_rows) = engine.journal_rows(&scope, 0, MAX_JOURNAL_PAGE) else {
                return table;
            };
            if durable_rows.len() < state.applied.len() {
                return table;
            }
            for id in &state.applied {
                let Some(row) = self.records.get(id) else { continue };
                if let Ok(id) = JournalRowId::new(format!("region-{}", row.origin.index()), row.seq)
                {
                    let hash = terrabase_replication_core::anti_entropy::row_hash(&row_bytes(row));
                    table.insert(&RowSummary::new(id, hash));
                }
            }
            table
        };
        let left_table = make_table(self.regions.iter().find(|state| state.id == left));
        let right_table = make_table(self.regions.iter().find(|state| state.id == right));
        let Ok(plan) = left_table.reconcile(&right_table, RECONCILIATION_LIMIT) else {
            return ReconcileStats::new(0, 0);
        };
        let differing = plan.differences().len();
        for row in self.records.values().cloned().collect::<Vec<_>>() {
            let left_missing = self
                .regions
                .iter()
                .find(|state| state.id == left)
                .is_some_and(|state| !state.applied.contains(&row.id()));
            let right_missing = self
                .regions
                .iter()
                .find(|state| state.id == right)
                .is_some_and(|state| !state.applied.contains(&row.id()));
            if left_missing && row.origin == right {
                let _ = self.apply_row(left, &row);
            } else if right_missing && row.origin == left {
                let _ = self.apply_row(right, &row);
            }
        }
        ReconcileStats::new(usize::from(plan.rounds()), differing)
    }

    fn is_uniform(&self, row: &JournalRow, selected: &[RegionId]) -> bool {
        selected.iter().all(|region| {
            let Some(state) = self.regions.iter().find(|state| state.id == *region) else {
                return false;
            };
            state.applied.contains(&row.id()) && self.applied_frontier(*region).covers(&row.context)
        })
    }

    fn read(&self, region: RegionId, token: &SessionToken, key: u64) -> Option<Option<u64>> {
        if !self.applied_frontier(region).covers(&token.frontier)
            || self.faults.region_down(region, self.step)
        {
            return None;
        }
        let engine = self.engine(region)?;
        let scope = engine.scope_for(&self.tenant);
        let id = DocumentId::try_new(&format!("key-{key}")).ok()?;
        let collection = terrabase::types::CollectionName::try_new(COLLECTION).ok()?;
        let snapshot = engine.read_snapshot(&scope).ok()?;
        let document = engine
            .snapshot_documents_in_collection(&scope, &snapshot, &collection)
            .ok()?
            .into_iter()
            .find(|doc| doc.id() == &id);
        Some(document.and_then(|doc| match doc.value() {
            DocumentValue::String(value) => value.parse::<u64>().ok(),
            _ => None,
        }))
    }

    fn digest_after_permutation(&mut self, rows: &[JournalRow]) -> u64 {
        let serial = TEMP_ROOT.fetch_add(1, Ordering::Relaxed);
        let location = self.root.join(format!("permutation-{serial}"));
        let Ok(config) = EngineConfig::durable(&location) else { return 0 };
        let Ok(engine) = Engine::open(config) else { return 0 };
        let scope = engine.scope_for(&self.tenant);
        for row in rows {
            let Ok(record) = Self::row_record(row) else { continue };
            let _ = engine.apply_replicated(&scope, &record);
        }
        let digest = {
            let snapshot = engine.read_snapshot(&scope);
            let collection = terrabase::types::CollectionName::try_new(COLLECTION);
            match (snapshot, collection) {
                (Ok(snapshot), Ok(collection)) => engine
                    .snapshot_documents_in_collection(&scope, &snapshot, &collection)
                    .map_or(0, |docs| {
                        docs.into_iter().fold(0xcbf2_9ce4_8422_2325, |hash, doc| {
                            let hash = fnv_bytes(hash, doc.id().as_str().as_bytes());
                            match doc.value() {
                                DocumentValue::String(value) => fnv_bytes(hash, value.as_bytes()),
                                _ => hash,
                            }
                        })
                    }),
                _ => 0,
            }
        };
        let _ = engine.shutdown();
        let _ = std::fs::remove_dir_all(location);
        digest
    }

    fn is_quiescent(&self) -> bool {
        let pending_frames = self.regions.iter().any(|source| {
            self.regions.iter().any(|target| {
                let stats = self.lane.link_stats(&source.member, &target.member);
                stats.queued > 0 || stats.held > 0
            })
        });
        let pending_rows = self.regions.iter().any(|region| {
            !self.faults.region_down(region.id, self.step)
                && self.records.values().any(|row| !region.applied.contains(&row.id()))
        });
        let pending = pending_frames || pending_rows;
        !pending && self.deferred.values().all(Vec::is_empty)
    }
}

impl SyncEngine for ProductionWorld {
    fn view_revision(&self, view: &str, region: RegionId) -> Option<ViewRevision> {
        self.refresh_view(view, region)
    }

    fn verify_revision(&self, view: &str, region: RegionId) -> bool {
        let Some(revision) = self.view_revision(view, region) else { return false };
        let Some(engine) = self.engine(region) else { return false };
        let scope = engine.scope_for(&self.tenant);
        let Some(snapshot) = engine.read_snapshot(&scope).ok() else { return false };
        revision.cut.seq_hi == snapshot.sequence().get()
            && revision.payload_digest == fnv_bytes(0xcbf2_9ce4_8422_2325, &revision.bytes)
    }

    fn cached_bytes(
        &self,
        view: &str,
        generation: u64,
        offset: u64,
        capability: u64,
    ) -> Option<Vec<u8>> {
        self.cache
            .get(&(view.to_owned(), generation, offset))
            .and_then(|(expected, bytes)| (*expected == capability).then(|| bytes.clone()))
    }
}

fn fnv_bytes(mut hash: u64, bytes: &[u8]) -> u64 {
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0100_0000_01B3);
    }
    hash
}

fn row_bytes(row: &JournalRow) -> Vec<u8> {
    format!(
        "{}\0{}\0{}\0{}\0{:?}\0{}\0{}",
        row.origin.index(),
        row.seq,
        row.hlc.value(),
        row.key,
        row.value,
        row.tombstone,
        row.context
            .entries()
            .map(|(origin, sequence)| format!("{}:{sequence}", origin.index()))
            .collect::<Vec<_>>()
            .join(","),
    )
    .into_bytes()
}

/// Capability token used by the deterministic cache seam.
#[must_use]
fn production_capability_for(view: &str, generation: u64) -> u64 {
    fnv_bytes(fnv_bytes(0xcbf2_9ce4_8422_2325, view.as_bytes()), &generation.to_be_bytes())
}
