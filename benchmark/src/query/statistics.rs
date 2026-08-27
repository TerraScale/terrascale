//! Typed result statistics: the result-metadata contract of one executed
//! query.
//!
//! V1 answers a query with a result array plus a statistics block — the
//! index it used, whether that index was stale, a state etag, the total,
//! skipped, and scanned counts, and a server-side duration. V2 carries the
//! same inventory in typed shape: the index identity and the per-namespace
//! staleness live on [`QueryResult`](super::QueryResult) beside the rows
//! (the includes-contract precedent for sibling-of-rows data), and this
//! module owns the counts, the state token, and the diagnostic duration.
//!
//! The full V1-to-V2 field mapping, with file and line anchors into the
//! pinned V1 tree, is the decision record
//! `docs/query/gapq11-result-statistics.md`.

use std::time::Duration;

use super::result::StalenessReport;
use super::spec::QueryPaging;
use crate::types::StorageSequence;

/// The FNV-1a 64-bit offset basis.
const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;

/// The FNV-1a 64-bit prime.
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// Mixes one byte into a running FNV-1a hash.
const fn mix_byte(hash: u64, byte: u8) -> u64 {
    (hash ^ byte as u64).wrapping_mul(FNV_PRIME)
}

/// Mixes one `u64` field into a running FNV-1a hash, in little-endian byte
/// order, so two adjacent fields can never be re-read as one.
fn mix_u64(hash: u64, value: u64) -> u64 {
    let mut mixed = hash;
    for byte in value.to_le_bytes() {
        mixed = mix_byte(mixed, byte);
    }
    mixed
}

/// Mixes one length-prefixed byte field into a running FNV-1a hash.
fn mix_bytes(hash: u64, bytes: &[u8]) -> u64 {
    let mut mixed = mix_u64(hash, u64::try_from(bytes.len()).unwrap_or(u64::MAX));
    for byte in bytes {
        mixed = mix_byte(mixed, *byte);
    }
    mixed
}

/// The state token of one executed query.
///
/// The token is a non-cryptographic 64-bit FNV-1a hash — the same class of
/// hash V1 computes for its `ResultEtag` with `XXHash64` — over the
/// observable state the answer was computed from: the snapshot sequence,
/// and, per required namespace in plan order, the epoch and generation the
/// snapshot observed and the one the staleness gate accepted.
///
/// It answers exactly one question: whether a re-execution against the same
/// engine would read the same state. It is not a content digest — two
/// different queries over the same state share a token, exactly as V1's
/// index etags do — not an identity across engines, not a consistency
/// token, and not ordered: two tokens never compare as "newer".
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ResultEtag(u64);

impl ResultEtag {
    /// Computes the token of one execution from its state inputs.
    pub(crate) fn of(sequence: StorageSequence, staleness: &StalenessReport) -> Self {
        // Each section is opened by a distinct domain tag so no two layouts
        // of the same bytes can collide.
        let mut hash = mix_u64(FNV_OFFSET_BASIS, 0x7462_7632_2d65_7461);
        hash = mix_u64(hash, sequence.get());
        for observation in staleness.observations() {
            hash = mix_u64(hash, 0x6e61_6d65_7370_6163);
            hash = mix_bytes(hash, observation.namespace().catalog_key().as_bytes());
            hash = mix_u64(hash, 0x6f62_7365_7276_6564);
            match observation.observed() {
                Some(generation) => {
                    hash = mix_byte(hash, 1);
                    hash = mix_u64(hash, generation.epoch());
                    hash = mix_u64(hash, generation.generation());
                }
                None => hash = mix_byte(hash, 0),
            }
            hash = mix_u64(hash, 0x7573_6564_2d67_656e);
            match observation.used() {
                Some(generation) => {
                    hash = mix_byte(hash, 1);
                    hash = mix_u64(hash, generation.epoch());
                    hash = mix_u64(hash, generation.generation());
                }
                None => hash = mix_byte(hash, 0),
            }
        }
        Self(hash)
    }

    /// Returns the 64-bit token value, for wire and evidence surfaces.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// The diagnostic duration of one query execution window.
///
/// What the value **is**: the wall-clock time the executing thread observed
/// between entering the execution path and constructing the result — the
/// binding checks, the staleness gate, the snapshot scan, the ordering, the
/// projection, and the include materialization of this one call, measured
/// on a monotonic clock and recorded for every successful execution. The
/// optimizer pass a dynamic plan additionally runs is a separate catalog
/// operation outside this window.
///
/// What the value **is not**: it is not a benchmark. It is not comparable
/// across engines, builds, machines, or workloads; it is not a latency
/// guarantee, budget, or service level; and it proves nothing about speed.
/// Under the charter's hard-data rule (PD-2026-08-26-03) no performance
/// claim may rest on this number — only TerraBase.Benchmark receipts from
/// the exclusive benchmark lane carry performance evidence.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DiagnosticDuration(Duration);

impl DiagnosticDuration {
    /// Wraps one observed execution duration. Crate-only: the number exists
    /// only as the engine's own observation of its own execution.
    pub(crate) const fn new(duration: Duration) -> Self {
        Self(duration)
    }

