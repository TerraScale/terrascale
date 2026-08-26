//! Live bucket migration: one bucket's committed records streamed
//! source-to-destination, then the bucket-range transfer with raised epochs
//! (SH-03, PB-5038/5039, ADR 0011 D3; CONTEXT: Bucket Migration).
//!
//! A [`BucketMigration`] is the explicit, idempotent, restartable engine
//! operation that moves one [`Bucket`] from a source shard to a destination
//! shard. It keeps V1's observable status shape — pending, moving, moved,
//! ownership transferred (`docs/sharding/model.md` §5) — and adds the clean
//! rollback the engine contract needs. There is no separate migration
//! transport and no engine-internal orchestrator: the Platform drives the
//! typed operations ([`BucketMigration::stream_round`],
//! [`BucketMigration::confirm_moved`], [`BucketMigration::cut_over`],
//! [`BucketMigration::roll_back`]) and owns every scheduling, placement, and
//! quiescing decision (PB-5039).
//!
//! Streaming rides the same machinery the replication plane's catch-up runs
//! on, composed at the engine seams it is built from: bounded pages of the
//! source's publication journal ([`Engine::tenant_commits`], bounded by
//! [`MAX_MIGRATION_STREAM_BATCH`]), gap detection against the journal's
//! servable bound (falling back to a full re-scan, never silently skipping),
//! and idempotent apply of each committed record under its original
//! operation identity through the durable operation ledger
//! ([`Engine::resolve_commit`] plus the engine transaction seam), so a
//! redelivered or re-scanned record resolves as a duplicate and an operation
//! identity bound to a different write set refuses with the typed
//! [`crate::engine::CommitError::OperationIdReuse`] — record changes never
//! double-apply. The replication member catch-up itself
//! (`src/replication/member.rs`) is member-to-member anti-entropy over whole
//! tenants and has no bucket-scoped serving path; that seam is untouched
//! here and this module filters the bucket at the migration layer instead.
//!
//! The cutover is the composition of SH-02's two table-side facts: the
//! bucket-range edit is exactly [`BucketRangeTable::with_bucket_moved`], and
//! the ownership transfer seats only through
//! [`ShardTopology::transition`](crate::sharding::ShardTopology::transition)'s
//! epoch rules — a shard whose buckets changed must raise its epoch, so a
//! stale owner of the migrated bucket is fenced, never silently lost. The
//! cutover is idempotent (re-issuing the pair after an interrupted seat
//! replays against the already-transferred routing facts) and refuses while
//! any bucket record is still in flight past the confirmed point, which is
//! what bounds the in-flight window: the migration converges by draining the
//! delta, never by cutting over an unbounded backlog. The refusal is scoped
//! to the migrating bucket — the Platform quiesces the migrating bucket for
//! the cutover, not the whole source shard (PB-5039) — so co-tenant journal
//! width past the confirmed point pages through as bounded scan cost and
//! never starves a quiesced bucket's cutover.
//!
//! Both completeness proofs fail closed on a diverged journal: the engine's
//! journal append is deliberately best-effort (W4-CA-4 repair F2), so a
//! commit can be published without being journaled, and a proof the journal
//! alone supplies is blind to exactly that record — the confirmation would
//! declare completeness and the cutover would seat a transfer that strands
//! the unjournaled record on the source. [`Engine::journal_divergence`]
//! counts those commits (derived from the durable ledger against the
//! journal, so the count holds across appends that heal the high-water mark
//! and across reopens), and a nonzero count refuses confirmation and
//! cutover with the typed [`MigrationError::JournalDivergence`] until the
//! Platform repairs the journal — never a silent strand.
//!
//! Boundaries and non-claims: the Platform constructs the next table and
//! view and stops writes to the migrating bucket for the final cutover (the
//! orchestrator coordination boundary of PB-5039); the engine validates and
//! executes. Streaming applies records at
//! [`LocalDurability::Synced`](crate::types::LocalDurability::Synced) at the
//! destination; acknowledgement levels, convergence, and per-shard
//! replication receipts are the existing replication seam's facts and are
//! not re-stated here. Sharded query fan-out (SH-04) and failover
//! qualification (SH-05) are not claimed, kill-point evidence for this
//! module runs in-process, and no sharded performance claim is made.

use std::collections::BTreeMap;

use crate::engine::{
    CommitError, CommitOutcome, DurableResolution, Engine, EngineError, Transaction,
};
use crate::replication::TopologyView;
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    ExpectedVersion, LocalDurability, OperationId, StorageSequence, TenantId, TenantScope,
};

use super::{Bucket, BucketRangeTable, ShardId, ShardKeySet, ShardTopology, ShardingError};

