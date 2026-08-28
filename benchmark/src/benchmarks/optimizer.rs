//! The IX-04 optimizer measurement class: the auto-indexed ad-hoc
//! workloads, their written budgets, and the receipts that decide them.
//!
//! This is the measured half of charter PD-2026-08-26-09 D3 (the optimizer
//! is measured: selection quality, auto-index overhead, and end-to-end
//! query performance) run under PD-2026-08-26-03's benchmark-mandatory rule.
//! ADR 0010 D-6 names IX-04 as the owner of these receipts.
//!
//! What lives here, in the SE-15 lane's shape:
//!
//! - the **ad-hoc workload class as data** ([`OptimizerWorkload`]): each
//!   workload's TerraBaseV2 operation and PostgreSQL equivalent, including
//!   the fairness asymmetry the charter requires be stated rather than
//!   hidden — PostgreSQL has no automatic index creation, so the comparable
//!   act on the query workloads is the operator's manual `CREATE INDEX`,
//!   and the write-path workload compares TerraBaseV2 against itself;
//! - the **budgets as constants** ([`budgets`]), each one carrying the
//!   identifier and the measured quantity that decides it. The budgets were
//!   written before any measurement ran
//!   (`docs/benchmarks/ix04-optimizer-evidence.md`); the constants keep the
//!   decision beside the driver that enforces it;
//! - the **driver** ([`AdHocLane`]) that really runs the workloads against a
//!   durable engine and returns exact, deterministic counters — selections,
//!   creations, cache hits, recorded reasons, live auto indexes, durable
//!   bytes;
//! - the **receipt renderer** ([`OptimizerRunRecord`]) emitting the same
//!   canonical lines every performance receipt carries.
//!
//! ## What this adapter does not do
//!
//! It holds no PostgreSQL client and runs no V1 server, so no objective
//! statement can come out of it on this host: the PostgreSQL reference
//! baseline is `Unrun` with its reason and owner
//! ([`super::reference`]), and the objective status of every receipt here is
//! `inconclusive`. The one comparison that **does** exist is the write-path
//! overhead comparison — TerraBaseV2 with active auto indexes against
//! TerraBaseV2 without — and its verdict is recorded with the exact
//! vocabulary while its objective status stays `inconclusive`.
//!
//! Execution remains the W3-2 bounded snapshot scan: no query here is
//! narrowed or ordered by index data, so nothing in this module is evidence
//! that an auto index made a query *faster*. What is measured is the
//! optimizer's own behavior — what it selected, what it created, what that
//! cost on the durable write path, and that it stayed inside its bounds.
//!
//! No measurement hook is added to `src/query/optimizer/` or `src/index/`:
//! everything below reads the typed reads that already exist
//! (`QueryStore::recent_index_selections`, `QueryStore::registered_indexes`)
//! plus the lane's durable-byte counter.

#![allow(clippy::doc_markdown)] // dense with the proper nouns PostgreSQL and TerraBaseV2

use std::{collections::BTreeMap, fmt::Write as _, time::Duration};

use crate::{
    operations::QueryInput,
    query::{
        IndexNamespace, QueryCondition, QueryOrdering, QueryPaging, QueryPredicate,
        QueryProjection, QuerySpec, QueryStore, StalenessMode, StalenessPolicy,
        optimizer::{
            CandidateVerdict, IndexCapabilities, IndexDefinition, IndexField, IndexKind,
            IndexLifecycleState, IndexSelectionRecord, RegisteredIndex, SelectionOutcome,
        },
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, TenantId,
    },
};

use super::{
    driver::V2Lane,
    gates::{self, MedianRange, Trend},
    lane::LaneConfig,
    reference,
    vocabulary::{self, ObjectiveStatus, Verdict},
};

/// One workload of the auto-indexed ad-hoc class, mapped on both sides.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum OptimizerWorkload {
    /// Sequential ad-hoc shapes over one collection: each first sighting
    /// triggers one optimizer pass on a served execution.
    AdHocSingleCollection,
    /// One ad-hoc shape per collection across many collections, each
    /// repeated: the cross-collection ad-hoc stream.
    AdHocCrossCollection,
    /// The acknowledged write stream with active auto indexes, against the
    /// same stream with none.
    AdHocWritePath,
    /// Distinct shapes driven past the per-tenant auto-index registry bound.
    AdHocRegistryBound,
}

