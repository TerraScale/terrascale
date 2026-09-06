//! Bounded range reconciliation over authenticated, placement-fenced member links.
//! Proofs describe retained source publications; they never authorize durability.
use std::collections::{BTreeMap, BTreeSet, VecDeque};

use prost::Message;
use terrabase::engine::{Engine, ReplicatedPublication, ReplicationRepairCut};
use terrabase::types::TenantId;
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_member_transport::identity::MemberId;
use terrabase_replication_core::anti_entropy::{
    JournalRange, JournalRowId, RangeSummaryTable, RowSummary,
};

use crate::{host::HostingError, registry::BoundScope};

/// Socket dispatcher discriminator.
pub const MAGIC: &[u8; 4] = b"TBRP";
const MAX_BYTES: usize = 512 * 1024;
const MAX_ORIGINS: usize = 256;
const MAX_ROWS: usize = 4096;
const MAX_WORK: usize = 4096;
const MAX_REPAIRS: usize = 256;
const RETRY_MS: u64 = 2_000;
const PERIOD_MS: u64 = 30_000;

#[derive(Clone, PartialEq, Message)]
struct Entry {
    #[prost(string, tag = "1")]
    origin: String,
    #[prost(uint32, tag = "2")]
    level: u32,
    #[prost(uint64, tag = "3")]
    first: u64,
    #[prost(uint64, tag = "4")]
    count: u64,
    #[prost(bytes = "vec", tag = "5")]
    hash: Vec<u8>,
    #[prost(uint64, tag = "6")]
    floor: u64,
    #[prost(uint64, tag = "7")]
    cutoff: u64,
}
#[derive(Clone, PartialEq, Message)]
struct Frame {
    #[prost(string, tag = "1")]
    source: String,
    #[prost(string, tag = "2")]
    target: String,
    #[prost(uint64, tag = "3")]
    id: u64,
    // 1 catalog request; 2 catalog response; 3 range request;
    // 4 equal; 5 children; 6 leaf; 7 request publications.
    #[prost(uint32, tag = "4")]
    kind: u32,
    #[prost(message, repeated, tag = "5")]
    entries: Vec<Entry>,
    #[prost(uint64, tag = "6")]
    cut: u64,
}

fn refused(message: &str) -> HostingError {
    HostingError::Refused(message.to_owned())
}
fn failure(error: impl std::fmt::Debug) -> HostingError {
    HostingError::EngineFailure(format!("publication repair: {error:?}"))
}

