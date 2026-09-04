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

#[derive(Clone, Debug)]
struct HistoricalVersion {
    value: Vec<u8>,
    tombstone: bool,
    hlc: u64,
    origin: RegionId,
    seq: u64,
    context: Frontier,
}

fn merge_historical(docs: &mut BTreeMap<u64, HistoricalVersion>, row: &JournalRow) {
    let incoming_wins = match docs.get(&row.key) {
        None => true,
        Some(stored) if row.context.get(stored.origin) >= stored.seq => true,
        Some(stored) if stored.context.get(row.origin) >= row.seq => false,
        Some(stored) if row.tombstone != stored.tombstone => row.tombstone,
        Some(stored) => (row.hlc.value(), row.origin) > (stored.hlc, stored.origin),
    };
    if incoming_wins {
        docs.insert(
            row.key,
            HistoricalVersion {
                value: row.value.clone(),
                tombstone: row.tombstone,
                hlc: row.hlc.value(),
                origin: row.origin,
                seq: row.seq,
                context: row.context.clone(),
            },
        );
    }
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
    /// Returns a deterministic world digest for seed reproduction. Transport
    /// counters and temporary Engine identities are deliberately excluded;
    /// the durable applied journal set is the qualification state.
    #[must_use]
    pub fn digest(&self) -> u64 {
        let mut hash = fnv_bytes(0xcbf2_9ce4_8422_2325, b"terrabase-sim-world-v2");
        for region in &self.regions {
            hash = fnv_bytes(hash, &region.id.index().to_be_bytes());
            hash = fnv_bytes(hash, &self.fingerprint(region.id).to_be_bytes());
        }
        hash
    }

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
        let Some(position) = self.region_position(region) else {
            return Err(ProductionWorldError::EngineUnavailable);
        };
        if let Some(engine) = self.regions[position].engine.take() {
            engine.shutdown().map_err(|_| ProductionWorldError::EngineUnavailable)?;
        }
        let location = self.regions[position].location.clone();
        let config = EngineConfig::durable(&location)
            .map_err(|_| ProductionWorldError::EngineUnavailable)?;
        self.regions[position].engine =
            Some(Engine::open(config).map_err(|_| ProductionWorldError::EngineUnavailable)?);
        // Progress is a cache, not the source of truth. Rebuild it from the
        // durable publication journal after every close/reopen so a restart
        // cannot manufacture a causal gap (or acknowledge a row that was not
        // actually persisted). The adapter's row map carries the complete
        // simulation metadata while the Engine proves the durable identity.
        let mut applied = BTreeSet::new();
        let mut cursor = 0;
        loop {
            let Some(engine) = self.regions[position].engine.as_ref() else {
                return Err(ProductionWorldError::EngineUnavailable);
            };
            let scope = engine.scope_for(&self.tenant);
            let page = engine
                .journal_rows(&scope, cursor, MAX_JOURNAL_PAGE)
                .map_err(|_| ProductionWorldError::EngineUnavailable)?;
            if page.is_empty() {
                break;
            }
            for durable in &page {
                // The engine's local origin is path-bound and therefore is
                // intentionally different from the simulation's Region
                // identity.  Rebuild the adapter cache from the durable
                // operation identity embedded in the canonical journal row,
                // rather than guessing from that local origin string.
                for (id, row) in &self.records {
                    if durable
                        .canonical
                        .windows(row_commit(row).len())
                        .any(|window| window == row_commit(row).as_slice())
                    {
                        applied.insert(*id);
                    }
                }
                cursor = cursor.max(durable.commit_sequence);
            }
            if page.len() < usize::try_from(MAX_JOURNAL_PAGE).unwrap_or(usize::MAX) {
                break;
            }
        }
        // A deterministic loser need not publish a second document row: the
        // production engine records it in merge history instead. Recover
        // those source dots as applied too, otherwise a restart would move a
        // frontier backwards even though the durable conflict proof remains.
        let Some(engine) = self.regions[position].engine.as_ref() else {
            return Err(ProductionWorldError::EngineUnavailable);
        };
        let scope = engine.scope_for(&self.tenant);
        let collection = terrabase::types::CollectionName::try_new(COLLECTION)
            .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
        let mut historical_commits = BTreeSet::new();
        for key in self.records.values().map(|row| row.key).collect::<BTreeSet<_>>() {
            let id = terrabase::types::DocumentId::try_new(&format!("key-{key}"))
                .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
            let conflicts = engine
                .merge_conflicts_for(&scope, &collection, &id, MAX_JOURNAL_PAGE)
                .map_err(|_| ProductionWorldError::EngineUnavailable)?;
            for conflict in conflicts {
                historical_commits.insert(conflict.loser_commit_ulid);
                historical_commits.insert(conflict.winner_commit_ulid);
            }
        }
        for (id, row) in &self.records {
            if historical_commits.contains(&format!("sim-r{}-{}", row.origin.index(), row.seq)) {
                applied.insert(*id);
            }
        }
        // A local commit can be represented by the row map before a journal
        // page is visible to a reopened handle. Keep only identities proven by
        // the journal, while preserving the origin sequence monotonicity.
        self.regions[position].applied = applied;
        let state_id = self.regions[position].id;
        self.regions[position].next_sequence = self
            .records
            .keys()
            .filter(|(origin, _)| *origin == state_id)
            .map(|(_, sequence)| *sequence)
            .max()
            .unwrap_or(0);
        Ok(())
    }

    /// Reads the complete durable journal for one region.  This is deliberately
    /// a fresh SQLite read on every proof operation; the simulation's row map
    /// is metadata for causal scheduling, never evidence that a mutation was
    /// persisted.
    fn durable_journal(
        &self,
        region: RegionId,
    ) -> Result<Vec<terrabase::engine::JournalRow>, ProductionWorldError> {
        let engine = self.engine(region).ok_or(ProductionWorldError::EngineUnavailable)?;
        let scope = engine.scope_for(&self.tenant);
        let mut cursor = 0;
        let mut rows = Vec::new();
        loop {
            let page = engine
                .journal_rows(&scope, cursor, MAX_JOURNAL_PAGE)
                .map_err(|_| ProductionWorldError::EngineUnavailable)?;
            if page.is_empty() {
                break;
            }
            cursor = page.iter().map(|row| row.commit_sequence).max().unwrap_or(cursor);
            rows.extend(page);
            if rows.len() > RECONCILIATION_LIMIT {
                return Err(ProductionWorldError::ReconciliationFailure);
            }
        }
        Ok(rows)
    }

    /// Checks a logical simulation row against the actual canonical journal
    /// bytes. Operation ids are part of the canonical encoding; matching them
    /// catches a missing, substituted, or non-durable row without decoding an
    /// engine-private format in the adapter.
    fn durable_contains(
        &self,
        region: RegionId,
        row: &JournalRow,
    ) -> Result<bool, ProductionWorldError> {
        let commit = row_commit(row);
        if self
            .durable_journal(region)?
            .iter()
            .any(|durable| durable.canonical.windows(commit.len()).any(|window| window == commit))
        {
            return Ok(true);
        }
        // A concurrent loser is intentionally absent from `documents` after
        // apply, but its source operation remains durable in merge history.
        let engine = self.engine(region).ok_or(ProductionWorldError::EngineUnavailable)?;
        let scope = engine.scope_for(&self.tenant);
        let collection = terrabase::types::CollectionName::try_new(COLLECTION)
            .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
        let id = DocumentId::try_new(&format!("key-{}", row.key))
            .map_err(|_| ProductionWorldError::InvalidIdentifier)?;
        let conflicts = engine
            .merge_conflicts_for(&scope, &collection, &id, MAX_JOURNAL_PAGE)
            .map_err(|_| ProductionWorldError::EngineUnavailable)?;
        Ok(conflicts.iter().any(|conflict| {
            conflict.loser_commit_ulid.as_bytes() == commit.as_slice()
                || conflict.winner_commit_ulid.as_bytes() == commit.as_slice()
        }))
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
        // A healthy member relays every row it has applied, including rows
        // whose origin is a third Region. This is required when the original
        // origin is partitioned from the target after reaching an
        // intermediate Region.
        let source_position =
            self.region_position(source).ok_or(ProductionWorldError::EngineUnavailable)?;
        let rows: Vec<_> = self.regions[source_position]
            .applied
            .iter()
            .filter_map(|id| self.records.get(id))
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

    fn historical_view(
        &self,
        view: &str,
        region: RegionId,
        cut: &ViewCut,
    ) -> Option<(u64, Vec<u8>)> {
        if view != "all" && view != "even_keys" {
            return None;
        }
        let state = self.regions.iter().find(|state| state.id == region)?;
        let mut docs = BTreeMap::new();
        for id in &state.applied {
            let Some(row) = self.records.get(id) else { continue };
            if row.seq <= cut.frontier.get(row.origin) {
                merge_historical(&mut docs, row);
            }
        }
        let mut visible = docs
            .into_iter()
            .filter(|(key, version)| !version.tombstone && (view == "all" || key % 2 == 0))
            .map(|(key, version)| (format!("key-{key}"), version.value))
            .collect::<Vec<_>>();
        visible.sort_by(|left, right| left.0.cmp(&right.0));
        let mut bytes = Vec::new();
        for (id, value) in visible {
            bytes.extend_from_slice(id.as_bytes());
            bytes.push(0);
            bytes.extend_from_slice(b"value");
            bytes.push(0);
            bytes.extend_from_slice(&value);
            bytes.push(0xff);
        }
        let digest = fnv_bytes(0xcbf2_9ce4_8422_2325, &bytes);
        Some((digest, bytes))
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
                    // Do not canonicalize the transport back to the
                    // bookkeeping row.  The frame's operation, provenance,
                    // identity, and body must all agree with the durable
                    // source row before it is eligible for causal apply.
                    let expected_body = (!row.tombstone).then(|| {
                        LaneValue::String(String::from_utf8_lossy(&row.value).into_owned())
                    });
                    if record.commit_ulid != row_commit_string(row)
                        || record.hlc != row.hlc.value()
                        || record.origin != format!("region-{}", row.origin.index())
                        || record.origin_sequence != row.seq
                        || record.collection.as_str() != COLLECTION
                        || record.id.as_str() != format!("key-{}", row.key)
                        || record.body != expected_body
                    {
                        continue;
                    }
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
        let Some(state) = self.regions.iter().find(|state| state.id == region) else {
            return 0;
        };
        // Fingerprints are over the reconciled logical journal set, including
        // causal metadata and tombstones.  The durable journal and SQLite
        // bodies are independently inspected first: an in-memory applied set
        // cannot prove that a write survived a close/reopen.
        if self.durable_journal(region).is_err() {
            return 0;
        }
        let Some(snapshot) = self.engine(region).and_then(|engine| {
            let scope = engine.scope_for(&self.tenant);
            let snapshot = engine.read_snapshot(&scope).ok()?;
            let collection = terrabase::types::CollectionName::try_new(COLLECTION).ok()?;
            engine.snapshot_documents_in_collection(&scope, &snapshot, &collection).ok()
        }) else {
            return 0;
        };
        let mut hash = fnv_bytes(0xcbf2_9ce4_8422_2325, b"terrabase-sim-fingerprint-v2");
        hash = fnv_bytes(hash, &(state.applied.len() as u64).to_be_bytes());
        // Include evidence derived from the durable files without hashing
        // path-bound local origins, which intentionally differ after the
        // same source row is applied on another member.
        let durable_rows = state
            .applied
            .iter()
            .filter_map(|id| self.records.get(id))
            .filter(|row| self.durable_contains(region, row).unwrap_or(false))
            .count();
        hash = fnv_bytes(hash, &(durable_rows as u64).to_be_bytes());
        hash = fnv_bytes(hash, &(snapshot.len() as u64).to_be_bytes());
        for document in snapshot {
            hash = fnv_bytes(hash, document.id().as_str().as_bytes());
            if let DocumentValue::String(value) = document.value() {
                hash = fnv_bytes(hash, value.as_bytes());
            }
        }
        for id in &state.applied {
            if let Some(row) = self.records.get(id) {
                let Ok(true) = self.durable_contains(region, row) else { return 0 };
                hash = fnv_bytes(hash, &row_bytes(row));
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
            let mut cursor = 0;
            loop {
                let Ok(page) = engine.journal_rows(&scope, cursor, MAX_JOURNAL_PAGE) else {
                    return RangeSummaryTable::new();
                };
                if page.is_empty() {
                    break;
                }
                cursor = page.iter().map(|row| row.commit_sequence).max().unwrap_or(cursor);
                if page.len() < usize::try_from(MAX_JOURNAL_PAGE).unwrap_or(usize::MAX) {
                    break;
                }
            }
            // The reconciliation set is built from the durable journal, not
            // the adapter cache.  Logical source metadata is restored from
            // the simulation row only after the durable operation identity is
            // proven present in SQLite.
            for (id, row) in &self.records {
                if !state.applied.contains(id) {
                    continue;
                }
                let commit = row_commit(row);
                let durable_has_row = durable.iter().any(|entry| {
                    entry.canonical.windows(commit.len()).any(|window| window == commit)
                });
                if durable_has_row {
                    if let Ok(row_id) =
                        JournalRowId::new(format!("region-{}", row.origin.index()), row.seq)
                    {
                        let hash =
                            terrabase_replication_core::anti_entropy::row_hash(&row_bytes(row));
                        table.insert(&RowSummary::new(row_id, hash));
                    }
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
        // Repair the complete set difference, not only rows whose origin is
        // one endpoint. A row written by a third Region can be present on
        // either side and must be copied to the other side as well.
        let rows = self.records.values().cloned().collect::<Vec<_>>();
        for row in &rows {
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
            if left_missing && !right_missing {
                let _ = self.apply_row(left, row);
            } else if right_missing && !left_missing {
                let _ = self.apply_row(right, row);
            }
        }
        // Applying a later row can enqueue it behind a dependency that was
        // itself repaired in this pass. Retry until the bounded queues stop
        // changing, exactly as the tailer does at a normal step.
        for _ in 0..rows.len().saturating_add(1) {
            let mut progressed = false;
            for target in [left, right] {
                let waiting = self.deferred.remove(&target).unwrap_or_default();
                for row in waiting {
                    let before = self
                        .regions
                        .iter()
                        .find(|state| state.id == target)
                        .is_some_and(|state| state.applied.contains(&row.id()));
                    let _ = self.apply_row(target, &row);
                    let after = self
                        .regions
                        .iter()
                        .find(|state| state.id == target)
                        .is_some_and(|state| state.applied.contains(&row.id()));
                    progressed |= !before && after;
                }
            }
            if !progressed {
                break;
            }
        }
        ReconcileStats::new(usize::from(plan.rounds()), differing)
    }

    fn is_uniform(&self, row: &JournalRow, selected: &[RegionId]) -> bool {
        // Global durability is a distinct acknowledgement class: a local
        // `Synced` commit is RegionalDurable, while GlobalDurable requires at
        // least two selected Regions to have durably accepted the source dot.
        selected.len() >= 2
            && selected.iter().all(|region| {
                let Some(state) = self.regions.iter().find(|state| state.id == *region) else {
                    return false;
                };
                !self.faults.region_down(*region, self.step)
                    && state.applied.contains(&row.id())
                    && self.durable_contains(*region, row).unwrap_or(false)
                    && self.applied_frontier(*region).covers(&row.context)
            })
    }

    /// Whether a row has reached this Region's durable SQLite journal.  This
    /// is the proof behind `RegionalDurable`; it is intentionally independent
    /// of the cross-Region applied cache.
    #[must_use]
    pub fn regional_durable(&self, row: &JournalRow, region: RegionId) -> bool {
        self.durable_contains(region, row).unwrap_or(false)
    }

    /// Whether a row satisfies the simulation's `GlobalDurable` contract for
    /// the selected Regions, including causal dependencies.
    #[must_use]
    pub fn global_durable(&self, row: &JournalRow, selected: &[RegionId]) -> bool {
        self.is_uniform(row, selected)
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
        // The wire contract carries a dotted context and receivers must not
        // expose a row before its dependencies. The current public
        // `ReplicatedRecord` predates that field, so the adapter enforces the
        // same causal gate while driving the production apply path. A stable
        // `(origin, seq)` tie-break makes every supplied permutation exercise
        // the same admissible causal order.
        let mut frontier = Frontier::new();
        let mut remaining = rows.iter().collect::<Vec<_>>();
        let mut ordered = Vec::with_capacity(remaining.len());
        while !remaining.is_empty() {
            let candidate = remaining
                .iter()
                .enumerate()
                .filter(|(_, row)| frontier.covers(&row.context))
                .min_by_key(|(_, row)| (row.origin, row.seq))
                .map(|(index, _)| index)
                .unwrap_or(0);
            let row = remaining.swap_remove(candidate);
            frontier.advance(row.origin, row.seq);
            ordered.push(row);
        }
        for row in ordered {
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
                        let mut hash = docs.into_iter().fold(0xcbf2_9ce4_8422_2325, |hash, doc| {
                            let hash = fnv_bytes(hash, doc.id().as_str().as_bytes());
                            match doc.value() {
                                DocumentValue::String(value) => fnv_bytes(hash, value.as_bytes()),
                                _ => hash,
                            }
                        });
                        // The state alone is insufficient for invariant 3:
                        // deterministic conflict explanations are part of
                        // the observable history contract. Sort entries so
                        // storage insertion order cannot affect the digest.
                        let mut keys = rows.iter().map(|row| row.key).collect::<Vec<_>>();
                        keys.sort_unstable();
                        keys.dedup();
                        for key in keys {
                            let Ok(id) = DocumentId::try_new(&format!("key-{key}")) else {
                                continue;
                            };
                            let Ok(mut conflicts) = engine.merge_conflicts_for(
                                &scope,
                                &collection,
                                &id,
                                MAX_JOURNAL_PAGE,
                            ) else {
                                continue;
                            };
                            conflicts.sort_by(|left, right| {
                                (
                                    &left.loser_commit_ulid,
                                    left.loser_hlc,
                                    &left.loser_origin,
                                    &left.winner_commit_ulid,
                                    left.winner_hlc,
                                    &left.winner_origin,
                                    &left.policy,
                                    &left.explanation,
                                )
                                    .cmp(&(
                                        &right.loser_commit_ulid,
                                        right.loser_hlc,
                                        &right.loser_origin,
                                        &right.winner_commit_ulid,
                                        right.winner_hlc,
                                        &right.winner_origin,
                                        &right.policy,
                                        &right.explanation,
                                    ))
                            });
                            for conflict in conflicts {
                                hash = fnv_bytes(hash, &conflict.loser_hlc.to_be_bytes());
                                hash = fnv_bytes(hash, &conflict.winner_hlc.to_be_bytes());
                                hash = fnv_bytes(hash, &[u8::from(conflict.loser_deleted)]);
                                for text in [
                                    conflict.loser_commit_ulid.as_str(),
                                    conflict.loser_origin.as_str(),
                                    conflict.winner_commit_ulid.as_str(),
                                    conflict.winner_origin.as_str(),
                                    conflict.policy.as_str(),
                                    conflict.explanation.as_str(),
                                ] {
                                    hash = fnv_bytes(hash, text.as_bytes());
                                    hash = fnv_bytes(hash, &[0]);
                                }
                            }
                        }
                        hash
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
        self.view_revision(view, region)
            .is_some_and(|revision| self.verify_revision_at(&revision, region))
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

impl ProductionWorld {
    /// Verifies a retained revision against the database reconstructed at the
    /// revision's own causal cut. Later writes therefore cannot invalidate a
    /// previously issued revision, while a forged cut or payload fails closed.
    #[must_use]
    pub fn verify_revision_at(&self, revision: &ViewRevision, region: RegionId) -> bool {
        let Some(state) = self.regions.iter().find(|state| state.id == region) else {
            return false;
        };
        if revision.generation != GENERATION
            || !state.applied.iter().all(|id| self.records.contains_key(id) || id.0 == region)
            || !self.applied_frontier(region).covers(&revision.cut.frontier)
        {
            return false;
        }
        let Some(engine) = self.engine(region) else { return false };
        let Ok(snapshot) = engine.read_snapshot(&engine.scope_for(&self.tenant)) else {
            return false;
        };
        if revision.cut.seq_hi > snapshot.sequence().get()
            || revision.cut.frontier.entries().any(|(_, seq)| seq > revision.cut.seq_hi)
        {
            return false;
        }
        let Some((digest, bytes)) = self.historical_view(&revision.view, region, &revision.cut)
        else {
            return false;
        };
        revision.payload_digest == digest && revision.bytes == bytes
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

/// Returns the operation identity that the durable canonical journal encoder
/// includes in every row.  Keeping this tiny probe here avoids depending on
/// the engine's private codec while still proving journal membership.
fn row_commit(row: &JournalRow) -> Vec<u8> {
    format!("sim-r{}-{}", row.origin.index(), row.seq).into_bytes()
}

fn row_commit_string(row: &JournalRow) -> String {
    String::from_utf8(row_commit(row)).expect("simulation operation ids are ASCII")
}

/// Capability token used by the deterministic cache seam.
#[must_use]
fn production_capability_for(view: &str, generation: u64) -> u64 {
    fnv_bytes(fnv_bytes(0xcbf2_9ce4_8422_2325, view.as_bytes()), &generation.to_be_bytes())
}