impl OptimizerWorkload {
    /// Every workload of the class.
    pub(crate) const ALL: [Self; 4] = [
        Self::AdHocSingleCollection,
        Self::AdHocCrossCollection,
        Self::AdHocWritePath,
        Self::AdHocRegistryBound,
    ];

    /// The receipt token.
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::AdHocSingleCollection => "auto-index-adhoc-single-collection",
            Self::AdHocCrossCollection => "auto-index-adhoc-cross-collection",
            Self::AdHocWritePath => "auto-index-adhoc-write-path",
            Self::AdHocRegistryBound => "auto-index-adhoc-registry-bound",
        }
    }

    /// The TerraBaseV2 operation this workload measures.
    pub(crate) const fn terrabase_v2_operation(self) -> &'static str {
        match self {
            Self::AdHocSingleCollection => {
                "Engine::queries().compile + execute — S sequential ad-hoc \
                 equality shapes (distinct field sets) over one seeded collection; every \
                 execution runs one optimizer pass that selects, widens, or creates"
            }
            Self::AdHocCrossCollection => {
                "Engine::queries().compile + execute — one ad-hoc shape per collection across \
                 C seeded collections, each repeated R times: creation on first sighting, then \
                 selection against post-maintenance truth"
            }
            Self::AdHocWritePath => {
                "Engine::begin + Transaction::put + commit(LocalDurability::Synced) — the \
                 acknowledged write stream with K active auto indexes, against the same stream \
                 with none, measured by the lane's deterministic durable-byte counter"
            }
            Self::AdHocRegistryBound => {
                "Engine::queries().compile + execute — distinct ad-hoc shapes driven past \
                 MAX_AUTO_INDEXES_PER_TENANT: the registry bound and its fail-closed refusal"
            }
        }
    }

    /// The PostgreSQL equivalent, as the SQL the reference side would run.
    pub(crate) const fn postgres_equivalent(self) -> &'static str {
        match self {
            Self::AdHocSingleCollection | Self::AdHocCrossCollection => {
                "-- one CREATE INDEX per distinct shape, then the SELECT it serves.\n-- \
                 PostgreSQL has no automatic index creation: the comparable act is the \
                 operator's manual CREATE INDEX, decided and run by a human. That asymmetry \
                 is the workload's stated fairness note, not a hidden tuning difference."
            }
            Self::AdHocWritePath => {
                "-- INSERT INTO t (...) VALUES (...) ON CONFLICT DO UPDATE, synchronous_commit \
                 = on,\n-- on a table carrying K indexes, against the same table carrying \
                 none: index maintenance on the write path"
            }
            Self::AdHocRegistryBound => {
                "-- no PostgreSQL equivalent: PostgreSQL's index count per table is \
                 operator-chosen and bounded by no engine rule, so no shared budget exists; \
                 this workload compares TerraBaseV2 against its own written bound"
            }
        }
    }

    /// Whether this workload has a PostgreSQL comparand at all. The first
    /// three name one; the registry bound does not, and says so rather than
    /// implying a comparison nobody can run.
    pub(crate) const fn has_postgresql_equivalent(self) -> bool {
        !matches!(self, Self::AdHocRegistryBound)
    }

    /// The reason this workload has no driver here, with its owner. All four
    /// are driven in-process against the engine core, so the class declares
    /// no open adapter slot.
    pub(crate) const fn driver_status(self) -> Option<(&'static str, &'static str)> {
        None
    }
}

/// The written budgets of the optimizer measurement class, as constants.
///
/// Each value is the budget the receipts decide; the identifier is the one
/// `docs/benchmarks/ix04-optimizer-evidence.md` maps to its test. The
/// budgets were written before any measurement ran.
pub(crate) mod budgets {
    /// SQ-B1 — the count of selection records in which a complete candidate
    /// was not chosen and carried no recorded reason.
    pub const UNEXPLAINED_UNCHOSEN_COMPLETE: u64 = 0;