/// The bounded number of source journal entries one streaming round scans.
///
/// The same rule and value as the replication plane's catch-up batch bound
/// (`MAX_CATCH_UP_BATCH` in `src/replication/topology.rs`, mirrored here
/// because that constant is private to the replication module): one round of
/// streaming is never unbounded, so the migration's in-flight work stays a
/// bounded page of the source's publication journal.
pub const MAX_MIGRATION_STREAM_BATCH: u32 = 1024;

/// The observable state of one bucket migration (V1's status shape, kept as
/// the contract; `docs/sharding/model.md` §5).
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum BucketMigrationState {
    /// The migration is seated against the topology and has streamed
    /// nothing yet.
    Pending,
    /// Records of the bucket are streaming source-to-destination.
    Moving,
    /// The destination holds every bucket record the source journaled
    /// through the confirmed point.
    Moved,
    /// The bucket-range transfer seated with raised epochs: the bucket
    /// routes to the destination and the source is fenced.
    OwnershipTransferred,
    /// The migration was abandoned before its cutover; ownership never
    /// changed.
    RolledBack,
}

/// One bucket migration as the Platform drives it (CONTEXT: Bucket
/// Migration).
///
/// The value is the migration's per-drive state: its progress map and
/// resolved-record count are advisory, in-memory facts that do not survive a
/// restart. What is durable is the streamed state itself — each applied
/// record lives in the destination's operation ledger under its original
/// operation identity — so [`BucketMigration::resume`] rebuilds the drive
/// from the routing facts and re-streams idempotently after any
/// interruption.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BucketMigration {
    topology: ShardTopology,
    bucket: Bucket,
    source: ShardId,
    destination: ShardId,
    state: BucketMigrationState,
    resolved: u64,
    confirmed_through: Option<StorageSequence>,
    progress: BTreeMap<TenantId, TenantProgress>,
}

/// The advisory scan progress for one tenant, in the source's own
/// storage-sequence space. In-memory only; restart re-scans idempotently.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct TenantProgress {
    scanned_through: Option<StorageSequence>,
}

impl BucketMigration {
    /// Starts one migration of `bucket` from `source` to `destination`
    /// against the seated topology.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::SourceIsDestination`] when the two shards
    /// are one identity, and [`MigrationError::Routing`] with
    /// [`ShardingError::BucketNotOwnedBySource`] when the seated table does
    /// not route the bucket to `source`.
    pub fn try_new(
        topology: &ShardTopology,
        bucket: Bucket,
        source: ShardId,
        destination: ShardId,
    ) -> Result<Self, MigrationError> {
        if source == destination {
            return Err(MigrationError::SourceIsDestination { bucket, shard: source });
        }
        let owner = topology.shard_of(bucket);
        if owner != source {
            return Err(MigrationError::Routing(ShardingError::BucketNotOwnedBySource {
                bucket,
                source,
                owner,
            }));
        }
        Ok(Self {
            topology: topology.clone(),
            bucket,
            source,
            destination,
            state: BucketMigrationState::Pending,
            resolved: 0,
            confirmed_through: None,
            progress: BTreeMap::new(),
        })
    }

    /// Rebuilds a migration from the seated routing facts after an
    /// interruption.
    ///
    /// Recovery is derived, never remembered: a bucket that already routes
    /// to the destination resumes transferred (a re-issued cutover replays
    /// idempotently), a bucket still on the source resumes pending and
    /// re-streams with every already-applied record resolving as a duplicate
    /// through the operation ledger, and a bucket that moved to a third
    /// shard voids this migration with the typed ownership refusal.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::SourceIsDestination`] when the two shards
    /// are one identity, and [`MigrationError::Routing`] with
    /// [`ShardingError::BucketNotOwnedBySource`] when the bucket routes to
    /// neither the source nor the destination.
    pub fn resume(
        topology: &ShardTopology,
        bucket: Bucket,
        source: ShardId,
        destination: ShardId,
    ) -> Result<Self, MigrationError> {
        if source == destination {
            return Err(MigrationError::SourceIsDestination { bucket, shard: source });
        }
        let owner = topology.shard_of(bucket);
        let state = if owner == destination {
            BucketMigrationState::OwnershipTransferred
        } else if owner == source {
            BucketMigrationState::Pending
        } else {
            return Err(MigrationError::Routing(ShardingError::BucketNotOwnedBySource {
                bucket,
                source,
                owner,
            }));
        };
        Ok(Self {
            topology: topology.clone(),
            bucket,
            source,
            destination,
            state,
            resolved: 0,
            confirmed_through: None,
            progress: BTreeMap::new(),
        })
    }