/// Bounded metadata for a durable, versioned publication-summary cut.
/// Publication hashes and hierarchical fingerprints remain in SQLite.
#[derive(Clone, Default)]
pub struct PublicationIndex {
    table: RangeSummaryTable,
    floors: BTreeMap<String, u64>,
    frontier: BTreeMap<String, u64>,
    cut: Option<ReplicationRepairCut>,
    tenant: Option<TenantId>,
    cursor: u64,
}
impl PublicationIndex {
    /// Captures current durable summary metadata without scanning publication history.
    /// # Errors
    /// Rejects invalid scopes, unavailable summaries or excessive origin cardinality.
    pub fn refresh(&mut self, engine: &Engine, bound: &BoundScope) -> Result<bool, HostingError> {
        bound.validate().map_err(failure)?;
        let tenant = TenantId::try_new(&bound.tenant).map_err(failure)?;
        if self.tenant.as_ref().is_some_and(|previous| previous != &tenant) {
            return Err(refused("repair index tenant changed"));
        }
        let scope = engine.scope_for(&tenant);
        let cut = engine.replication_repair_cut(&scope).map_err(failure)?;
        if cut.frontier().len() > MAX_ORIGINS {
            return Err(refused("repair origin bound exceeded"));
        }
        self.frontier = cut.frontier().clone();
        self.floors = self.frontier.keys().map(|origin| (origin.clone(), 1)).collect();
        self.cursor = cut.local_sequence();
        self.cut = Some(cut);
        self.tenant = Some(tenant);
        Ok(true)
    }
    /// Fetches one original group selected by a verified repair request.
    /// # Errors
    /// Rejects a foreign scope or unavailable source publication.
    pub fn publication(
        &self,
        engine: &Engine,
        bound: &BoundScope,
        origin: &str,
        sequence: u64,
    ) -> Result<ReplicatedPublication, HostingError> {
        if self.tenant.as_ref().is_none_or(|tenant| tenant.as_str() != bound.tenant) {
            return Err(refused("repair publication scope mismatch"));
        }
        let mut groups = self.publications(engine, origin, sequence, sequence)?;
        if groups.len() != 1 {
            return Err(refused("repair publication requires snapshot"));
        }
        Ok(groups.remove(0))
    }
    fn publications(
        &self,
        engine: &Engine,
        origin: &str,
        first: u64,
        last: u64,
    ) -> Result<Vec<ReplicatedPublication>, HostingError> {
        let cut = self.cut.as_ref().ok_or_else(|| refused("uncaptured repair index"))?;
        let tenant = self.tenant.as_ref().ok_or_else(|| refused("unbound repair index"))?;
        let last = last.min(self.frontier.get(origin).copied().unwrap_or(0));
        if last < first {
            return Ok(Vec::new());
        }
        engine
            .replication_range_publications(&engine.scope_for(tenant), cut, origin, first, last)
            .map_err(failure)
    }
    /// Records a proven retained floor; any pruned prefix requires native bootstrap.
    pub fn set_retained_floor(&mut self, origin: String, first_available: u64) {
        self.floors.insert(origin, first_available);
    }
    fn at_frontier(&self, frontier: &BTreeMap<String, u64>) -> Self {
        let mut view = self.clone();
        view.frontier = frontier
            .iter()
            .map(|(origin, last)| {
                (origin.clone(), (*last).min(self.frontier.get(origin).copied().unwrap_or(0)))
            })
            .collect();
        view
    }
    fn summary(&self, engine: &Engine, range: &JournalRange) -> Result<Entry, HostingError> {
        let last = range.last().min(self.frontier.get(range.origin()).copied().unwrap_or(0));
        let fingerprint = if last < range.first() {
            Default::default()
        } else {
            let cut = self.cut.as_ref().ok_or_else(|| refused("uncaptured repair index"))?;
            let tenant = self.tenant.as_ref().ok_or_else(|| refused("unbound repair index"))?;
            engine
                .replication_range_summary(
                    &engine.scope_for(tenant),
                    cut,
                    range.origin(),
                    range.first(),
                    last,
                )
                .map_err(failure)?
        };
        Ok(Entry {
            origin: range.origin().to_owned(),
            level: u32::from(range.level()),
            first: range.first(),
            count: fingerprint.count,
            hash: fingerprint.sum.to_be_bytes().to_vec(),
            floor: 0,
            cutoff: 0,
        })
    }
    fn children(&self, engine: &Engine, range: &JournalRange) -> Result<Vec<Entry>, HostingError> {
        if range.level() == 0 {
            return Ok(Vec::new());
        }
        let level = range.level() - 1;
        let span = self.table.range(range.origin(), level, 0).map_err(failure)?.last();
        let first = (range.first() - 1) / span;
        let last = (range.last() - 1) / span;
        if last.saturating_sub(first) >= 64 {
            return Err(refused("repair fanout bound exceeded"));
        }
        let mut entries = Vec::new();
        for bucket in first..=last {
            let child = self.table.range(range.origin(), level, bucket).map_err(failure)?;
            let entry = self.summary(engine, &child)?;
            if entry.count != 0 {
                entries.push(entry);
            }
        }
        Ok(entries)
    }
    fn leaf_rows(
        &self,
        engine: &Engine,
        range: &JournalRange,
    ) -> Result<Vec<RowSummary>, HostingError> {
        self.publications(engine, range.origin(), range.first(), range.last())?
            .into_iter()
            .map(|publication| {
                Ok(RowSummary::new(
                    JournalRowId::new(&publication.origin, publication.origin_sequence)
                        .map_err(failure)?,
                    publication.fingerprint().map_err(failure)?,
                ))
            })
            .collect()
    }
    fn range(&self, entry: &Entry) -> Result<JournalRange, HostingError> {
        let level = u8::try_from(entry.level).map_err(failure)?;
        let first = self.table.range(&entry.origin, level, 0).map_err(failure)?;
        if entry.first == 0 {
            return Err(refused("zero repair sequence"));
        }
        let range = self
            .table
            .range(&entry.origin, level, (entry.first - 1) / first.last())
            .map_err(failure)?;
        if range.first() != entry.first {
            return Err(refused("unaligned repair range"));
        }
        Ok(range)
    }
}