    /// OH-B1 — durable bytes one auto-index creation appends.
    pub const DURABLE_BYTES_PER_CREATION: u64 = 0;

    /// OH-B3 — additional durable bytes per acknowledged write per active
    /// auto index.
    pub const DURABLE_BYTES_PER_WRITE_PER_ACTIVE_INDEX: u64 = 0;

    /// MB-B1 — the live auto-index registry bound, the engine's own
    /// `MAX_AUTO_INDEXES_PER_TENANT`.
    pub const LIVE_AUTO_PER_TENANT: usize = crate::query::optimizer::MAX_AUTO_INDEXES_PER_TENANT;

    /// MB-B2 — the retained selection-record bound, the engine's own
    /// `MAX_SELECTION_RECORDS`.
    pub const RETAINED_SELECTION_RECORDS: usize = crate::query::optimizer::MAX_SELECTION_RECORDS;
}

/// The exact, deterministic counters of one measured ad-hoc window.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct AdHocWindow {
    /// Ad-hoc query operations the window completed.
    pub operations: u64,
    /// Documents the window returned to the caller.
    pub documents_returned: u64,
    /// Durable bytes the window appended to the engine location.
    pub durable_bytes_written: u64,
    /// Selection records the window's queries produced.
    pub selections: u64,
    /// Selections that created an auto index.
    pub creations: u64,
    /// Selections served by an existing index.
    pub chosen: u64,
    /// Selections served from the bounded selection cache.
    pub cache_hits: u64,
    /// Selections that were pure collection scans.
    pub collection_scans: u64,
    /// Selections whose creation was refused by a fail-closed guard.
    pub refusals: u64,
    /// SQ-B1: selections where a complete candidate lost without a recorded
    /// reason. The budget is zero.
    pub unexplained_unchosen_complete: u64,
    /// Live auto indexes after the window, from the typed read.
    pub live_auto_after: usize,
    /// Retired (deleted) auto indexes still observable after the window.
    pub retired_after: usize,
}

impl AdHocWindow {
    /// Returns the selection records the window produced, classified.
    ///
    /// The four outcome counters must account for every selection the window
    /// produced — a window whose numbers do not add up is not a window the
    /// receipt can cite.
    #[must_use]
    pub(crate) const fn outcomes_accounted(&self) -> bool {
        self.selections == self.creations + self.chosen + self.collection_scans + self.refusals
    }
}

/// The outcome of one registry-bound window: how far the stream got before
/// the bounded registry refused.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct RegistryWindow {
    /// Shapes driven before the first refusal.
    pub shapes_served: u64,
    /// Shapes refused by the bounded registry.
    pub shapes_refused: u64,
    /// Live auto indexes after the window.
    pub live_auto_after: usize,
    /// Retired auto indexes still observable after the window.
    pub retired_after: usize,
}

/// The write-path overhead comparison: the one comparison that exists on
/// this host, both sides TerraBaseV2.
#[derive(Clone, Debug)]
pub(crate) struct WritePathComparison {
    /// Durable bytes per write with no active auto index, one entry per
    /// measured round.
    pub without_auto_indexes: Vec<f64>,
    /// Durable bytes per write with the active auto indexes, one entry per
    /// measured round.
    pub with_auto_indexes: Vec<f64>,
    /// Active auto indexes during the measured window.
    pub active_auto_indexes: usize,
    /// The gate's classification of the counter (lower is better).
    pub trend: Trend,
    /// The comparison verdict, from the closed vocabulary.
    pub verdict: Verdict,
}