    /// Returns the migration's observable progress: source, destination,
    /// bucket, state, resolved records, per-tenant scan positions, and the
    /// confirmed point (CONTEXT: the Bucket Migration progress contract).
    #[must_use]
    pub fn progress(&self) -> MigrationProgress {
        MigrationProgress {
            bucket: self.bucket,
            source: self.source,
            destination: self.destination,
            state: self.state,
            resolved_records: self.resolved,
            confirmed_through: self.confirmed_through,
            scanned: self
                .progress
                .iter()
                .filter_map(|(tenant, progress)| {
                    progress.scanned_through.map(|through| (tenant.clone(), through))
                })
                .collect(),
        }
    }

    /// Streams one bounded round of the bucket's committed records for the
    /// tenant the scope names, source journal to destination ledger.
    ///
    /// One round scans at most [`MAX_MIGRATION_STREAM_BATCH`] journal
    /// entries of that tenant, resolves each entry that touches the
    /// migrating bucket at the destination under its original operation
    /// identity (fresh applies and idempotent duplicates alike), and skips
    /// every other entry. A source journal that cannot serve below the
    /// tenant's scan position falls back to a full re-scan of the tenant —
    /// the durable ledger, not the advisory cursor, is what makes the
    /// re-delivery safe. The Platform drives one round per tenant until the
    /// report says the tenant is caught up.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::StepRefused`] from the terminal states,
    /// [`MigrationError::ApplyFailed`] when a record did not apply (a reused
    /// operation identity bound to a different write set refuses with the
    /// typed [`crate::engine::CommitError::OperationIdReuse`], never a
    /// silent skip), and [`MigrationError::Engine`] for the engines' typed
    /// failures.
    pub fn stream_round(
        &mut self,
        source: &Engine,
        destination: &Engine,
        destination_scope: &TenantScope,
    ) -> Result<MigrationStreamReport, MigrationError> {
        if Self::is_terminal(self.state) {
            return Err(MigrationError::StepRefused { state: self.state, step: "stream_round" });
        }
        let tenant = destination_scope.tenant().clone();
        let mut after = self.progress.get(&tenant).and_then(|entry| entry.scanned_through);
        let mut fell_back_to_full_scan = false;
        let servable_from = source.servable_history_start().map_err(MigrationError::Engine)?;
        if migration_gap(after, servable_from) {
            // The journal cannot serve below the advisory position; fall back
            // to a full re-scan of the tenant rather than skip a gap.
            after = None;
            fell_back_to_full_scan = true;
        }
        let keys = self.topology.shard_keys().clone();
        let page = source
            .tenant_commits(
                &tenant,
                after.unwrap_or(StorageSequence::new(0)),
                MAX_MIGRATION_STREAM_BATCH,
            )
            .map_err(MigrationError::Engine)?;
        let mut scanned_through = after;
        let mut bucket_records = 0;
        let mut applied = 0;
        let mut duplicates_resolved = 0;
        for commit in &page {
            scanned_through = Some(commit.sequence());
            if !record_touches_bucket(&keys, commit, self.bucket) {
                continue;
            }
            bucket_records += 1;
            match apply_record(destination, destination_scope, commit)? {
                ApplyStep::Applied => applied += 1,
                ApplyStep::Duplicate => duplicates_resolved += 1,
            }
            self.resolved += 1;
        }
        let high_water = source.journaled_high_water().map_err(MigrationError::Engine)?;
        let tenant_high_water = high_water
            .map(|at| source.tenant_sequence(&tenant, at))
            .transpose()
            .map_err(MigrationError::Engine)?
            .flatten();
        let caught_up = tenant_high_water
            .is_none_or(|water| scanned_through.is_some_and(|scanned| scanned >= water));
        self.progress.entry(tenant.clone()).or_default().scanned_through = scanned_through;
        self.state = BucketMigrationState::Moving;
        Ok(MigrationStreamReport {
            tenant,
            scanned: page.len(),
            bucket_records,
            applied,
            duplicates_resolved,
            scanned_through,
            tenant_high_water,
            caught_up,
            fell_back_to_full_scan,
        })
    }