/// Effects of one verified repair message. Publications use normal causal apply/RF2.
#[derive(Default)]
pub struct RepairReceive {
    /// Scoped protocol responses to send on the same authenticated connection.
    pub frames: Vec<Vec<u8>>,
    /// Local publications the peer lacks; ship through `CausalDriver::publication_frame`.
    pub requested: Vec<(String, u64)>,
    /// Exact source hashes expected from this authenticated peer during repair.
    pub expected: Vec<(String, u64, [u8; 32])>,
    /// Missing retained history requires the native snapshot bootstrap path.
    pub needs_snapshot: bool,
}

/// Connection-local reconciliation state. Reconnect begins a fresh comparison.
pub struct RepairDriver {
    local: MemberId,
    peer: MemberId,
    prefix: Vec<u8>,
    next_id: u64,
    pending: Option<(Frame, u64)>,
    work: VecDeque<Entry>,
    triggered: bool,
    last_pass: u64,
    scope: BoundScope,
    generation: u64,
    epoch: u64,
    source_cut: Option<(u64, PublicationIndex)>,
    comparison: Option<ComparisonCut>,
    verified: Option<VerifiedRepairCut>,
    repair_count: usize,
}
struct ComparisonCut {
    id: u64,
    frontier: BTreeMap<String, u64>,
    roots: Vec<Entry>,
    verify_again: bool,
}

/// A correlated equality proof for one immutable source publication cut.
/// Physical durability must separately cover `local_sequence()` before readiness.
#[derive(Clone, Debug)]
pub struct VerifiedRepairCut {
    scope: DatabaseRouteScope,
    epoch: u64,
    generation: u64,
    peer: String,
    frontier: BTreeMap<String, u64>,
    cut_digest: [u8; 32],
    root_digest: [u8; 32],
    local_sequence: u64,
}
impl VerifiedRepairCut {
    /// Fully authenticated database scope.
    #[must_use]
    pub const fn scope(&self) -> DatabaseRouteScope {
        self.scope
    }
    /// Fenced replica-set epoch.
    #[must_use]
    pub const fn replica_epoch(&self) -> u64 {
        self.epoch
    }
    /// Fenced placement generation.
    #[must_use]
    pub const fn placement_generation(&self) -> u64 {
        self.generation
    }
    /// Selected authenticated peer whose frozen source cut was compared.
    #[must_use]
    pub fn peer(&self) -> &str {
        &self.peer
    }
    /// Exact per-origin source upper bounds; this does not infer causal continuity.
    #[must_use]
    pub fn frontier(&self) -> &BTreeMap<String, u64> {
        &self.frontier
    }
    /// Domain-separated scope, epoch and frontier digest.
    #[must_use]
    pub const fn cut_digest(&self) -> [u8; 32] {
        self.cut_digest
    }
    /// Domain-separated fingerprints for the exact source cut.
    #[must_use]
    pub const fn root_digest(&self) -> [u8; 32] {
        self.root_digest
    }
    /// Local durable journal sequence observed when equal; RF2 must cover it.
    #[must_use]
    pub const fn local_sequence(&self) -> u64 {
        self.local_sequence
    }
}
impl RepairDriver {
    /// Binds messages to authenticated member identities and exact database placement.
    /// # Errors
    /// Rejects invalid identities, database scope, or zero epoch fencing tokens.
    pub fn new(
        local: MemberId,
        peer: MemberId,
        scope: BoundScope,
        generation: u64,
        epoch: u64,
    ) -> Result<Self, HostingError> {
        scope.validate().map_err(failure)?;
        if local == peer || generation == 0 || epoch == 0 {
            return Err(refused("invalid repair admission"));
        }
        let mut prefix = MAGIC.to_vec();
        for field in [&scope.tenant, &scope.project, &scope.database] {
            prefix.extend_from_slice(&u32::try_from(field.len()).map_err(failure)?.to_be_bytes());
            prefix.extend_from_slice(field.as_bytes());
        }
        prefix.extend_from_slice(&generation.to_be_bytes());
        prefix.extend_from_slice(&epoch.to_be_bytes());
        Ok(Self {
            local,
            peer,
            prefix,
            next_id: 1,
            pending: None,
            work: VecDeque::new(),
            triggered: true,
            last_pass: 0,
            scope,
            generation,
            epoch,
            source_cut: None,
            comparison: None,
            verified: None,
            repair_count: 0,
        })
    }
    /// Schedules comparison after a dependency stall or reconnection.
    pub fn trigger(&mut self) {
        self.triggered = true;
        self.verified = None;
    }
    /// Last completed exact-cut equality on this authenticated connection.
    #[must_use]
    pub fn verified_cut(&self) -> Option<&VerifiedRepairCut> {
        self.verified.as_ref()
    }

