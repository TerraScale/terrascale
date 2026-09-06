//! Bounded range reconciliation over authenticated, placement-fenced member links.
//! Proofs describe retained source publications; they never authorize durability.
use std::collections::{BTreeMap, BTreeSet, VecDeque};

use prost::Message;
use terrabase::engine::{Engine, ReplicatedPublication};
use terrabase::types::TenantId;
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
}

fn refused(message: &str) -> HostingError {
    HostingError::Refused(message.to_owned())
}
fn failure(error: impl std::fmt::Debug) -> HostingError {
    HostingError::EngineFailure(format!("publication repair: {error:?}"))
}

/// Maintained set of complete retained source publications, including superseded writes.
#[derive(Default)]
pub struct PublicationIndex {
    table: RangeSummaryTable,
    floors: BTreeMap<String, u64>,
    positions: BTreeMap<(String, u64), u64>,
    cursor: u64,
}
impl PublicationIndex {
    /// Inserts a complete immutable source group using the engine's canonical hash.
    /// # Errors
    /// Rejects malformed groups or reuse of a source identity with different content.
    pub fn insert(&mut self, publication: &ReplicatedPublication) -> Result<(), HostingError> {
        let id =
            JournalRowId::new(&publication.origin, publication.origin_sequence).map_err(failure)?;
        let hash = publication.fingerprint().map_err(failure)?;
        if self.table.get(&id).is_some_and(|old| *old != hash) {
            return Err(refused("retained publication identity has conflicting hashes"));
        }
        self.table.insert(&RowSummary::new(id, hash));
        self.floors.entry(publication.origin.clone()).or_insert(1);
        if self.floors.len() > MAX_ORIGINS {
            return Err(refused("repair origin bound exceeded"));
        }
        Ok(())
    }
    /// Adds up to 1024 newly durable publication groups to the maintained index.
    /// Returns true when this page reached the current journal end. Call repeatedly
    /// before starting a comparison; a partial initial scan is not a complete set.
    /// # Errors
    /// Returns storage, scope, or immutable-identity failures.
    pub fn refresh(&mut self, engine: &Engine, bound: &BoundScope) -> Result<bool, HostingError> {
        bound.validate().map_err(failure)?;
        let scope = engine.scope_for(&TenantId::try_new(&bound.tenant).map_err(failure)?);
        let rows = engine.replication_publications(&scope, self.cursor, 1024).map_err(failure)?;
        let complete = rows.len() < 1024;
        for (position, publication) in rows {
            if position <= self.cursor {
                return Err(refused("nonadvancing retained publication cursor"));
            }
            self.insert(&publication)?;
            self.positions
                .insert((publication.origin.clone(), publication.origin_sequence), position);
            self.cursor = position;
        }
        Ok(complete)
    }

    /// Reads one original retained group selected by an authenticated repair request.
    /// # Errors
    /// Rejects absent/pruned publications and source hash changes since comparison.
    pub fn publication(
        &self,
        engine: &Engine,
        bound: &BoundScope,
        origin: &str,
        sequence: u64,
    ) -> Result<ReplicatedPublication, HostingError> {
        bound.validate().map_err(failure)?;
        let position = self
            .positions
            .get(&(origin.to_owned(), sequence))
            .copied()
            .ok_or_else(|| refused("repair publication requires snapshot"))?;
        let scope = engine.scope_for(&TenantId::try_new(&bound.tenant).map_err(failure)?);
        let rows = engine
            .replication_publications(&scope, position.saturating_sub(1), 1)
            .map_err(failure)?;
        let (actual, publication) = rows
            .into_iter()
            .next()
            .ok_or_else(|| refused("repair publication requires snapshot"))?;
        let id = JournalRowId::new(origin, sequence).map_err(failure)?;
        if actual != position
            || publication.origin != origin
            || publication.origin_sequence != sequence
            || *self.table.get(&id).ok_or_else(|| refused("missing repair publication hash"))?
                != publication.fingerprint().map_err(failure)?
        {
            return Err(refused("repair publication changed since comparison"));
        }
        Ok(publication)
    }

