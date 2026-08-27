//! The auto-index lifecycle: merge, supersede, and retire (IX-03, ADR 0010
//! D-4).
//!
//! This module owns the automatic lifecycle of optimizer-created indexes
//! against the engine-owned [`IndexCatalog`](crate::index::IndexCatalog):
//! the query-path supersede (a partial match widens the mapping into the
//! covering identity), the background surpass pass (an auto index another
//! auto index of the same collection completely serves is retired; a
//! partially served one is extended into the union, one extension per pass),
//! and idle retirement (unqueried past the idle threshold an auto index is
//! demoted but stays selectable; still unqueried past the deletion threshold
//! it is deleted). Every transition records the measured value and the
//! threshold that produced it, exactly as V1's observer commands do, and no
//! transition issues while a namespace's usage report is missing — V1's
//! fail-closed observer rule (AX-21).
//!
//! # Where the pass runs
//!
//! [`maintain`] is the one maintenance entry. It runs at the head of every
//! dynamic query's optimizer pass, so selection decides against
//! post-maintenance truth and one public-seam query is enough to drive the
//! whole state machine. V1 runs the surpass pass on database idle
//! operations; V2 maps that to [`Engine::maintain_indexes`], the engine-owned
//! tick a host schedules on its own timer (PD-2026-08-26-01 D1: cadence is
//! host-side, the pass is engine-side), with the thresholds configured
//! through [`EngineConfig`](crate::engine::EngineConfig) at open. The query
//! path stays the live driver, so a host without a timer loses no
//! correctness — only idle-time retirement latency.
//!
//! # Deletion is always drain-waited
//!
//! V1 deletes a surpassed index immediately in the background pass and
//! drain-waits only on the query path (AX-17/AX-18). V2 routes **every**
//! auto-index deletion through one rule: the replacement's covered storage
//! sequence must reach the target's (the catalog's coverage parity, the
//! adaptation of V1's etag parity), and only then does a drain wait start,
//! measured against [`LifecycleThresholds::supersede_drain`]. Replacement
//! never interrupts in-flight queries at this seam — execution reads the
//! bounded snapshot scan, never live index state — and the drain wait keeps
//! that true for every future index-consuming path. A zero drain is V1's
//! documented test mode and resolves the deletion in the same pass.
//!
//! # Not claimed
//!
//! No performance claim of any kind (D-6; receipts belong to IX-04). The
//! in-memory catalog does not persist lifecycle state across a restart;
//! recovery re-derives it deterministically from the same query shapes,
//! which the recovery drill in this packet's tests covers.

use std::{collections::BTreeMap, time::Duration};

use super::{IndexField, IndexKind, QueryDemand, auto_definition_of, auto_index_name, verdict_of};
use crate::{
    engine::Engine,
    index::{IndexBuilder, IndexCatalog},
    types::TenantId,
};

/// The lifecycle state of one index as the typed reads report it.
///
/// Live auto indexes are [`Active`](Self::Active) or
/// [`Idle`](Self::Idle) — idle means demoted but selectable (AX-22). A
/// [`Retired`](Self::Retired) entry is a deletion tombstone kept for a
/// bounded window so the retirement stays observable; explicit indexes are
/// always reported [`Active`](Self::Active) and never carry transitions.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexLifecycleState {
    /// Serving selections normally.
    Active,
    /// Demoted for preference ranking, still selectable, woken
    /// synchronously by the next query that selects it.
    Idle,
    /// Deleted; the entry is a bounded tombstone carrying the recorded
    /// retirement transition.
    Retired,
}

/// One recorded lifecycle transition of one index.
///
/// The measured value is what the pass observed (the unqueried period, the
/// drain since coverage parity) and the threshold is the configured bound
/// the transition fired against; both ride the typed index view, which is
/// V1's "reason string quotes the measured idle values and thresholds"
/// contract made typed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexTransition {
    subject: String,
    from: IndexLifecycleState,
    to: IndexLifecycleState,
    measured: Duration,
    threshold: Duration,
}

impl IndexTransition {
    /// Creates one transition. Crate-only: the lifecycle is the only
    /// producer.
    pub(crate) const fn new(
        subject: String,
        from: IndexLifecycleState,
        to: IndexLifecycleState,
        measured: Duration,
        threshold: Duration,
    ) -> Self {
        Self { subject, from, to, measured, threshold }
    }