impl WritePathComparison {
    /// Runs the gate over both sides' durable-bytes-per-write samples and
    /// maps the trend onto the verdict vocabulary.
    ///
    /// Lower is better here: `Stable` is the budget met (`no-improvement`),
    /// `Worse` — the auto-index side appending measurably more — is the
    /// budget breached (`v2-regression`), and `Better` is recorded as
    /// `v2-faster` rather than absorbed.
    pub(crate) fn of(
        without_auto_indexes: Vec<f64>,
        with_auto_indexes: Vec<f64>,
        active_auto_indexes: usize,
    ) -> Self {
        let current = MedianRange::of(&with_auto_indexes).expect("measured samples");
        let prior = MedianRange::of(&without_auto_indexes).expect("measured samples");
        let trend =
            gates::classify_lower(&current, &prior, LaneConfig::RECORD_MINIMUM.noise_percent);
        let verdict = match trend {
            Trend::Stable => Verdict::NoImprovement,
            Trend::Worse => Verdict::V2Regression,
            Trend::Better => Verdict::V2Faster,
        };
        Self { without_auto_indexes, with_auto_indexes, active_auto_indexes, trend, verdict }
    }
}

/// The in-process driver of the ad-hoc class: a durable engine plus the
/// ad-hoc operations and typed reads the workloads need.
pub(crate) struct AdHocLane {
    lane: V2Lane,
}

impl AdHocLane {
    /// Opens a lane whose engine resolves supersessions inside the window
    /// (a zero supersede drain — V1's documented test mode for that gate;
    /// see the packet record's decision D3).
    pub(crate) fn open(tag: &str) -> Self {
        let defaults = crate::query::optimizer::LifecycleThresholds::DEFAULT;
        let thresholds = crate::query::optimizer::LifecycleThresholds::new(
            defaults.idle_after(),
            defaults.delete_after_idle(),
            Duration::ZERO,
        );
        Self { lane: V2Lane::open_with_lifecycle(tag, thresholds) }
    }

    /// Seeds one collection with `documents` documents carrying `fields`,
    /// each field value unique per document so an equality query on any
    /// field matches exactly one document. The payload matches V1's
    /// benchmark document (256 bytes).
    pub(crate) fn seed_collection(&self, collection: &str, documents: usize, fields: &[&str]) {
        for index in 0..documents {
            let mut values = BTreeMap::new();
            for field in fields {
                values
                    .insert((*field).to_owned(), DocumentValue::String(format!("{field}-{index}")));
            }
            values.insert("payload".to_owned(), DocumentValue::String("x".repeat(256)));
            self.lane.commit_put(
                Document::new(
                    DocumentId::try_new(&format!("{collection}-{index}")).expect("valid id"),
                    CollectionName::try_new(collection).expect("valid collection"),
                    DocumentValue::Object(values),
                ),
                ExpectedVersion::Any,
            );
        }
    }

    /// Registers one explicit index definition through the engine's catalog
    /// — the same typed registration seam the optimizer's own tests drive —
    /// so a workload can measure explicit precedence (ADR 0010 D-3).
    pub(crate) fn register_explicit_index(&self, collection: &str, field: &str) -> String {
        let name = format!("Explicit/{collection}/By{field}");
        let namespace =
            IndexNamespace::try_new(self.tenant().clone(), &name).expect("valid namespace");
        let definition = IndexDefinition::try_new(
            CollectionName::try_new(collection).expect("valid collection"),
            vec![IndexField::try_new(field, IndexCapabilities::FULL).expect("valid field")],
            IndexKind::Explicit,
        )
        .expect("valid definition");
        self.lane
            .engine()
            .index_catalog()
            .register_explicit(&namespace, &definition)
            .expect("registers");
        name
    }

    /// Executes one ad-hoc equality query over `fields` against one
    /// collection, returning the row count, or the typed error a fail-closed
    /// guard produced.
    ///
    /// The queried value addresses seeded document `target`, so a seeded
    /// collection answers with exactly one document.
    pub(crate) fn ad_hoc(
        &self,
        collection: &str,
        fields: &[&str],
        target: usize,
    ) -> Result<usize, crate::query::QueryError> {
        let snapshot = self.lane.engine().read_snapshot(self.lane.scope()).expect("snapshot");
        let store: QueryStore<'_> = self.lane.engine().queries();
        let plan = store
            .compile(QueryInput::typed(self.spec(collection, fields, target)))
            .expect("compiles");
        let result = store.execute(&plan, &snapshot);
        match result {
            Ok(executed) => Ok(executed.rows().len()),
            Err(failure) => Err(failure),
        }
    }