    /// Records the first available sequence after pruning, including an empty origin.
    pub fn set_retained_floor(&mut self, origin: String, first_available: u64) {
        self.floors.insert(origin, first_available);
    }
    fn summary(&self, range: &JournalRange) -> Entry {
        let fingerprint = self.table.fingerprint(range);
        Entry {
            origin: range.origin().to_owned(),
            level: u32::from(range.level()),
            first: range.first(),
            count: fingerprint.count,
            hash: fingerprint.sum.to_be_bytes().to_vec(),
            floor: 0,
        }
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
        })
    }
    /// Schedules comparison after a dependency stall or reconnection.
    pub fn trigger(&mut self) {
        self.triggered = true;
    }
    fn frame(&self, id: u64, kind: u32, entries: Vec<Entry>) -> Frame {
        Frame {
            source: self.local.as_str().to_owned(),
            target: self.peer.as_str().to_owned(),
            id,
            kind,
            entries,
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
            let range = index.range(&entry)?;
            self.frame(self.next_id, 3, vec![index.summary(&range)])
        } else if self.triggered || now_ms.saturating_sub(self.last_pass) >= PERIOD_MS {
            self.triggered = false;
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
                let mut entries = Vec::new();
                for (origin, floor) in &index.floors {
                    let root = index.table.root(origin).map_err(failure)?;
                    let mut entry = index.summary(&root);
                    entry.floor = *floor;
                    entries.push(entry);
                }
                result.frames.push(self.encode(&self.frame(frame.id, 2, entries))?);
            }
            3 => {
                if frame.entries.len() != 1 {
                    return Err(refused("invalid repair range request"));
                }
                let entry = &frame.entries[0];
                if entry.hash.len() != 16 {
                    return Err(refused("invalid repair fingerprint"));
                }
                let range = index.range(entry)?;
                let local = index.summary(&range);
                let (kind, entries) = if local.count == entry.count && local.hash == entry.hash {
                    (4, vec![local])
                } else if range.level() == 0 {
                    let rows = index
                        .table
                        .leaf_rows(&range)
                        .into_iter()
                        .map(|row| Entry {
                            origin: row.id.origin().to_owned(),
                            level: 0,
                            first: row.id.sequence(),
                            count: 1,
                            hash: row.row_hash.to_vec(),
                            floor: 0,
                        })
                        .collect();
                    (6, rows)
                } else {
                    (
                        5,
                        index
                            .table
                            .children(&range)
                            .iter()
                            .map(|child| index.summary(child))
                            .collect(),
                    )
                };
                result.frames.push(self.encode(&self.frame(frame.id, kind, entries))?);
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
                    match index.table.get(&id) {
                        Some(hash) if hash.as_slice() == entry.hash => {
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
                    2 => {
                        if pending.kind != 1 || frame.entries.len() > MAX_ORIGINS {
                            return Err(refused("unexpected repair catalog"));
                        }
                        let mut origins = BTreeSet::new();
                        for entry in &frame.entries {
                            let root = index.table.root(&entry.origin).map_err(failure)?;
                            if entry.first != 1
                                || entry.level != u32::from(root.level())
                                || entry.hash.len() != 16
                                || entry.floor == 0
                                || !origins.insert(entry.origin.clone())
                            {
                                return Err(refused("invalid repair catalog root"));
                            }
                            let local = index.summary(&root);
                            if local.hash != entry.hash || local.count != entry.count {
                                self.work.push_back(local);
                            }
                            // A missing retained prefix cannot be reconstructed by journal repair.
                            let local_floor = index.floors.get(&entry.origin).copied().unwrap_or(1);
                            if entry.floor != local_floor && (entry.floor > 1 || local_floor > 1) {
                                result.needs_snapshot = true;
                            }
                        }
                        for origin in
                            index.floors.keys().filter(|origin| !origins.contains(*origin))
                        {
                            self.work.push_back(
                                index.summary(&index.table.root(origin).map_err(failure)?),
                            );
                        }
                    }
                    4 | 5 | 6 => {
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
                                for child in index.table.children(&range) {
                                    children.entry(child.first()).or_insert(Entry {
                                        count: 0,
                                        hash: vec![0; 16],
                                        ..index.summary(&child)
                                    });
                                }
                                for remote in children.values() {
                                    let local = index.summary(&index.range(remote)?);
                                    if local.count != remote.count || local.hash != remote.hash {
                                        self.work.push_back(local);
                                    }
                                }
                            }
                            6 => self.compare_leaf(index, &range, &frame, &mut result)?,
                            _ => unreachable!(),
                        }
                    }
                    _ => unreachable!(),
                }
                self.pending = None;
            }
            _ => return Err(refused("unsupported repair message")),
        }
        if self.work.len() > MAX_WORK {
            self.work.clear();
            result.needs_snapshot = true;
        }
        if result.needs_snapshot {
            self.work.clear();
        }
        Ok(result)
    }
    fn compare_leaf(
        &self,
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
        for row in index.table.leaf_rows(range) {
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
            result.frames.push(self.encode(&self.frame(frame.id, 7, missing))?);
        }
        Ok(())
    }
}