    /// Returns the name of the index the transition fired on.
    #[must_use]
    pub fn subject(&self) -> &str {
        &self.subject
    }

    /// Returns the state the index transitioned from.
    #[must_use]
    pub const fn from(&self) -> IndexLifecycleState {
        self.from
    }

    /// Returns the state the index transitioned to.
    #[must_use]
    pub const fn to(&self) -> IndexLifecycleState {
        self.to
    }

    /// Returns the measured value the pass observed.
    #[must_use]
    pub const fn measured(&self) -> Duration {
        self.measured
    }

    /// Returns the threshold the transition fired against.
    #[must_use]
    pub const fn threshold(&self) -> Duration {
        self.threshold
    }
}

/// The configured bounds of the lifecycle state machine.
///
/// The defaults carry V1's observer defaults (AX-21: 30 minutes to idle,
/// 72 hours to delete; AX-17: a 15-second drain before deleting a
/// superseded index). They are initial values, tunable engine
/// configuration, and subject to measurement — never performance claims.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LifecycleThresholds {
    idle_after: Duration,
    delete_after_idle: Duration,
    supersede_drain: Duration,
}

impl LifecycleThresholds {
    /// V1's carried defaults: idle after 30 minutes unqueried, delete after
    /// 72 hours idle, a 15-second drain before deleting a superseded index.
    pub const DEFAULT: Self = Self {
        idle_after: Duration::from_mins(30),
        delete_after_idle: Duration::from_hours(72),
        supersede_drain: Duration::from_secs(15),
    };

    /// Creates one threshold set. A zero value is V1's documented test mode
    /// for that gate (the transition fires on the next pass that meets the
    /// gate); every value is a valid bound.
    #[must_use]
    pub const fn new(
        idle_after: Duration,
        delete_after_idle: Duration,
        supersede_drain: Duration,
    ) -> Self {
        Self { idle_after, delete_after_idle, supersede_drain }
    }

    /// Returns the unqueried period that demotes an auto index to idle.
    #[must_use]
    pub const fn idle_after(&self) -> Duration {
        self.idle_after
    }

    /// Returns the idle period after which the auto index is deleted.
    #[must_use]
    pub const fn delete_after_idle(&self) -> Duration {
        self.delete_after_idle
    }

    /// Returns the drain wait between replacement coverage parity and the
    /// superseded index's deletion.
    #[must_use]
    pub const fn supersede_drain(&self) -> Duration {
        self.supersede_drain
    }
}

impl Default for LifecycleThresholds {
    fn default() -> Self {
        Self::DEFAULT
    }
}

/// The maximum number of transitions retained per index in the typed view.
pub(crate) const MAX_TRANSITIONS_PER_INDEX: usize = 16;

/// The maximum number of retirement tombstones retained per tenant.
pub(crate) const MAX_RETAINED_RETIRED: usize = 16;

/// Runs one lifecycle maintenance pass over the catalog (ADR 0010 D-4).
///
/// The pass advances pending supersessions (driving the replacement's
/// catch-up build when it is behind), runs the revision-guarded surpass
/// pass, retires idle auto indexes past their thresholds, and advances the
/// pending supersessions once more so a zero drain — V1's test mode —
/// resolves in the same pass. It is bounded, holds no lock across an Engine
/// call (builds happen through the builder's private-build-then-publish
/// path), and never touches an explicit index.
pub(crate) fn maintain(engine: &Engine, catalog: &IndexCatalog, engine_identity: u128) {
    advance_pending_supersessions(engine, catalog, engine_identity);
    surpass_pass(engine, catalog, engine_identity);
    retire_idle_indexes(catalog);
    advance_pending_supersessions(engine, catalog, engine_identity);
}