    /// Confirms the destination holds every bucket record the source
    /// journaled, moving the migration to `Moved`.
    ///
    /// Complete means: every tenant with committed history has been scanned
    /// to the tenant's sequence at the source's durable high water, and the
    /// journal is not diverged — the engine's journal append is
    /// deliberately best-effort (W4-CA-4 repair F2), so a published commit
    /// can be absent from the journal, and a proof the journal alone
    /// supplies would be blind to exactly that record. A nonzero
    /// divergence therefore refuses: the migration stays `Moving` and the
    /// Platform repairs the journal before re-driving, never a silent
    /// strand. The confirmation is a fact about the past, not a freeze:
    /// the Platform stops writes to the migrating bucket for the cutover
    /// (the PB-5039 coordination boundary), and
    /// [`BucketMigration::cut_over`] re-checks the window.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::StepRefused`] from the terminal states,
    /// [`MigrationError::JournalDivergence`] when the source's journal
    /// diverges from its published state (published commits the journal
    /// does not serve), and [`MigrationError::Engine`] for the source's
    /// typed failures.
    pub fn confirm_moved(&mut self, source: &Engine) -> Result<MoveConfirmation, MigrationError> {
        if Self::is_terminal(self.state) {
            return Err(MigrationError::StepRefused { state: self.state, step: "confirm_moved" });
        }
        // Fail closed on a diverged journal: the completeness proof below
        // is journal-only, so a published-but-unjournaled commit is
        // invisible to it and its records would strand on the source.
        let divergence = source.journal_divergence().map_err(MigrationError::Engine)?;
        if divergence > 0 {
            self.state = BucketMigrationState::Moving;
            return Err(MigrationError::JournalDivergence { unjournaled: divergence });
        }
        let high_water = source.journaled_high_water().map_err(MigrationError::Engine)?;
        let at = high_water.unwrap_or(StorageSequence::new(0));
        for tenant in source.committed_tenants().map_err(MigrationError::Engine)? {
            let Some(tenant_water) =
                source.tenant_sequence(&tenant, at).map_err(MigrationError::Engine)?
            else {
                continue;
            };
            let scanned = self.progress.get(&tenant).and_then(|entry| entry.scanned_through);
            if scanned.is_none_or(|scanned| scanned < tenant_water) {
                self.state = BucketMigrationState::Moving;
                return Ok(MoveConfirmation::Outstanding { tenant });
            }
        }
        self.state = BucketMigrationState::Moved;
        self.confirmed_through = high_water;
        Ok(MoveConfirmation::Confirmed { through: high_water })
    }