    /// Returns the observed elapsed time of this one execution window.
    ///
    /// The number is diagnostic-only and carries no claim: see the type
    /// documentation for exactly what it is and is not.
    #[must_use]
    pub const fn elapsed(self) -> Duration {
        self.0
    }
}

/// The typed result-metadata of one executed query.
///
/// The counts are the FILTER accounting of V1's statistics in typed shape:
/// [`scanned`](Self::scanned) counts the candidate documents of the source
/// collection the predicate was evaluated against, and
/// [`skipped`](Self::skipped) counts the candidates that did not match —
/// V1's `ScannedResults` and the skipped half of its filter accounting.
///
/// Every count is an exact `u64`. V1 answers a count it does not know with
/// the in-band marker `-1` and can compute a negative capped count; V2 does
/// neither: a bound that cannot be met refuses before execution, a count
/// saturates instead of wrapping, and the capped count saturates at zero
/// instead of going negative, so no marker value is ever ambiguous with a
/// real count. The decision record cites the V1 sites.
///
/// Equality is over the counts and the state token only. The diagnostic
/// duration is an observation of one call, not a property of the answer, so
/// two executions of one plan against one snapshot are equal results with
/// independently observed durations — the timing must never become part of
/// an answer's identity.
#[derive(Clone, Copy, Debug)]
pub struct QueryStatistics {
    scanned: u64,
    skipped: u64,
    capped_matched: u64,
    result_etag: ResultEtag,
    duration: DiagnosticDuration,
}

impl PartialEq for QueryStatistics {
    fn eq(&self, other: &Self) -> bool {
        self.scanned == other.scanned
            && self.skipped == other.skipped
            && self.capped_matched == other.capped_matched
            && self.result_etag == other.result_etag
    }
}

impl Eq for QueryStatistics {}

impl QueryStatistics {
    /// Assembles the statistics of one execution. Crate-only: execution is
    /// the only producer, and the capped count is derived here so every
    /// producer applies the same saturating window rule.
    pub(crate) fn new(
        matched: u64,
        paging: QueryPaging,
        scanned: u64,
        skipped: u64,
        result_etag: ResultEtag,
        duration: DiagnosticDuration,
    ) -> Self {
        // The V1 capped count — `min(limit, total - offset)` clamped at
        // zero on V1's index path but unclamped (and so possibly negative)
        // on its collection path. V2 saturates both ends: the offset
        // subtracts to zero, never below, and no limit means no cap.
        let remaining = matched.saturating_sub(paging.offset());
        let capped_matched = match paging.limit() {
            Some(limit) => remaining.min(u64::from(limit)),
            None => remaining,
        };
        Self { scanned, skipped, capped_matched, result_etag, duration }
    }

    /// Returns the number of candidate documents of the source collection
    /// the predicate was evaluated against.
    ///
    /// Documents of other collections are not candidates and are counted
    /// nowhere. This is the diagnostic accounting surface for V1's
    /// `ScannedResults`; it is an observation of this one execution, not a
    /// claim about any other execution of the same query.
    #[must_use]
    pub const fn scanned(&self) -> u64 {
        self.scanned
    }

    /// Returns the number of scanned candidates that did not match the
    /// predicate.
    ///
    /// This is the skipped half of V1's filter accounting. It does not
    /// include the page offset: V2 pages over the ordered matched set, so
    /// the matched count stays exact and the caller never adjusts a page
    /// start by the skipped count the way V1's filtered collection queries
    /// must.
    #[must_use]
    pub const fn skipped(&self) -> u64 {
        self.skipped
    }

    /// Returns the matched count as the paging window caps it: the smaller
    /// of the page limit and the matches remaining after the offset, and
    /// zero when the offset reaches past the matched set.
    ///
    /// This is V1's `CappedMaxResults` with V2's saturating rule: never
    /// negative, never a `-1` marker, and exact for the window it
    /// describes. The uncapped matched count stays on
    /// [`QueryResult::matched`](super::QueryResult::matched).
    #[must_use]
    pub const fn capped_matched(&self) -> u64 {
        self.capped_matched
    }

    /// Returns the state token of the state this answer was computed from.
    ///
    /// See [`ResultEtag`] for exactly what the token is and is not.
    #[must_use]
    pub const fn result_etag(&self) -> ResultEtag {
        self.result_etag
    }

    /// Returns the diagnostic duration of this one execution window.
    ///
    /// Diagnostic-only, never a speed claim: see [`DiagnosticDuration`] for
    /// exactly what the number is and is not.
    #[must_use]
    pub const fn duration(&self) -> DiagnosticDuration {
        self.duration
    }
}