/// Widens one demand with the fields of the partially matching auto indexes
/// and returns the widened demand with the entries it supersedes (AX-16).
///
/// The union never invents fields — every added field comes from a partial
/// auto candidate's own definition — and explicit indexes never contribute:
/// a dynamic query must not disturb an explicit index, so a partial
/// explicit candidate is recorded as a verdict but never widened into the
/// identity (AX-26, ADR 0010 D-3). The definition bound caps the union; an
/// entry is superseded only when the final field set completely covers it.
pub(crate) fn widen_from_partials(
    demand: &QueryDemand,
    entries: &[crate::index::DefinitionEntry],
) -> (QueryDemand, Vec<crate::index::DefinitionEntry>) {
    let mut partials: Vec<&crate::index::DefinitionEntry> = entries
        .iter()
        .filter(|entry| {
            entry.definition().kind() == IndexKind::Auto
                && matches!(verdict_of(entry, demand), super::CandidateVerdict::Partial(_))
        })
        .collect();
    // V1 widens from the best partial match first: the most up to date,
    // then the widest.
    partials.sort_by(|left, right| {
        let left_key = (left.covered_sequence().unwrap_or(0), left.definition().fields().len());
        let right_key = (right.covered_sequence().unwrap_or(0), right.definition().fields().len());
        right_key.cmp(&left_key)
    });

    let mut fields: Vec<IndexField> = demand.fields().to_vec();
    for entry in &partials {
        for field in entry.definition().fields() {
            if fields.iter().any(|existing| existing.name() == field.name()) {
                continue;
            }
            if fields.len() >= super::MAX_DEFINITION_FIELDS {
                break;
            }
            if let Ok(indexed) = IndexField::try_new(field.name(), field.capabilities()) {
                fields.push(indexed);
            }
        }
    }
    let widened = QueryDemand::new(demand.collection().to_owned(), fields);
    let superseded = partials
        .into_iter()
        .filter(|entry| {
            entry
                .definition()
                .fields()
                .iter()
                .all(|field| widened.fields().iter().any(|wider| wider.name() == field.name()))
        })
        .cloned()
        .collect::<Vec<_>>();
    (widened, superseded)
}

/// Advances every pending supersession: drives the replacement's catch-up
/// build when its coverage is behind, starts the drain clock at coverage
/// parity, and deletes the target once the drain has elapsed (AX-17).
fn advance_pending_supersessions(engine: &Engine, catalog: &IndexCatalog, engine_identity: u128) {
    let drain = catalog.lifecycle_thresholds().supersede_drain();
    for pending in catalog.pending_supersessions() {
        // Behind coverage: drive one catch-up build through the
        // compare-and-publish builder. A replayed or non-advancing build is
        // rejected inside and simply leaves the pending record waiting.
        if !catalog.supersede_at_parity(&pending)
            && let Some(delta) = catalog.delta()
        {
            let _ = IndexBuilder::new(catalog, engine).rebuild_from(
                pending.replacement(),
                &delta,
                engine_identity,
            );
        }
        // Start the drain clock at parity if it has not started; a zero
        // drain — V1's test mode — then resolves in this same call.
        catalog.note_supersede_parity(&pending);
        if let Some(elapsed) = catalog.supersede_drain_elapsed(&pending)
            && elapsed >= drain
        {
            catalog.retire(pending.target(), elapsed, drain);
        }
    }
}

/// Runs the background surpass pass: an auto index that another auto index
/// of the same collection completely serves, at least as up to date, is
/// scheduled for supersede; a partially served one is extended into the
/// union identity, one extension per pass (AX-18).
///
/// The pass is guarded by the catalog revision — V1's database-record etag
/// guard — so an unchanged catalog skips the pairwise work entirely.
fn surpass_pass(engine: &Engine, catalog: &IndexCatalog, engine_identity: u128) {
    if !catalog.surpass_pass_due() {
        return;
    }
    for tenant in catalog.tenants_with_definitions() {
        surpass_tenant(engine, catalog, &tenant, engine_identity);
    }
    catalog.mark_surpass_pass_done();
}