    /// Executes the cutover: seats the bucket-range transfer with raised
    /// epochs, idempotently.
    ///
    /// The next table must be exactly this migration's bucket move (the
    /// [`BucketRangeTable::with_bucket_moved`] edit of the seated table) and
    /// the next view must seat it through
    /// [`ShardTopology::transition`](crate::sharding::ShardTopology::transition)'s
    /// rules, which force a raised epoch on every shard whose buckets
    /// changed. Before seating, the bounded in-flight window is re-checked
    /// against the source journal: any bucket record past the confirmed
    /// point, or a bucket-bearing delta wider than one bounded page,
    /// refuses with [`MigrationError::InFlightDelta`] and returns the
    /// migration to `Moving` — the delta drains by streaming, never by
    /// cutting over a backlog. The refusal is scoped to the migrating
    /// bucket, because the Platform boundary quiesces the migrating bucket,
    /// not the whole source shard (PB-5039): co-tenant journal width past
    /// the confirmed point pages through as bounded scan cost and never
    /// refuses, so sustained co-tenant writes cannot starve a quiesced
    /// bucket's cutover. A diverged journal (published commits the journal
    /// does not serve) refuses with [`MigrationError::JournalDivergence`]:
    /// the delta re-check reads the journal only, so it cannot prove a
    /// window empty over a record the journal never recorded. When the
    /// seated table already routes the bucket to the destination (an
    /// interrupted seat, recovered by [`BucketMigration::resume`]), the
    /// same pair replays idempotently.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::StepRefused`] outside `Moved` (except the
    /// replay case), [`MigrationError::InFlightDelta`] when bucket records
    /// are in flight past the confirmed point, [`MigrationError::JournalDivergence`]
    /// when the source's journal diverges from its published state,
    /// [`MigrationError::Routing`] when the bucket no longer routes to the
    /// source, [`MigrationError::TableIsNotTheBucketMove`] for any other
    /// table, and [`MigrationError::SeatingRefused`] when the transition
    /// rules refuse the pair (unraised or regressed epochs, unservable
    /// view).
    pub fn cut_over(
        &mut self,
        source: &Engine,
        next_table: BucketRangeTable,
        next_view: TopologyView,
    ) -> Result<CutOverOutcome, MigrationError> {
        // Idempotent completion: the routing facts already seat the
        // transfer, so the only acceptable pair is the seated one replayed.
        if self.topology.table().shard_of(self.bucket) == self.destination {
            if next_table != *self.topology.table() {
                return Err(MigrationError::TableIsNotTheBucketMove {
                    bucket: self.bucket,
                    source: self.source,
                    destination: self.destination,
                });
            }
            let seated = self
                .topology
                .transition(next_table, next_view)
                .map_err(MigrationError::SeatingRefused)?;
            self.state = BucketMigrationState::OwnershipTransferred;
            return Ok(CutOverOutcome { topology: seated, replayed: true });
        }
        if self.state != BucketMigrationState::Moved {
            return Err(MigrationError::StepRefused { state: self.state, step: "cut_over" });
        }
        // Fail closed on a diverged journal before the window check: the
        // re-check below reads the journal only, so a published commit the
        // journal never recorded is invisible to it and its records would
        // strand on the source behind a seated transfer.
        let divergence = source.journal_divergence().map_err(MigrationError::Engine)?;
        if divergence > 0 {
            self.state = BucketMigrationState::Moving;
            self.confirmed_through = None;
            return Err(MigrationError::JournalDivergence { unjournaled: divergence });
        }
        // The bounded in-flight window: cutover admits only an empty delta
        // of the migrating bucket's records past the confirmed point,
        // checked against the source journal. Only the migrating bucket's
        // records refuse the window — the Platform quiesces the migrating
        // bucket, not the whole source shard — so a full page with no
        // bucket record is co-tenant width: keep paging to the tenant's
        // delta end (bounded scan cost, never a refusal), while a page that
        // carries bucket records past the confirmed point refuses, with the
        // page's own last sequence when the page is full (the window is
        // then wider than one bounded page).
        let confirmed = self.confirmed_through.unwrap_or(StorageSequence::new(0));
        let keys = self.topology.shard_keys().clone();
        let batch = usize::try_from(MAX_MIGRATION_STREAM_BATCH).unwrap_or(usize::MAX);
        for tenant in source.committed_tenants().map_err(MigrationError::Engine)? {
            let mut after = confirmed;
            loop {
                let page = source
                    .tenant_commits(&tenant, after, MAX_MIGRATION_STREAM_BATCH)
                    .map_err(MigrationError::Engine)?;
                if page.iter().any(|commit| record_touches_bucket(&keys, commit, self.bucket)) {
                    let past = if page.len() >= batch {
                        page.last().map(PublishedCommit::sequence)
                    } else {
                        Some(confirmed)
                    };
                    return Err(self.in_flight_delta(past));
                }
                if page.len() < batch {
                    break;
                }
                after = page.last().map_or(after, PublishedCommit::sequence);
            }
        }
        // The table edit of a bucket migration is exactly the explicit
        // bucket move; a Platform-issued table that changes anything else is
        // not this operation.
        let expected = self
            .topology
            .table()
            .with_bucket_moved(self.bucket, self.source, self.destination)
            .map_err(MigrationError::Routing)?;
        if next_table != expected {
            return Err(MigrationError::TableIsNotTheBucketMove {
                bucket: self.bucket,
                source: self.source,
                destination: self.destination,
            });
        }
        let seated = self
            .topology
            .transition(next_table, next_view)
            .map_err(MigrationError::SeatingRefused)?;
        self.state = BucketMigrationState::OwnershipTransferred;
        Ok(CutOverOutcome { topology: seated, replayed: false })
    }

    /// Rolls the migration back before its cutover: ownership never changes.
    ///
    /// The records already streamed at the destination are non-authoritative
    /// (the bucket still routes to the source) and idempotent for any future
    /// migration of the bucket: the operation ledger re-resolves them as
    /// duplicates. Rolling back after the transfer refuses — the inverse
    /// move is a new migration the Platform issues, never a silent undo.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::StepRefused`] from the terminal states.
    pub fn roll_back(&mut self) -> Result<RollBackRecord, MigrationError> {
        if Self::is_terminal(self.state) {
            return Err(MigrationError::StepRefused { state: self.state, step: "roll_back" });
        }
        self.state = BucketMigrationState::RolledBack;
        Ok(RollBackRecord {
            bucket: self.bucket,
            source: self.source,
            destination: self.destination,
            resolved_records: self.resolved,
        })
    }

    /// Records a refused in-flight window and returns the typed refusal: the
    /// migration returns to `Moving` so the delta can drain by streaming.
    fn in_flight_delta(&mut self, past: Option<StorageSequence>) -> MigrationError {
        self.state = BucketMigrationState::Moving;
        self.confirmed_through = None;
        MigrationError::InFlightDelta { past: past.unwrap_or(StorageSequence::new(0)) }
    }