    /// Commits `count` acknowledged writes into `collection`, returning the
    /// durable bytes the stream appended.
    pub(crate) fn write_stream(&self, collection: &str, count: usize) -> u64 {
        let baseline = self.lane.durable_bytes();
        for index in 0..count {
            self.lane.commit_put(
                Document::new(
                    DocumentId::try_new(&format!("{collection}-measured-{index}"))
                        .expect("valid id"),
                    CollectionName::try_new(collection).expect("valid collection"),
                    DocumentValue::Object(BTreeMap::from([
                        ("f0".to_owned(), DocumentValue::String(format!("f0-measured-{index}"))),
                        ("payload".to_owned(), DocumentValue::String("x".repeat(256))),
                    ])),
                ),
                ExpectedVersion::Any,
            );
        }
        self.lane.durable_bytes().saturating_sub(baseline)
    }

    /// The lane's deterministic durable-byte counter.
    pub(crate) fn durable_bytes(&self) -> u64 {
        self.lane.durable_bytes()
    }

    /// The lane's tenant.
    pub(crate) fn tenant(&self) -> &TenantId {
        self.lane.tenant()
    }

    /// One tenant's recent selection records, through the typed read.
    pub(crate) fn selections(&self) -> Vec<IndexSelectionRecord> {
        self.lane.engine().queries().recent_index_selections(self.lane.tenant())
    }

    /// One tenant's registered indexes, through the typed read.
    pub(crate) fn registered(&self) -> Vec<RegisteredIndex> {
        self.lane.engine().queries().registered_indexes(self.lane.tenant())
    }

    /// Shuts the engine down and removes its location.
    pub(crate) fn remove(self) {
        self.lane.remove();
    }

    /// Builds the ad-hoc equality specification: one equality condition per
    /// demanded field, projected to the demanded fields, first row only.
    fn spec(&self, collection: &str, fields: &[&str], target: usize) -> QuerySpec {
        let conditions = fields
            .iter()
            .map(|field| QueryCondition::Equals {
                field: (*field).to_owned(),
                value: DocumentValue::String(format!("{field}-{target}")),
            })
            .collect::<Vec<_>>();
        QuerySpec::try_new(
            self.lane.tenant().clone(),
            crate::query::QuerySource::try_new(
                CollectionName::try_new(collection).expect("valid collection"),
            )
            .expect("collection source"),
            QueryPredicate::all(conditions).expect("valid conjunction"),
            QueryOrdering::try_new(Vec::new()).expect("no ordering"),
            QueryProjection::fields(fields).expect("valid projection"),
            QueryPaging::try_new(0, Some(1)).expect("valid paging"),
            StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
            Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
            Cancellation::new(),
        )
        .expect("valid specification")
    }
}

/// Drives the single-collection ad-hoc workload: every non-empty subset of
/// `fields`, in canonical order, one query per shape.
///
/// Each shape is a first sighting, so each one runs a full optimizer pass
/// that widens the union and creates the covering index (V1 AX-16), and the
/// window's counters say what that cost.
pub(crate) fn run_single_collection(
    lane: &AdHocLane,
    collection: &str,
    fields: &[&str],
) -> AdHocWindow {
    let baseline = lane.durable_bytes();
    let before = lane.selections().len();
    let mut window = AdHocWindow::default();
    for shape in non_empty_subsets(fields) {
        window.operations += 1;
        match lane.ad_hoc(collection, &shape, 0) {
            Ok(rows) => window.documents_returned += u64::try_from(rows).unwrap_or(0),
            Err(_) => window.refusals += 1,
        }
    }
    finish_window(lane, baseline, before, window)
}

