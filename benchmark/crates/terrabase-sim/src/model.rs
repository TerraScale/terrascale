//! World vocabulary shared by every simulation adapter (ADR 0044 §2–§3).
//!
//! These types mirror the engine's own language — per-origin journal rows,
//! dotted causal contexts summarized as frontier vectors, session tokens,
//! view cuts — without importing any engine crate, so the harness compiles
//! against trait interfaces from day one and real-engine adapters can reuse
//! the vocabulary unchanged once E-5/E-6 land.

use std::collections::BTreeMap;

/// Identifies one Region (one writer) in the simulated world.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct RegionId(pub u32);

impl RegionId {
    /// Wraps a raw region index.
    #[must_use]
    pub fn new(index: u32) -> Self {
        Self(index)
    }

    /// Converts a harness region position, saturating on absurd inputs
    /// instead of truncating (region counts are harness-small).
    #[must_use]
    pub fn from_index(index: usize) -> Self {
        Self(u32::try_from(index).unwrap_or(u32::MAX))
    }

    /// Returns the raw region index.
    #[must_use]
    pub fn index(self) -> u32 {
        self.0
    }
}

/// Hybrid logical clock timestamp carried by every journal row.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Hlc(pub u64);

impl Hlc {
    /// Wraps a raw HLC value.
    #[must_use]
    pub fn new(value: u64) -> Self {
        Self(value)
    }

    /// Returns the raw HLC value.
    #[must_use]
    pub fn value(self) -> u64 {
        self.0
    }
}

/// Per-Region frontier vector: `{origin → highest applied seq}`.
///
/// This is the summary form of a dotted causal context (ADR 0044 §2.2) and
/// the body of a client session token (ADR 0044 §2.4).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Frontier {
    /// Highest applied sequence per origin Region.
    inner: BTreeMap<RegionId, u64>,
}

impl Frontier {
    /// Creates an empty frontier (nothing applied).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Records that `seq` from `origin` is applied; keeps the maximum.
    pub fn advance(&mut self, origin: RegionId, seq: u64) {
        let entry = self.inner.entry(origin).or_insert(0);
        if seq > *entry {
            *entry = seq;
        }
    }

    /// Returns the highest applied sequence for `origin`, or `0` when none.
    #[must_use]
    pub fn get(&self, origin: RegionId) -> u64 {
        self.inner.get(&origin).copied().unwrap_or(0)
    }

    /// Returns `true` when every origin in `other` is applied here at an
    /// equal or higher sequence (the COPS dependency check, ADR 0044 §2.2).
    #[must_use]
    pub fn covers(&self, other: &Self) -> bool {
        other.inner.iter().all(|(origin, seq)| self.get(*origin) >= *seq)
    }

    /// Merges `other` into `self`, keeping per-origin maxima.
    pub fn merge(&mut self, other: &Self) {
        for (origin, seq) in &other.inner {
            self.advance(*origin, *seq);
        }
    }

    /// Iterates over `(origin, seq)` entries in origin order.
    pub fn entries(&self) -> impl Iterator<Item = (RegionId, u64)> + '_ {
        self.inner.iter().map(|(origin, seq)| (*origin, *seq))
    }

    /// Returns the number of origins tracked.
    #[must_use]
    pub fn len(&self) -> usize {
        self.inner.len()
    }

    /// Returns `true` when no origin is tracked.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.inner.is_empty()
    }
}

/// One committed publication-journal row (ADR 0044 §0).
///
/// Carries the per-origin total order (`origin`, `seq`), the causal
/// `context`, the `hlc` tiebreak, and a compact key/value body standing in
/// for canonical document bytes. Real-engine adapters replace the body with
/// canonical bytes plus dot/context metadata without changing the shape.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct JournalRow {
    /// The Region that committed this row.
    pub origin: RegionId,
    /// The per-origin sequence number.
    pub seq: u64,
    /// The causal context the row was committed under.
    pub context: Frontier,
    /// The hybrid logical clock timestamp of the commit.
    pub hlc: Hlc,
    /// The document key this row writes.
    pub key: u64,
    /// The document value; ignored when `tombstone` is set.
    pub value: u64,
    /// Whether this row deletes `key` (remove-wins map semantics).
    pub tombstone: bool,
}

impl JournalRow {
    /// Returns the `(origin, seq)` identity of this row.
    #[must_use]
    pub fn id(&self) -> (RegionId, u64) {
        (self.origin, self.seq)
    }
}

/// Client session token: the causal timestamp from ADR 0044 §2.4.
///
/// Carries the frontier of everything the client has observed plus the HLC
/// of its last write; a member serves a read only when its applied frontier
/// covers the token, else it waits briefly and returns `StaleReplica`.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SessionToken {
    /// Everything the client has observed.
    pub frontier: Frontier,
    /// The HLC of the client's last write.
    pub last_write_hlc: Hlc,
}

impl SessionToken {
    /// Creates a token observing `frontier` with no writes yet.
    #[must_use]
    pub fn observing(frontier: Frontier) -> Self {
        Self { frontier, last_write_hlc: Hlc::new(0) }
    }

    /// Records an observed row: advances the frontier and the write HLC.
    pub fn observe(&mut self, row: &JournalRow) {
        self.frontier.advance(row.origin, row.seq);
        if row.hlc > self.last_write_hlc {
            self.last_write_hlc = row.hlc;
        }
    }
}

/// The journal cut a view revision is computed from (ADR 0044 §3.4).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct ViewCut {
    /// The applied frontier the revision reflects.
    pub frontier: Frontier,
    /// The highest journal sequence folded into the revision.
    pub seq_hi: u64,
}

impl ViewCut {
    /// Creates a cut at `frontier` / `seq_hi`.
    #[must_use]
    pub fn new(frontier: Frontier, seq_hi: u64) -> Self {
        Self { frontier, seq_hi }
    }
}