    /// Reports whether the state is terminal: a transferred or rolled-back
    /// migration admits no further steps — the Platform issues a new
    /// migration, never a step past the end state.
    const fn is_terminal(state: BucketMigrationState) -> bool {
        matches!(
            state,
            BucketMigrationState::OwnershipTransferred | BucketMigrationState::RolledBack
        )
    }
}

/// The observable progress of one bucket migration: source, destination,
/// resolved records, and the confirmed point (CONTEXT: Bucket Migration).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationProgress {
    bucket: Bucket,
    source: ShardId,
    destination: ShardId,
    state: BucketMigrationState,
    resolved_records: u64,
    confirmed_through: Option<StorageSequence>,
    scanned: Vec<(TenantId, StorageSequence)>,
}

impl MigrationProgress {
    /// Returns the migrating bucket.
    #[must_use]
    pub const fn bucket(&self) -> Bucket {
        self.bucket
    }

    /// Returns the shard the bucket migrates from.
    #[must_use]
    pub const fn source(&self) -> ShardId {
        self.source
    }

    /// Returns the shard the bucket migrates to.
    #[must_use]
    pub const fn destination(&self) -> ShardId {
        self.destination
    }

    /// Returns the migration's observable state.
    #[must_use]
    pub const fn state(&self) -> BucketMigrationState {
        self.state
    }

    /// Returns the bucket records this drive resolved at the destination,
    /// fresh applies and idempotent duplicate resolutions alike (a restart
    /// re-resolves what an earlier drive streamed, by design).
    #[must_use]
    pub const fn resolved_records(&self) -> u64 {
        self.resolved_records
    }

    /// Returns the source high water the destination was confirmed complete
    /// through, once the migration reached `Moved`.
    #[must_use]
    pub const fn confirmed_through(&self) -> Option<StorageSequence> {
        self.confirmed_through
    }

    /// Returns each scanned tenant's advisory position in the source's
    /// storage-sequence space, in canonical tenant order.
    #[must_use]
    pub fn scanned(&self) -> &[(TenantId, StorageSequence)] {
        &self.scanned
    }
}

/// The report of one streaming round.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationStreamReport {
    tenant: TenantId,
    scanned: usize,
    bucket_records: usize,
    applied: usize,
    duplicates_resolved: usize,
    scanned_through: Option<StorageSequence>,
    tenant_high_water: Option<StorageSequence>,
    caught_up: bool,
    fell_back_to_full_scan: bool,
}

impl MigrationStreamReport {
    /// Returns the tenant whose history this round scanned.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the number of journal entries this round scanned (bounded by
    /// [`MAX_MIGRATION_STREAM_BATCH`]).
    #[must_use]
    pub const fn scanned(&self) -> usize {
        self.scanned
    }

    /// Returns the scanned entries that touch the migrating bucket.
    #[must_use]
    pub const fn bucket_records(&self) -> usize {
        self.bucket_records
    }

    /// Returns the bucket records this round applied freshly at the
    /// destination.
    #[must_use]
    pub const fn applied(&self) -> usize {
        self.applied
    }

    /// Returns the bucket records this round resolved as idempotent
    /// duplicates through the destination's operation ledger.
    #[must_use]
    pub const fn duplicates_resolved(&self) -> usize {
        self.duplicates_resolved
    }

    /// Returns this tenant's advisory scan position after the round, in the
    /// source's storage-sequence space.
    #[must_use]
    pub const fn scanned_through(&self) -> Option<StorageSequence> {
        self.scanned_through
    }

    /// Returns the tenant's sequence at the source's durable high water
    /// observed by this round.
    #[must_use]
    pub const fn tenant_high_water(&self) -> Option<StorageSequence> {
        self.tenant_high_water
    }

    /// Reports whether the tenant's scan reached its high water: nothing
    /// more to stream for this tenant from this source.
    #[must_use]
    pub const fn caught_up(&self) -> bool {
        self.caught_up
    }

    /// Reports whether the source journal could not serve below the
    /// advisory position and this round fell back to a full re-scan.
    #[must_use]
    pub const fn fell_back_to_full_scan(&self) -> bool {
        self.fell_back_to_full_scan
    }
}

/// The outcome of one move confirmation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MoveConfirmation {
    /// The destination holds every bucket record through the source's
    /// durable high water; the migration is `Moved`.
    Confirmed {
        /// The source high water the completeness proof covers.
        through: Option<StorageSequence>,
    },
    /// One tenant still owes unscanned history; the migration stays
    /// `Moving`.
    Outstanding {
        /// The first tenant with outstanding history, in canonical order.
        tenant: TenantId,
    },
}

/// The outcome of one cutover.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CutOverOutcome {
    topology: ShardTopology,
    replayed: bool,
}