/// Drives the cross-collection ad-hoc workload: one shape per collection,
/// each repeated `repeats` times, addressing a different seeded document on
/// each repeat.
pub(crate) fn run_cross_collection(
    lane: &AdHocLane,
    collections: usize,
    fields: &[&str],
    repeats: usize,
) -> AdHocWindow {
    let baseline = lane.durable_bytes();
    let before = lane.selections().len();
    let mut window = AdHocWindow::default();
    for collection in 0..collections {
        let name = format!("adhoc{collection}");
        for repeat in 0..repeats {
            window.operations += 1;
            match lane.ad_hoc(&name, fields, repeat) {
                Ok(rows) => window.documents_returned += u64::try_from(rows).unwrap_or(0),
                Err(_) => window.refusals += 1,
            }
        }
    }
    finish_window(lane, baseline, before, window)
}

/// Drives the registry-bound workload: one shape per fresh collection until
/// the bounded registry refuses, then one more shape past it.
pub(crate) fn run_registry_bound(
    lane: &AdHocLane,
    collections: usize,
    field: &str,
) -> RegistryWindow {
    let mut window = RegistryWindow::default();
    for collection in 0..collections {
        let name = format!("bound{collection}");
        match lane.ad_hoc(&name, &[field], 0) {
            Ok(_) => window.shapes_served += 1,
            Err(_) => window.shapes_refused += 1,
        }
    }
    let registered = lane.registered();
    window.live_auto_after = live_auto_of(&registered);
    window.retired_after = retired_of(&registered);
    window
}

/// Folds the typed reads into one window's counters.
fn finish_window(
    lane: &AdHocLane,
    baseline: u64,
    records_before: usize,
    mut window: AdHocWindow,
) -> AdHocWindow {
    let records = lane.selections();
    window.selections = records.len().saturating_sub(records_before) as u64;
    window.durable_bytes_written = lane.durable_bytes().saturating_sub(baseline);
    for record in records.iter().skip(records_before) {
        match record.outcome() {
            SelectionOutcome::CreatedAutoIndex { .. } => window.creations += 1,
            SelectionOutcome::ChosenIndex { .. } => {
                window.chosen += 1;
                if record.cache_hit() {
                    window.cache_hits += 1;
                }
            }
            SelectionOutcome::CollectionScan => window.collection_scans += 1,
            SelectionOutcome::CreationRefused { .. } => window.refusals += 1,
        }
        // SQ-B1, made mechanical: a complete candidate that lost must carry
        // its recorded reason. The count is the budget the receipt decides.
        for candidate in record.candidates() {
            let complete = matches!(
                candidate.verdict(),
                CandidateVerdict::Complete | CandidateVerdict::CompleteButIdle
            );
            if complete
                && !chosen_index_of(record).is_some_and(|name| name == candidate.name())
                && candidate.note().is_none()
            {
                window.unexplained_unchosen_complete += 1;
            }
        }
    }
    let registered = lane.registered();
    window.live_auto_after = live_auto_of(&registered);
    window.retired_after = retired_of(&registered);
    window
}

/// The index one record's outcome served, when it served one.
fn chosen_index_of(record: &IndexSelectionRecord) -> Option<&str> {
    match record.outcome() {
        SelectionOutcome::ChosenIndex { name, .. }
        | SelectionOutcome::CreatedAutoIndex { name, .. } => Some(name.as_str()),
        SelectionOutcome::CollectionScan | SelectionOutcome::CreationRefused { .. } => None,
    }
}

/// Live auto indexes of one typed read.
fn live_auto_of(registered: &[RegisteredIndex]) -> usize {
    registered
        .iter()
        .filter(|index| {
            index.kind() == IndexKind::Auto && index.state() == IndexLifecycleState::Active
        })
        .count()
}

/// Retired auto indexes of one typed read.
fn retired_of(registered: &[RegisteredIndex]) -> usize {
    registered.iter().filter(|index| index.state() == IndexLifecycleState::Retired).count()
}