/// Runs the surpass pass for one tenant's auto indexes.
fn surpass_tenant(
    engine: &Engine,
    catalog: &IndexCatalog,
    tenant: &TenantId,
    engine_identity: u128,
) {
    let mut by_collection: BTreeMap<String, Vec<crate::index::DefinitionEntry>> = BTreeMap::new();
    for entry in catalog.definitions(tenant) {
        if entry.definition().kind() == IndexKind::Auto {
            by_collection
                .entry(entry.definition().collection().as_str().to_owned())
                .or_default()
                .push(entry);
        }
    }
    for (_, group) in by_collection {
        let mut removals: Vec<(usize, usize)> = Vec::new(); // (target, replacement)
        let mut extensions: Vec<usize> = Vec::new();
        for (subject, entry) in group.iter().enumerate() {
            if removals.iter().any(|(target, _)| *target == subject) {
                continue;
            }
            let subject_demand = QueryDemand::new(
                entry.definition().collection().as_str().to_owned(),
                entry.definition().fields().to_vec(),
            );
            let mut extended = false;
            for (other, candidate) in group.iter().enumerate() {
                if other == subject || removals.iter().any(|(target, _)| *target == other) {
                    continue;
                }
                match verdict_of(candidate, &subject_demand) {
                    // Completely served by an at-least-as-up-to-date index:
                    // the subject is surpassed (AX-18's deletion rule).
                    super::CandidateVerdict::Complete
                    | super::CandidateVerdict::CompleteButIdle
                        if candidate.covered_sequence().unwrap_or(0)
                            >= entry.covered_sequence().unwrap_or(0) =>
                    {
                        removals.push((subject, other));
                        extended = false;
                        break;
                    }
                    // Partially served: extend the subject with the other's
                    // fields (AX-18's merge rule).
                    super::CandidateVerdict::Partial(_) => extended = true,
                    super::CandidateVerdict::Complete
                    | super::CandidateVerdict::CompleteButIdle
                    | super::CandidateVerdict::Failure(_) => {}
                }
            }
            if extended && !removals.iter().any(|(target, _)| *target == subject) {
                extensions.push(subject);
            }
        }

        for (target, replacement) in removals {
            catalog.schedule_supersede(group[replacement].namespace(), group[target].namespace());
        }

        // One extension per pass (AX-18): the first extendable subject in
        // catalog order becomes the union identity, and its own deletion
        // follows the drain-waited path like every other.
        for subject in extensions {
            let entry = &group[subject];
            let (union_demand, _) = widen_from_partials(
                &QueryDemand::new(
                    entry.definition().collection().as_str().to_owned(),
                    entry.definition().fields().to_vec(),
                ),
                &group,
            );
            let definition = auto_definition_of(&union_demand);
            if auto_index_name(&definition) == entry.namespace().name() {
                continue; // nothing actually widened: the extension is a no-op
            }
            let Ok(registration) = catalog.register_auto(tenant, &definition) else {
                continue; // a full registry or an identity conflict waits
            };
            if !registration.created() {
                continue; // the union already exists; the next pass surpasses with it
            }
            if let Some(delta) = catalog.delta() {
                let _ = IndexBuilder::new(catalog, engine).rebuild_from(
                    registration.namespace(),
                    &delta,
                    engine_identity,
                );
            }
            catalog.schedule_supersede(registration.namespace(), entry.namespace());
            break;
        }
    }
}

/// Retires idle auto indexes: past the idle threshold an active index is
/// demoted to idle; past the deletion threshold an idle index is deleted
/// (AX-21). No transition issues while the usage report is missing, and an
/// explicit index is never considered.
fn retire_idle_indexes(catalog: &IndexCatalog) {
    let thresholds = catalog.lifecycle_thresholds();
    let now = catalog.now();
    for tenant in catalog.tenants_with_definitions() {
        for entry in catalog.definitions(&tenant) {
            if entry.definition().kind() != IndexKind::Auto {
                continue;
            }
            let Some(queried_at) = entry.last_queried() else {
                continue; // reporting incomplete: the fail-closed observer rule
            };
            match entry.state() {
                IndexLifecycleState::Active => {
                    let unqueried = now.saturating_duration_since(queried_at);
                    if unqueried >= thresholds.idle_after() {
                        catalog.demote_to_idle(
                            entry.namespace(),
                            unqueried,
                            thresholds.idle_after(),
                        );
                    }
                }
                IndexLifecycleState::Idle => {
                    let Some(idle_since) = entry.idle_since() else {
                        continue;
                    };
                    let idle_for = now.saturating_duration_since(idle_since);
                    if idle_for >= thresholds.delete_after_idle() {
                        catalog.retire(entry.namespace(), idle_for, thresholds.delete_after_idle());
                    }
                }
                IndexLifecycleState::Retired => {}
            }
        }
    }
}
