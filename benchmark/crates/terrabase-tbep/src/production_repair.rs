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
const MAX_ORIGINS: usize = 20;
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
    #[prost(bytes = "vec", tag = "7")]
    baseline: Vec<u8>,
}

fn refused(message: &str) -> HostingError {
    HostingError::Refused(message.to_owned())
}
fn failure(error: impl std::fmt::Debug) -> HostingError {
    HostingError::EngineFailure(format!("publication repair: {error:?}"))
}

// Count repeated entries before prost allocates them; a tiny repeated-message
// flood must not turn a bounded wire frame into an unbounded object graph.
fn validate_encoded_bound(mut bytes: &[u8]) -> Result<(), HostingError> {
    fn varint(bytes: &mut &[u8]) -> Result<u64, HostingError> {
        let mut value = 0_u64;
        for shift in (0..70).step_by(7) {
            let (&byte, rest) =
                bytes.split_first().ok_or_else(|| refused("truncated repair frame"))?;
            *bytes = rest;
            if shift == 63 && byte > 1 {
                return Err(refused("overflowing repair varint"));
            }
            value |= u64::from(byte & 0x7f) << shift;
            if byte & 0x80 == 0 {
                return Ok(value);
            }
        }
        Err(refused("invalid repair varint"))
    }
    let mut entries = 0;
    while !bytes.is_empty() {
        let key = varint(&mut bytes)?;
        let length = match key & 7 {
            0 => {
                varint(&mut bytes)?;
                0
            }
            1 => 8,
            2 => {
                let length = usize::try_from(varint(&mut bytes)?).map_err(failure)?;
                if key >> 3 == 5 {
                    entries += 1;
                    if entries > MAX_ROWS || length > 2048 {
                        return Err(refused("repair entry allocation bound exceeded"));
                    }
                }
                length
            }
            5 => 4,
            _ => return Err(refused("unsupported repair wire type")),
        };
        bytes = bytes.get(length..).ok_or_else(|| refused("truncated repair frame"))?;
    }
    Ok(())
}