impl CutOverOutcome {
    /// Returns the seated topology after the transfer: the bucket routes to
    /// the destination at raised epochs.
    #[must_use]
    pub const fn topology(&self) -> &ShardTopology {
        &self.topology
    }

    /// Reports whether this cutover replayed an already-seated transfer
    /// (idempotent completion after an interrupted seat).
    #[must_use]
    pub const fn replayed(&self) -> bool {
        self.replayed
    }
}

/// The record of one clean rollback.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RollBackRecord {
    bucket: Bucket,
    source: ShardId,
    destination: ShardId,
    resolved_records: u64,
}

impl RollBackRecord {
    /// Returns the bucket whose migration rolled back.
    #[must_use]
    pub const fn bucket(&self) -> Bucket {
        self.bucket
    }

    /// Returns the shard the bucket still routes to (ownership never
    /// changed).
    #[must_use]
    pub const fn source(&self) -> ShardId {
        self.source
    }

    /// Returns the shard the migration streamed toward.
    #[must_use]
    pub const fn destination(&self) -> ShardId {
        self.destination
    }

    /// Returns the bucket records this drive had resolved at the
    /// destination before the rollback: non-authoritative, and idempotent
    /// for any future migration of the bucket.
    #[must_use]
    pub const fn resolved_records(&self) -> u64 {
        self.resolved_records
    }
}

/// Why a streamed record did not apply at the destination.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MigrationApplyFailure {
    /// The engine rejected the write set. A
    /// [`crate::engine::CommitError::OperationIdReuse`] here means the
    /// operation identity is bound to a different write set at the
    /// destination: a typed refusal, never a silent skip.
    Rejected(CommitError),
    /// A replayed delete targeted a key whose collection differs from the
    /// one the source recorded.
    KeyMismatch,
    /// The outcome stayed undecided after durable resolution by identity.
    Pending,
}

/// Typed bucket-migration failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MigrationError {
    /// A routing fact refused the operation: the named source does not own
    /// the bucket, or the table edit refused.
    Routing(ShardingError),
    /// The migration named its source as its destination: a bucket never
    /// migrates to the shard that owns it.
    SourceIsDestination {
        /// The bucket the migration named.
        bucket: Bucket,
        /// The shard both sides named.
        shard: ShardId,
    },
    /// The step ran from a state that does not admit it.
    StepRefused {
        /// The state the migration was in.
        state: BucketMigrationState,
        /// The refused step.
        step: &'static str,
    },
    /// The offered table is not this migration's bucket move.
    TableIsNotTheBucketMove {
        /// The bucket this migration moves.
        bucket: Bucket,
        /// The shard it moves from.
        source: ShardId,
        /// The shard it moves to.
        destination: ShardId,
    },
    /// The seating rules refused the offered pair: unraised or regressed
    /// epochs, an unservable view, or a shard without an owner.
    SeatingRefused(ShardingError),
    /// Bucket records are in flight past the confirmed point (or a
    /// bucket-bearing window is wider than one bounded page): the cutover
    /// refused and the migration returned to `Moving`.
    InFlightDelta {
        /// The sequence the refusal saw the window extend past.
        past: StorageSequence,
    },
    /// The source's publication journal diverges from its published state:
    /// `unjournaled` published commits are absent from the journal, so the
    /// journal cannot prove the migration complete. The confirmation or
    /// cutover refused and the migration returned to `Moving`; the
    /// Platform repairs the journal (or re-seats the migration on a
    /// repaired source) before re-driving — the engine never strands the
    /// missing records silently.
    JournalDivergence {
        /// The number of published commits the journal does not serve.
        unjournaled: u64,
    },
    /// A streamed record did not apply at the destination.
    ApplyFailed {
        /// The operation identity that failed.
        operation: OperationId,
        /// The typed failure.
        reason: MigrationApplyFailure,
    },
    /// An engine returned a typed failure.
    Engine(EngineError),
}

/// The outcome of applying one streamed record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ApplyStep {
    /// The record applied freshly under its original operation identity.
    Applied,
    /// The destination's operation ledger had already resolved the
    /// operation: an idempotent duplicate.
    Duplicate,
}