/// Every non-empty subset of `fields` in canonical (length, then
/// positional) order — the deterministic shape stream of the
/// single-collection workload.
#[must_use]
pub(crate) fn non_empty_subsets<'a>(fields: &[&'a str]) -> Vec<Vec<&'a str>> {
    let total = 1_usize << fields.len();
    let mut subsets: Vec<Vec<&str>> = (1..total)
        .map(|mask| {
            fields
                .iter()
                .enumerate()
                .filter(|(index, _)| mask & (1 << index) != 0)
                .map(|(_, field)| *field)
                .collect()
        })
        .collect();
    subsets.sort_by(|left, right| left.len().cmp(&right.len()).then_with(|| left.cmp(right)));
    subsets
}

/// One receipt of the optimizer measurement class.
#[derive(Clone, Debug)]
pub(crate) struct OptimizerRunRecord {
    /// The run's label.
    pub label: String,
    /// The workload measured.
    pub workload: OptimizerWorkload,
    /// The TerraBaseV2 source identity measured.
    pub source_identity: String,
    /// The comparison verdict: the closed vocabulary, used only where a
    /// comparison exists.
    pub verdict: Verdict,
    /// The objective status against PostgreSQL.
    pub status: ObjectiveStatus,
    /// Validity notes, retained whether they flatter the run or not.
    pub notes: Vec<String>,
}

impl OptimizerRunRecord {
    /// A receipt whose comparison exists (the write-path overhead budget):
    /// the verdict is the gate's reading of that comparison, and the
    /// objective status is still fail-closed against the unrun PostgreSQL
    /// baseline.
    pub(crate) fn budget_comparison(
        workload: OptimizerWorkload,
        verdict: Verdict,
        notes: Vec<String>,
    ) -> Self {
        let (status, _) = gates::reference_comparison_status(
            &reference::postgresql_reference_run(),
            &reference::terrabase_v1_reference_run(),
            gates::ReferenceComparison { throughput: Trend::Better, p95: Trend::Better },
        );
        Self {
            label: format!("ix04-{}", workload.as_str()),
            workload,
            source_identity: super::record::source_identity(),
            verdict,
            status,
            notes,
        }
    }

    /// A receipt with no comparand: the deterministic counters are facts,
    /// the verdict and the objective status are both `inconclusive`, and
    /// the receipt says why.
    pub(crate) fn counters_only(workload: OptimizerWorkload, notes: Vec<String>) -> Self {
        Self {
            label: format!("ix04-{}", workload.as_str()),
            workload,
            source_identity: super::record::source_identity(),
            verdict: Verdict::Inconclusive,
            status: ObjectiveStatus::Inconclusive,
            notes,
        }
    }

    /// Renders the receipt block a performance statement cites: the same
    /// canonical lines every receipt under `evidence/benchmarks/` carries.
    pub(crate) fn render(&self) -> String {
        let mut text = String::new();
        let _unused = writeln!(text, "run label: {}", self.label);
        let _unused = writeln!(text, "workload: {}", self.workload.as_str());
        let _unused = writeln!(text, "terrabase v2 source: {}", self.source_identity);
        let _unused =
            writeln!(text, "postgresql pin: {}", reference::POSTGRESQL_REFERENCE.describe());
        let _unused =
            writeln!(text, "terrabase v1 pin: {}", reference::TERRABASE_V1_REFERENCE.describe());
        let _unused = writeln!(
            text,
            "postgresql reference: {}",
            reference::postgresql_reference_run().describe()
        );
        let _unused = writeln!(
            text,
            "terrabase v1 reference: {}",
            reference::terrabase_v1_reference_run().describe()
        );
        let _unused = writeln!(text, "verdict: {}", self.verdict.as_str());
        let _unused = writeln!(text, "{}", vocabulary::objective_status_line(self.status));
        if self.status == ObjectiveStatus::NotMet
            && let Some(follow_up) = super::record::RunRecord::regression_follow_up(self.status)
        {
            let _unused = writeln!(text, "regression follow-up owner: {}", follow_up.owner);
            let _unused = writeln!(text, "regression tracking rule: {}", follow_up.rule);
        }
        if !self.workload.has_postgresql_equivalent() {
            let _unused = writeln!(text, "no comparand: {}", self.workload.postgres_equivalent());
        }
        for note in &self.notes {
            let _unused = writeln!(text, "note: {note}");
        }
        text
    }
}