/// Bounded metadata for a durable, versioned publication-summary cut.
/// Publication hashes and hierarchical fingerprints remain in `SQLite`.
#[derive(Clone, Default)]
pub struct PublicationIndex {
    table: RangeSummaryTable,
    floors: BTreeMap<String, u64>,
    frontier: BTreeMap<String, u64>,
    cut: Option<ReplicationRepairCut>,
    tenant: Option<TenantId>,
    cursor: u64,
    baseline: [u8; 32],
}
impl PublicationIndex {
    fn require_scope(&self, bound: &BoundScope) -> Result<(), HostingError> {
        if self.cut.is_none()
            || self.tenant.as_ref().is_none_or(|tenant| tenant.as_str() != bound.tenant)
        {
            return Err(refused("repair index has no captured database cut"));
        }
        Ok(())
    }
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
        self.baseline = cut.baseline_digest();
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
            terrabase_replication_core::anti_entropy::RangeFingerprint::default()
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
    desired_cut: Option<LocalRepairCut>,
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
    cut: LocalRepairCut,
    peer: String,
}
/// Frozen durable local publication roots. This alone proves no remote equality.
#[derive(Clone, Debug)]
pub struct LocalRepairCut {
    scope: DatabaseRouteScope,
    epoch: u64,
    generation: u64,
    frontier: BTreeMap<String, u64>,
    cut_digest: [u8; 32],
    root_digest: [u8; 32],
    local_sequence: u64,
    roots: Vec<Entry>,
    baseline: [u8; 32],
}
impl LocalRepairCut {
    /// Fully bound database scope.
    #[must_use]
    pub const fn scope(&self) -> DatabaseRouteScope {
        self.scope
    }
    /// Exact replica-set epoch.
    #[must_use]
    pub const fn replica_epoch(&self) -> u64 {
        self.epoch
    }
    /// Exact placement generation.
    #[must_use]
    pub const fn placement_generation(&self) -> u64 {
        self.generation
    }
    /// Source sequence upper bounds; no causal continuity is inferred.
    #[must_use]
    pub fn frontier(&self) -> &BTreeMap<String, u64> {
        &self.frontier
    }
    /// Canonical scoped cut digest, independent of connection request identifiers.
    #[must_use]
    pub const fn cut_digest(&self) -> [u8; 32] {
        self.cut_digest
    }
    /// Canonical root digest for exactly the scoped cut.
    #[must_use]
    pub const fn root_digest(&self) -> [u8; 32] {
        self.root_digest
    }
    /// Durable local sequence whose physical checkpoint must cover this capture.
    #[must_use]
    pub const fn local_sequence(&self) -> u64 {
        self.local_sequence
    }
}
impl VerifiedRepairCut {
    /// Fully authenticated database scope.
    #[must_use]
    pub const fn scope(&self) -> DatabaseRouteScope {
        self.cut.scope
    }
    /// Fenced replica-set epoch.
    #[must_use]
    pub const fn replica_epoch(&self) -> u64 {
        self.cut.epoch
    }
    /// Fenced placement generation.
    #[must_use]
    pub const fn placement_generation(&self) -> u64 {
        self.cut.generation
    }
    /// Selected authenticated peer whose frozen source cut was compared.
    #[must_use]
    pub fn peer(&self) -> &str {
        &self.peer
    }
    /// Exact source upper bounds; does not infer causal continuity.
    #[must_use]
    pub fn frontier(&self) -> &BTreeMap<String, u64> {
        &self.cut.frontier
    }
    /// Domain-separated scope, epoch and frontier digest.
    #[must_use]
    pub const fn cut_digest(&self) -> [u8; 32] {
        self.cut.cut_digest
    }
    /// Domain-separated fingerprints for the exact source cut.
    #[must_use]
    pub const fn root_digest(&self) -> [u8; 32] {
        self.cut.root_digest
    }
    /// Local durable sequence observed when equal; RF2 must cover it.
    #[must_use]
    pub const fn local_sequence(&self) -> u64 {
        self.cut.local_sequence
    }
    /// Reuses the sealed descriptor as the common cut for another selected peer.
    #[must_use]
    pub const fn local_cut(&self) -> &LocalRepairCut {
        &self.cut
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
            desired_cut: None,
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

    /// Pins comparison to the sealed canonical cut shared by all selected peers.
    /// # Errors
    /// Rejects scope or placement changes; a new authenticated driver is required.
    pub fn request_cut(&mut self, cut: &LocalRepairCut) -> Result<(), HostingError> {
        let scope = DatabaseRouteScope::try_new(
            &self.scope.tenant,
            &self.scope.project,
            &self.scope.database,
        )
        .map_err(failure)?;
        if cut.scope != scope || cut.epoch != self.epoch || cut.generation != self.generation {
            return Err(refused("common repair cut scope mismatch"));
        }
        if self.desired_cut.as_ref().is_some_and(|old| {
            old.cut_digest == cut.cut_digest && old.root_digest == cut.root_digest
        }) {
            return Ok(());
        }
        self.desired_cut = Some(cut.clone());
        self.pending = None;
        self.comparison = None;
        self.work.clear();
        self.trigger();
        Ok(())
    }
    /// Captures sealed local roots for the actual durable engine cut.
    /// # Errors
    /// Returns unavailable summary or scope/placement failures.
    pub fn local_cut(
        &self,
        engine: &Engine,
        index: &PublicationIndex,
    ) -> Result<LocalRepairCut, HostingError> {
        index.require_scope(&self.scope)?;
        let mut roots = Vec::new();
        for (origin, cutoff) in &index.frontier {
            let mut entry = index.summary(engine, &index.table.root(origin).map_err(failure)?)?;
            entry.floor = index.floors.get(origin).copied().unwrap_or(1);
            if entry.floor != 1 {
                return Err(refused("local cut requires retained prefix bootstrap"));
            }
            entry.cutoff = *cutoff;
            roots.push(entry);
        }
        self.cut_descriptor(index.frontier.clone(), roots, index.cursor, index.baseline)
    }
    fn cut_descriptor(
        &self,
        frontier: BTreeMap<String, u64>,
        roots: Vec<Entry>,
        local_sequence: u64,
        baseline: [u8; 32],
    ) -> Result<LocalRepairCut, HostingError> {
        let scope = DatabaseRouteScope::try_new(
            &self.scope.tenant,
            &self.scope.project,
            &self.scope.database,
        )
        .map_err(failure)?;
        let mut cut = blake3::Hasher::new();
        cut.update(b"terrabase-repair-exact-cut-v1");
        cut.update(&self.prefix);
        cut.update(&baseline);
        for (origin, sequence) in &frontier {
            cut.update(&u64::try_from(origin.len()).map_err(failure)?.to_be_bytes());
            cut.update(origin.as_bytes());
            cut.update(&sequence.to_be_bytes());
        }
        let cut_digest = *cut.finalize().as_bytes();
        let mut root = blake3::Hasher::new();
        root.update(b"terrabase-repair-exact-root-v1");
        root.update(&cut_digest);
        for entry in &roots {
            root.update(&entry.count.to_be_bytes());
            root.update(&entry.hash);
        }
        Ok(LocalRepairCut {
            scope,
            epoch: self.epoch,
            generation: self.generation,
            frontier,
            cut_digest,
            root_digest: *root.finalize().as_bytes(),
            local_sequence,
            roots,
            baseline,
        })
    }

    fn frame(&self, id: u64, kind: u32, entries: Vec<Entry>) -> Frame {
        Frame {
            source: self.local.as_str().to_owned(),
            target: self.peer.as_str().to_owned(),
            id,
            kind,
            entries,
            cut: self.comparison.as_ref().map_or(0, |cut| cut.id),
            baseline: self.desired_cut.as_ref().map_or_else(Vec::new, |cut| cut.baseline.to_vec()),
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
        index.require_scope(&self.scope)?;
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
            self.frame(
                self.next_id,
                1,
                self.desired_cut.as_ref().map_or_else(Vec::new, |cut| cut.roots.clone()),
            )
        } else if self.triggered || now_ms.saturating_sub(self.last_pass) >= PERIOD_MS {
            self.triggered = false;
            self.comparison = None;
            self.verified = None;
            self.repair_count = 0;
            self.last_pass = now_ms;
            self.frame(
                self.next_id,
                1,
                self.desired_cut.as_ref().map_or_else(Vec::new, |cut| cut.roots.clone()),
            )
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
        index.require_scope(&self.scope)?;
        let frame = self.decode_frame(bytes)?;
        let mut result = RepairReceive::default();
        match frame.kind {
            1 => self.receive_catalog_request(engine, index, &frame, &mut result)?,
            3 => self.receive_range_request(engine, index, &frame, &mut result)?,
            7 => self.receive_publication_request(engine, index, &frame, &mut result)?,
            2 | 4..=6 => self.receive_response(engine, index, &frame, &mut result)?,
            _ => return Err(refused("unsupported repair message")),
        }
        if !result.requested.is_empty() && matches!(frame.kind, 6) {
            self.comparison = None;
            self.work.clear();
            self.triggered = true;
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
    fn decode_frame(&self, bytes: &[u8]) -> Result<Frame, HostingError> {
        if bytes.len() > MAX_BYTES {
            return Err(refused("repair wire byte bound exceeded"));
        }
        let body = bytes
            .strip_prefix(self.prefix.as_slice())
            .ok_or_else(|| refused("repair scope or epoch mismatch"))?;
        validate_encoded_bound(body)?;
        let frame = Frame::decode(body).map_err(failure)?;
        if frame.source != self.peer.as_str()
            || frame.target != self.local.as_str()
            || frame.id == 0
            || frame.entries.len() > MAX_ROWS
            || frame.baseline.len() > 32
        {
            return Err(refused("repair identity or entry bound mismatch"));
        }
        if frame.entries.iter().any(|entry| {
            entry.origin.is_empty() || entry.origin.len() > 1024 || entry.hash.len() > 32
        }) {
            return Err(refused("invalid repair entry"));
        }
        Ok(frame)
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
            || frame.baseline.len() != 32
            || frame.cut == 0
            || (pending.cut != 0 && pending.cut != frame.cut)
        {
            return Err(refused("unexpected repair catalog"));
        }
        if frame.baseline != index.baseline {
            result.needs_snapshot = true;
            self.verified = None;
            return Ok(());
        }
        if let Some(previous) = &self.comparison
            && (previous.id != frame.cut || previous.roots != frame.entries)
        {
            return Err(refused("source changed immutable repair cut"));
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
            let cut =
                self.cut_descriptor(frontier, frame.entries.clone(), index.cursor, index.baseline)?;
            if self.desired_cut.as_ref().is_some_and(|desired| {
                desired.cut_digest != cut.cut_digest || desired.root_digest != cut.root_digest
            }) {
                result.needs_snapshot = true;
                self.verified = None;
            } else {
                self.verified =
                    Some(VerifiedRepairCut { cut, peer: self.peer.as_str().to_owned() });
            }
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
        let mut old: Option<Vec<u8>> = None;
        let mut repaired = 0;
        for tick in 0..64 {
            if tick == 1 {
                let scope =
                    source.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
                let mut tx = source
                    .begin(
                        &scope,
                        source.read_snapshot(&scope).unwrap(),
                        OperationId::try_new("late-operation").unwrap(),
                    )
                    .unwrap();
                tx.put(
                    Document::new(
                        DocumentId::try_new("late").unwrap(),
                        CollectionName::try_new("items").unwrap(),
                        DocumentValue::Object(BTreeMap::new()),
                    ),
                    ExpectedVersion::Any,
                )
                .unwrap();
                assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
                left.refresh(&source, &bound()).unwrap();
            }
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
        let proof = receiver.verified_cut().unwrap();
        assert_eq!(proof.frontier().values().copied().collect::<Vec<_>>(), vec![1]);
        assert_eq!(right.frontier.values().copied().collect::<Vec<_>>(), vec![1]);
        assert_eq!(left.frontier.values().copied().collect::<Vec<_>>(), vec![2]);
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
    fn canonical_cut_descriptor_is_shared_across_selected_peer_comparisons() {
        let (path, source, target) = fixture();
        let scope = source.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
        let group = source.replication_publications(&scope, 0, 1).unwrap().remove(0).1;
        let target_scope =
            target.scope_for(&TenantId::try_new("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap());
        target.apply_replicated_publication(&target_scope, &group).unwrap();
        let mut left = PublicationIndex::default();
        let mut right = PublicationIndex::default();
        left.refresh(&source, &bound()).unwrap();
        right.refresh(&target, &bound()).unwrap();
        let canonical = driver("a", "b", 1).local_cut(&source, &left).unwrap();
        let mut requester = driver("c", "d", 1);
        requester.request_cut(&canonical).unwrap();
        let mut source_peer = driver("d", "c", 1);
        let request = requester.outbound_frame(&target, &right, 0).unwrap().unwrap();
        let response =
            source_peer.receive_frame(&source, &left, &request).unwrap().frames.remove(0);
        requester.receive_frame(&target, &right, &response).unwrap();
        let proof = requester.verified_cut().unwrap();
        assert_eq!(proof.cut_digest(), canonical.cut_digest());
        assert_eq!(proof.root_digest(), canonical.root_digest());
        assert_eq!(proof.peer(), "d");
        assert!(driver("x", "y", 2).request_cut(&canonical).is_err());
        drop(source);
        drop(target);
        std::fs::remove_dir_all(path).unwrap();
    }

    #[test]
    fn explicit_empty_cut_stays_empty_and_baseline_mismatch_requires_snapshot() {
        let (path, source, target) = fixture();
        let mut left = PublicationIndex::default();
        let mut right = PublicationIndex::default();
        left.refresh(&source, &bound()).unwrap();
        right.refresh(&target, &bound()).unwrap();
        let empty = driver("b", "a", 1).local_cut(&target, &right).unwrap();
        let mut receiver = driver("b", "a", 1);
        receiver.request_cut(&empty).unwrap();
        let mut sender = driver("a", "b", 1);
        let request = receiver.outbound_frame(&target, &right, 0).unwrap().unwrap();
        let response = sender.receive_frame(&source, &left, &request).unwrap().frames.remove(0);
        receiver.receive_frame(&target, &right, &response).unwrap();
        assert_eq!(receiver.verified_cut().unwrap().root_digest(), empty.root_digest());
        assert!(receiver.verified_cut().unwrap().frontier().is_empty());

        let mut receiver = driver("b", "a", 1);
        let request = receiver.outbound_frame(&target, &right, 0).unwrap().unwrap();
        let mut sender = driver("a", "b", 1);
        let response = sender.receive_frame(&source, &left, &request).unwrap().frames.remove(0);
        let mut frame =
            Frame::decode(response.strip_prefix(sender.prefix.as_slice()).unwrap()).unwrap();
        frame.baseline = vec![1; 32];
        let response = sender.encode(&frame).unwrap();
        assert!(receiver.receive_frame(&target, &right, &response).unwrap().needs_snapshot);
        assert!(receiver.verified_cut().is_none());
        assert!(validate_encoded_bound(&[0x2a, 0].repeat(MAX_ROWS + 1)).is_err());
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