    fn frame(&self, id: u64, kind: u32, entries: Vec<Entry>) -> Frame {
        Frame {
            source: self.local.as_str().to_owned(),
            target: self.peer.as_str().to_owned(),
            id,
            kind,
            entries,
            cut: self.comparison.as_ref().map_or(0, |cut| cut.id),
        }
    }
    fn encode(&self, frame: &Frame) -> Result<Vec<u8>, HostingError> {
        if self.prefix.len().saturating_add(frame.encoded_len()) > MAX_BYTES {
            return Err(refused("repair wire byte bound exceeded"));
        }
        let mut bytes = self.prefix.clone();
        frame.encode(&mut bytes).map_err(failure)?;
        Ok(bytes)
    }
    /// Produces a bounded request or retries its exact bytes after timeout.
    /// # Errors
    /// Returns wire-bound or identifier exhaustion failures.
    pub fn outbound_frame(
        &mut self,
        engine: &Engine,
        index: &PublicationIndex,
        now_ms: u64,
    ) -> Result<Option<Vec<u8>>, HostingError> {
        if let Some((frame, sent)) = &mut self.pending {
            if now_ms.saturating_sub(*sent) < RETRY_MS {
                return Ok(None);
            }
            *sent = now_ms;
            let frame = frame.clone();
            return self.encode(&frame).map(Some);
        }
        let frame = if let Some(entry) = self.work.pop_front() {
            let view = self.comparison.as_ref().map(|cut| index.at_frontier(&cut.frontier));
            let index = view.as_ref().unwrap_or(index);
            let range = index.range(&entry)?;
            self.frame(self.next_id, 3, vec![index.summary(engine, &range)?])
        } else if self.comparison.as_ref().is_some_and(|cut| cut.verify_again) {
            self.frame(self.next_id, 1, Vec::new())
        } else if self.triggered || now_ms.saturating_sub(self.last_pass) >= PERIOD_MS {
            self.triggered = false;
            self.comparison = None;
            self.verified = None;
            self.repair_count = 0;
            self.last_pass = now_ms;
            self.frame(self.next_id, 1, Vec::new())
        } else {
            return Ok(None);
        };
        self.next_id =
            self.next_id.checked_add(1).ok_or_else(|| refused("repair request ids exhausted"))?;
        let bytes = self.encode(&frame)?;
        self.pending = Some((frame, now_ms));
        Ok(Some(bytes))
    }
    /// Validates a response against its outstanding range before scheduling repair.
    /// # Errors
    /// Rejects scope/epoch/identity mismatch, malformed bounds, unsolicited responses,
    /// and conflicting hashes for an immutable source publication.
    pub fn receive_frame(
        &mut self,
        engine: &Engine,
        index: &PublicationIndex,
        bytes: &[u8],
    ) -> Result<RepairReceive, HostingError> {
        if bytes.len() > MAX_BYTES {
            return Err(refused("repair wire byte bound exceeded"));
        }
        let body = bytes
            .strip_prefix(self.prefix.as_slice())
            .ok_or_else(|| refused("repair scope or epoch mismatch"))?;
        let frame = Frame::decode(body).map_err(failure)?;
        if frame.source != self.peer.as_str()
            || frame.target != self.local.as_str()
            || frame.id == 0
            || frame.entries.len() > MAX_ROWS
        {
            return Err(refused("repair identity or entry bound mismatch"));
        }
        if frame.entries.iter().any(|entry| {
            entry.origin.is_empty() || entry.origin.len() > 1024 || entry.hash.len() > 32
        }) {
            return Err(refused("invalid repair entry"));
        }
        let mut result = RepairReceive::default();
        match frame.kind {
            1 => {
                if !frame.entries.is_empty() || index.floors.len() > MAX_ORIGINS {
                    return Err(refused("invalid repair catalog"));
                }
                if frame.cut == 0 && self.source_cut.as_ref().is_none_or(|(id, _)| *id != frame.id)
                {
                    let frontier = index.frontier.clone();
                    self.source_cut = Some((frame.id, index.at_frontier(&frontier)));
                }
                let (cut_id, frozen) =
                    self.source_cut.as_ref().ok_or_else(|| refused("unknown repair source cut"))?;
                if frame.cut != 0 && frame.cut != *cut_id {
                    return Err(refused("expired repair source cut"));
                }
                let frontier = frozen.frontier.clone();
                let mut entries = Vec::new();
                for (origin, floor) in &frozen.floors {
                    let root = frozen.table.root(origin).map_err(failure)?;
                    let mut entry = frozen.summary(engine, &root)?;
                    entry.floor = *floor;
                    entry.cutoff = frontier.get(origin).copied().unwrap_or(0);
                    entries.push(entry);
                }
                let mut response = self.frame(frame.id, 2, entries);
                response.cut = *cut_id;
                result.frames.push(self.encode(&response)?);
            }
            3 => {
                let (cut_id, index) =
                    self.source_cut.as_ref().ok_or_else(|| refused("missing source repair cut"))?;
                if *cut_id != frame.cut {
                    return Err(refused("wrong source repair cut"));
                }
                if frame.entries.len() != 1 {
                    return Err(refused("invalid repair range request"));
                }
                let entry = &frame.entries[0];
                if entry.hash.len() != 16 {
                    return Err(refused("invalid repair fingerprint"));
                }
                let range = index.range(entry)?;
                let local = index.summary(engine, &range)?;
                let (kind, entries) = if local.count == entry.count && local.hash == entry.hash {
                    (4, vec![local])
                } else if range.level() == 0 {
                    let rows = index
                        .leaf_rows(engine, &range)?
                        .into_iter()
                        .map(|row| Entry {
                            origin: row.id.origin().to_owned(),
                            level: 0,
                            first: row.id.sequence(),
                            count: 1,
                            hash: row.row_hash.to_vec(),
                            floor: 0,
                            cutoff: 0,
                        })
                        .collect();
                    (6, rows)
                } else {
                    (5, index.children(engine, &range)?)
                };
                let mut response = self.frame(frame.id, kind, entries);
                response.cut = frame.cut;
                result.frames.push(self.encode(&response)?);
            }
            7 => {
                if frame.entries.len() > MAX_REPAIRS {
                    return Err(refused("repair publication request budget exceeded"));
                }
                let mut unique = BTreeSet::new();
                for entry in &frame.entries {
                    let id = JournalRowId::new(&entry.origin, entry.first).map_err(failure)?;
                    if !unique.insert(id.clone()) || entry.hash.len() != 32 {
                        return Err(refused("invalid repair publication request"));
                    }
                    let groups =
                        index.publications(engine, id.origin(), id.sequence(), id.sequence())?;
                    match groups.first() {
                        Some(group)
                            if group.fingerprint().map_err(failure)?.as_slice() == entry.hash =>
                        {
                            result.requested.push((entry.origin.clone(), entry.first))
                        }
                        None => result.needs_snapshot = true,
                        _ => return Err(refused("requested publication hash mismatch")),
                    }
                }
            }
            2 | 4 | 5 | 6 => {
                // Old duplicates and reordered responses cannot consume a newer request.
                let Some((pending, _)) = &self.pending else {
                    return Ok(result);
                };
                if frame.id < pending.id {
                    return Ok(result);
                }
                if frame.id != pending.id {
                    return Err(refused("uncorrelated repair response"));
                }
                let pending = pending.clone();
                match frame.kind {
                    2 => self.receive_catalog(engine, index, &pending, &frame, &mut result)?,
                    4 | 5 | 6 => {
                        let cut = self
                            .comparison
                            .as_ref()
                            .ok_or_else(|| refused("missing comparison cut"))?;
                        if frame.cut != cut.id {
                            return Err(refused("wrong comparison cut"));
                        }
                        let view = index.at_frontier(&cut.frontier);
                        let index = &view;
                        if pending.kind != 3 || pending.entries.len() != 1 {
                            return Err(refused("unexpected repair range response"));
                        }
                        let range = index.range(&pending.entries[0])?;
                        match frame.kind {
                            4 => {
                                if frame.entries != pending.entries {
                                    return Err(refused("unproved equal range"));
                                }
                            }
                            5 => {
                                if range.level() == 0 || frame.entries.len() > 64 {
                                    return Err(refused("invalid repair refinement"));
                                }
                                let mut children = BTreeMap::new();
                                for entry in &frame.entries {
                                    let child = index.range(entry)?;
                                    if child.origin() != range.origin()
                                        || child.level() + 1 != range.level()
                                        || child.first() < range.first()
                                        || child.last() > range.last()
                                        || entry.hash.len() != 16
                                        || children.insert(child.first(), entry.clone()).is_some()
                                    {
                                        return Err(refused(
                                            "repair child outside requested range",
                                        ));
                                    }
                                }
                                for child in index.children(engine, &range)? {
                                    children.entry(child.first).or_insert(Entry {
                                        count: 0,
                                        hash: vec![0; 16],
                                        ..child
                                    });
                                }
                                for remote in children.values() {
                                    let local = index.summary(engine, &index.range(remote)?)?;
                                    if local.count != remote.count || local.hash != remote.hash {
                                        self.work.push_back(local);
                                    }
                                }
                            }
                            6 => self.compare_leaf(engine, index, &range, &frame, &mut result)?,
                            _ => unreachable!(),
                        }
                    }
                    _ => unreachable!(),
                }
                self.pending = None;
            }
            _ => return Err(refused("unsupported repair message")),
        }
        self.repair_count = self.repair_count.saturating_add(result.expected.len());
        if self.repair_count > MAX_REPAIRS {
            result.frames.clear();
            result.expected.clear();
            result.requested.clear();
            result.needs_snapshot = true;
        }
        if self.work.len() > MAX_WORK {
            self.work.clear();
            result.needs_snapshot = true;
        }
        if result.needs_snapshot {
            self.work.clear();
            self.comparison = None;
            self.verified = None;
        }
        Ok(result)
    }
    fn receive_catalog(
        &mut self,
        engine: &Engine,
        index: &PublicationIndex,
        pending: &Frame,
        frame: &Frame,
        result: &mut RepairReceive,
    ) -> Result<(), HostingError> {
        if pending.kind != 1
            || frame.entries.len() > MAX_ORIGINS
            || frame.cut == 0
            || (pending.cut != 0 && pending.cut != frame.cut)
        {
            return Err(refused("unexpected repair catalog"));
        }
        if let Some(previous) = &self.comparison {
            if previous.id != frame.cut || previous.roots != frame.entries {
                return Err(refused("source changed immutable repair cut"));
            }
        }
        let mut frontier = BTreeMap::new();
        for entry in &frame.entries {
            let root = index.table.root(&entry.origin).map_err(failure)?;
            if entry.first != 1
                || entry.level != u32::from(root.level())
                || entry.hash.len() != 16
                || entry.floor == 0
                || frontier.insert(entry.origin.clone(), entry.cutoff).is_some()
            {
                return Err(refused("invalid repair catalog prefix"));
            }
            if entry.floor != 1 || index.floors.get(&entry.origin).is_some_and(|floor| *floor != 1)
            {
                result.needs_snapshot = true;
            }
        }
        let local = index.at_frontier(&frontier);
        for entry in &frame.entries {
            let range = local.range(entry)?;
            let summary = local.summary(engine, &range)?;
            if summary.count != entry.count || summary.hash != entry.hash {
                self.work.push_back(summary);
            }
        }
        let equal = self.work.is_empty() && !result.needs_snapshot;
        self.comparison = Some(ComparisonCut {
            id: frame.cut,
            frontier: frontier.clone(),
            roots: frame.entries.clone(),
            verify_again: !equal,
        });
        if equal {
            let scope = DatabaseRouteScope::try_new(
                &self.scope.tenant,
                &self.scope.project,
                &self.scope.database,
            )
            .map_err(failure)?;
            let mut cut = blake3::Hasher::new();
            cut.update(b"terrabase-repair-exact-cut-v1");
            cut.update(&self.prefix);
            for (origin, sequence) in &frontier {
                cut.update(&u64::try_from(origin.len()).map_err(failure)?.to_be_bytes());
                cut.update(origin.as_bytes());
                cut.update(&sequence.to_be_bytes());
            }
            let cut_digest = *cut.finalize().as_bytes();
            let mut root = blake3::Hasher::new();
            root.update(b"terrabase-repair-exact-root-v1");
            root.update(&cut_digest);
            for entry in &frame.entries {
                root.update(&entry.count.to_be_bytes());
                root.update(&entry.hash);
            }
            self.verified = Some(VerifiedRepairCut {
                scope,
                epoch: self.epoch,
                generation: self.generation,
                peer: self.peer.as_str().to_owned(),
                frontier,
                cut_digest,
                root_digest: *root.finalize().as_bytes(),
                local_sequence: index.cursor,
            });
        } else {
            self.verified = None;
        }
        Ok(())
    }