/// Applies one committed record at the destination through the engine
/// transaction seam under its original operation identity, so the durable
/// operation ledger decides between an idempotent replay and the typed
/// identity-reuse refusal.
fn apply_record(
    destination: &Engine,
    destination_scope: &TenantScope,
    commit: &PublishedCommit,
) -> Result<ApplyStep, MigrationError> {
    let already_committed = matches!(
        destination
            .resolve_commit(destination_scope, commit.operation_id().clone())
            .map_err(MigrationError::Engine)?,
        DurableResolution::Committed(_)
    );
    let snapshot = destination.read_snapshot(destination_scope).map_err(MigrationError::Engine)?;
    let mut transaction = destination
        .begin(destination_scope, snapshot, commit.operation_id().clone())
        .map_err(MigrationError::Engine)?;
    stage_record_writes(&mut transaction, commit)?;
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {
            Ok(if already_committed { ApplyStep::Duplicate } else { ApplyStep::Applied })
        }
        CommitOutcome::Rejected(reason) => Err(MigrationError::ApplyFailed {
            operation: commit.operation_id().clone(),
            reason: MigrationApplyFailure::Rejected(reason),
        }),
        CommitOutcome::Unknown(unknown) => {
            match destination
                .resolve_commit(destination_scope, unknown.operation_id().clone())
                .map_err(MigrationError::Engine)?
            {
                DurableResolution::Committed(_) => Ok(ApplyStep::Applied),
                DurableResolution::Rejected(reason) => Err(MigrationError::ApplyFailed {
                    operation: commit.operation_id().clone(),
                    reason: MigrationApplyFailure::Rejected(reason),
                }),
                DurableResolution::Pending => Err(MigrationError::ApplyFailed {
                    operation: commit.operation_id().clone(),
                    reason: MigrationApplyFailure::Pending,
                }),
            }
        }
    }
}

/// Stages one record's write set into a destination transaction, mirroring
/// the replication plane's replay rules: puts stage with
/// [`ExpectedVersion::Any`], deletes stage against the exact recorded key
/// and version, and a key that now lives in another collection is a typed
/// mismatch, never a silent removal.
fn stage_record_writes(
    transaction: &mut Transaction<'_>,
    commit: &PublishedCommit,
) -> Result<(), MigrationError> {
    for write in commit.writes() {
        match write {
            PublishedWrite::Put { document } => {
                transaction
                    .put(document.clone(), ExpectedVersion::Any)
                    .map_err(MigrationError::Engine)?;
            }
            PublishedWrite::Delete { collection, id, version_at_delete } => {
                if let Some(recorded) = collection
                    && let Some(existing) = transaction.read(id).map_err(MigrationError::Engine)?
                    && existing.collection() != recorded
                {
                    return Err(MigrationError::ApplyFailed {
                        operation: commit.operation_id().clone(),
                        reason: MigrationApplyFailure::KeyMismatch,
                    });
                }
                let expected = if *version_at_delete == 0 {
                    ExpectedVersion::Any
                } else {
                    ExpectedVersion::Exact(*version_at_delete)
                };
                transaction.delete(id.clone(), expected).map_err(MigrationError::Engine)?;
            }
        }
    }
    Ok(())
}

/// Reports whether one committed record touches the migrating bucket.
///
/// A record touches the bucket when any write it carries routes there under
/// the topology's shard-key declarations; an atomic record that spans
/// buckets applies atomically at the destination, exactly as it committed at
/// the source. A write whose bucket cannot be resolved — a delete under a
/// field shard key, whose value the journal record does not carry — counts
/// as touching: over-inclusion resolves idempotently, under-inclusion could
/// lose a record.
fn record_touches_bucket(keys: &ShardKeySet, commit: &PublishedCommit, bucket: Bucket) -> bool {
    commit.writes().iter().any(|write| match routed_bucket(keys, write) {
        Some(routed) => routed == bucket,
        None => true,
    })
}

/// Resolves the bucket one write routes to, when the shard key can be
/// resolved from the record alone.
fn routed_bucket(keys: &ShardKeySet, write: &PublishedWrite) -> Option<Bucket> {
    match write {
        PublishedWrite::Put { document } => keys
            .resolve(document.collection(), document.id(), Some(document.value()))
            .ok()
            .map(|key| key.bucket()),
        PublishedWrite::Delete { collection, id, .. } => collection
            .as_ref()
            .and_then(|collection| keys.resolve(collection, id, None).ok())
            .map(|key| key.bucket()),
    }
}

/// Reports whether the source journal cannot serve below the advisory
/// position: the same rule the replication plane's catch-up applies
/// (`catch_up_gap` in `src/replication/member.rs`, mirrored here because
/// that helper is private to the replication module). A gap falls back to a
/// full re-scan, never a silent skip.
fn migration_gap(after: Option<StorageSequence>, servable_from: Option<StorageSequence>) -> bool {
    let required_from = after.map_or(0, StorageSequence::get);
    let servable = servable_from.map_or(0, StorageSequence::get);
    servable > required_from.saturating_add(1)
}