    fn compare_leaf(
        &self,
        engine: &Engine,
        index: &PublicationIndex,
        range: &JournalRange,
        frame: &Frame,
        result: &mut RepairReceive,
    ) -> Result<(), HostingError> {
        if range.level() != 0 {
            return Err(refused("exact ids supplied for internal range"));
        }
        let mut remote = BTreeMap::new();
        for entry in &frame.entries {
            if entry.origin != range.origin()
                || entry.first < range.first()
                || entry.first > range.last()
                || entry.hash.len() != 32
                || entry.level != 0
                || entry.count != 1
                || remote.insert(entry.first, entry.clone()).is_some()
            {
                return Err(refused("invalid exact repair identities"));
            }
        }
        let mut missing = Vec::new();
        for row in index.leaf_rows(engine, range)? {
            match remote.remove(&row.id.sequence()) {
                Some(entry) if entry.hash != row.row_hash => {
                    return Err(refused("immutable publication hash mismatch"));
                }
                None => result.requested.push((row.id.origin().to_owned(), row.id.sequence())),
                _ => {}
            }
        }
        missing.extend(remote.into_values());
        if missing.len().saturating_add(result.requested.len()) > MAX_REPAIRS {
            result.requested.clear();
            result.needs_snapshot = true;
        } else if !missing.is_empty() {
            for entry in &missing {
                result.expected.push((
                    entry.origin.clone(),
                    entry.first,
                    entry.hash.as_slice().try_into().map_err(failure)?,
                ));
            }
            result.frames.push(self.encode(&self.frame(frame.id, 7, missing))?);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::engine::{CommitOutcome, EngineConfig};
    use terrabase::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId,
    };

    fn bound() -> BoundScope {
        BoundScope {
            principal: "member".into(),
            tenant: "01ARZ3NDEKTSV4RRFFQ69G5FAV".into(),
            project: "01ARZ3NDEKTSV4RRFFQ69G5FAV".into(),
            database: "01ARZ3NDEKTSV4RRFFQ69G5FAV".into(),
        }
    }
    fn driver(local: &str, peer: &str, epoch: u64) -> RepairDriver {
        RepairDriver::new(
            MemberId::try_new(local).unwrap(),
            MemberId::try_new(peer).unwrap(),
            bound(),
            1,
            epoch,
        )
        .unwrap()
    }
    fn fixture() -> (std::path::PathBuf, Engine, Engine) {
        let path = std::env::temp_dir().join(format!(
            "production-repair-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let source = Engine::open(EngineConfig::durable(path.join("source")).unwrap()).unwrap();
        let target = Engine::open(EngineConfig::durable(path.join("target")).unwrap()).unwrap();
        let scope = source.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
        let mut tx = source
            .begin(
                &scope,
                source.read_snapshot(&scope).unwrap(),
                OperationId::try_new("repair-group").unwrap(),
            )
            .unwrap();
        for name in ["a", "b"] {
            tx.put(
                Document::new(
                    DocumentId::try_new(name).unwrap(),
                    CollectionName::try_new("items").unwrap(),
                    DocumentValue::Object(BTreeMap::new()),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        }
        assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        (path, source, target)
    }
    #[test]
    fn retained_atomic_groups_repair_with_duplicate_and_reordered_responses() {
        let (path, source, target) = fixture();
        let mut left = PublicationIndex::default();
        let mut right = PublicationIndex::default();
        assert!(left.refresh(&source, &bound()).unwrap());
        assert!(right.refresh(&target, &bound()).unwrap());
        let mut sender = driver("a", "b", 1);
        let mut receiver = driver("b", "a", 1);
        let mut old = None;
        let mut repaired = 0;
        for tick in 0..64 {
            let Some(request) = receiver.outbound_frame(&target, &right, tick).unwrap() else {
                break;
            };
            let reply = sender.receive_frame(&source, &left, &request).unwrap();
            assert_eq!(reply.frames.len(), 1);
            if let Some(old) = &old {
                assert!(receiver.receive_frame(&target, &right, old).unwrap().frames.is_empty());
            }
            let reply = &reply.frames[0];
            let effects = receiver.receive_frame(&target, &right, reply).unwrap();
            assert!(!effects.needs_snapshot);
            assert!(receiver.receive_frame(&target, &right, reply).unwrap().frames.is_empty());
            old = Some(reply.clone());
            for frame in effects.frames {
                let wanted = sender.receive_frame(&source, &left, &frame).unwrap();
                for (origin, sequence) in wanted.requested {
                    let group = left.publication(&source, &bound(), &origin, sequence).unwrap();
                    assert_eq!(group.records.len(), 2);
                    assert!(effects.expected.contains(&(
                        origin,
                        sequence,
                        group.fingerprint().unwrap()
                    )));
                    let scope =
                        target.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
                    target.apply_replicated_publication(&scope, &group).unwrap();
                    // Duplicate transfers retain exactly one canonical source identity.
                    target.apply_replicated_publication(&scope, &group).unwrap();
                    repaired += 1;
                }
                assert!(right.refresh(&target, &bound()).unwrap());
            }
        }
        assert_eq!(repaired, 1);
        assert!(receiver.verified_cut().is_some());
        drop(source);
        drop(target);
        std::fs::remove_dir_all(path).unwrap();
    }
    #[test]
    fn group_hash_is_order_invariant_and_conflicting_identity_is_refused() {
        let (path, source, target) = fixture();
        let scope = source.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
        let mut group = source.replication_publications(&scope, 0, 1).unwrap().remove(0).1;
        let scope_target =
            target.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
        target.apply_replicated_publication(&scope_target, &group).unwrap();
        let hash = group.fingerprint().unwrap();
        group.records.reverse();
        assert_eq!(group.fingerprint().unwrap(), hash);
        target.apply_replicated_publication(&scope_target, &group).unwrap();
        group.records[0].body = DocumentValue::Null;
        assert!(target.apply_replicated_publication(&scope_target, &group).is_err());
        drop(source);
        drop(target);
        std::fs::remove_dir_all(path).unwrap();
    }
    #[test]
    fn placement_epoch_and_wire_bounds_are_enforced() {
        let (path, source, target) = fixture();
        let mut index = PublicationIndex::default();
        index.refresh(&source, &bound()).unwrap();
        let mut sender = driver("a", "b", 1);
        let request = sender.outbound_frame(&source, &index, 0).unwrap().unwrap();
        assert!(driver("b", "a", 2).receive_frame(&target, &index, &request).is_err());
        assert!(driver("c", "a", 1).receive_frame(&target, &index, &request).is_err());
        assert!(
            driver("b", "a", 1).receive_frame(&target, &index, &vec![0; MAX_BYTES + 1]).is_err()
        );
    }
}
