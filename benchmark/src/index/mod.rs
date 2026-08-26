//! Private index publication: namespaces, epochs, and monotonic generations.
//!
//! This module owns the crate-private index machinery listed in the W3 core
//! contract: `IndexSegment`, `IndexDelta`, `IndexBuilder`, and the index lock.
//! Nothing here is public. Index publication follows storage publication: an
//! immutable commit publication event arrives after storage locks release,
//! the catalog records the derived delta under its own lock, and a builder
//! reads documents, builds a segment privately without any lock held, then
//! publishes under the index lock with a compare-and-publish rule that keeps
//! the visible generation monotonic per namespace and epoch.
//!
//! IX-03 adds the catalog side of the auto-index lifecycle (ADR 0010 D-4):
//! per-namespace lifecycle state and usage reports, pending supersessions
//! with the coverage-parity and drain gates, bounded retirement tombstones,
//! the lifecycle clock and thresholds, the bounded per-tenant auto registry,
//! and the per-tenant selection-record stores. The policy that drives them
//! lives in `src/query/optimizer/lifecycle.rs`; this module owns the guarded
//! state and the mechanics, and every deletion still flows through the same
//! compare-and-publish publication order the builder enforces.
//!
//! W3-2 proves this seam with in-memory segments. Nothing here persists an
//! epoch or generation across a restart, asks Storage to publish a data root,
//! or holds a storage lock while building or publishing.

use std::{
    collections::{BTreeMap, VecDeque},
    sync::{Arc, Mutex},
    time::Instant,
};

#[cfg(test)]
mod tests;

use crate::{
    engine::{CommitPublicationSink, Engine},
    query::{
        GenerationView, IndexNamespace, ObservedGeneration,
        optimizer::{
            IndexDefinition, IndexKind, IndexSelectionRecord, MAX_AUTO_INDEXES_PER_TENANT,
            MAX_CACHED_SELECTIONS, MAX_SELECTION_RECORDS, auto_index_name,
            lifecycle::{
                IndexLifecycleState, IndexTransition, LifecycleThresholds, MAX_RETAINED_RETIRED,
                MAX_TRANSITIONS_PER_INDEX,
            },
        },
    },
    storage::CommitPublicationEvent,
    types::{
        Document, DocumentId, DocumentValue, IndexGenerationSet, ReadSnapshot, StorageSequence,
        TenantId, TenantScope,
    },
};

/// Index publication failures. Crate-private by design.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum IndexError {
    /// The namespace has no registered definition in this catalog.
    NamespaceUnknown,
    /// The namespace was not recreated: the requested epoch is not newer.
    EpochNotNewer,
    /// No publication event has been observed yet.
    NoPublicationObserved,
    /// The generation sequence of one namespace and epoch is exhausted.
    GenerationExhausted,
    /// The Engine's snapshot view returned a typed failure.
    StorageFailure,
    /// An explicit registration named the reserved `Auto/` namespace.
    AutoNamespaceReserved,
    /// A registration carried the wrong index kind for its entry point.
    KindMismatch,
    /// The deterministic auto name is already occupied by different content,
    /// so creation fails closed instead of reusing or overwriting it.
    AutoIdentityMismatch,
    /// The tenant's auto-index registry is at its bound, so a new auto
    /// identity is refused until the retirement sweep frees a slot.
    AutoRegistryFull,
}

/// The immutable delta derived from one commit publication event.
///
/// A delta records the storage sequence the event published and the immutable
/// root that became visible. It is the only input to index building, so index
/// state is always derived from storage publication order, never from a
/// private write path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct IndexDelta {
    covered_sequence: u64,
    root: String,
}

impl IndexDelta {
    /// Derives one delta from an immutable commit publication event.
    pub(crate) fn from_event(event: &CommitPublicationEvent) -> Self {
        Self { covered_sequence: event.sequence().get(), root: event.root().to_string() }
    }

    /// Returns the storage sequence this delta covers.
    pub(crate) const fn covered_sequence(&self) -> u64 {
        self.covered_sequence
    }

    /// Returns the immutable published root identity text.
    #[allow(dead_code, reason = "observed by the index tests; the later wiring diffs roots")]
    pub(crate) fn root(&self) -> &str {
        &self.root
    }
}

/// One published in-memory index segment.
///
/// A segment maps the canonical key of one indexed field tuple to the
/// documents of one tenant that carry it, as observed at the covered storage
/// sequence of the publication that produced the segment. A multi-field
/// definition composes its key from the per-field canonical keys joined with
/// the unit separator; a document that misses any indexed field, or carries
/// a value the subset does not index, has no entry.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct IndexSegment {
    namespace_key: String,
    epoch: u64,
    generation: u64,
    covered_sequence: u64,
    fields: Vec<String>,
    entries: BTreeMap<String, Vec<DocumentId>>,
}

impl IndexSegment {
    /// Returns the catalog key of the segment's namespace.
    #[allow(dead_code, reason = "observed by the index tests")]
    pub(crate) fn namespace_key(&self) -> &str {
        &self.namespace_key
    }

    /// Returns the persisted epoch the segment belongs to.
    pub(crate) const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns the generation of the segment within its epoch.
    pub(crate) const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the storage sequence the segment covers.
    pub(crate) const fn covered_sequence(&self) -> u64 {
        self.covered_sequence
    }

    /// Returns the indexed fields of the segment, in definition order.
    #[allow(dead_code, reason = "observed by the index tests")]
    pub(crate) fn fields(&self) -> &[String] {
        &self.fields
    }

    /// Returns the segment entries in canonical key order.
    #[allow(dead_code, reason = "observed by the index tests")]
    pub(crate) fn entries(&self) -> &BTreeMap<String, Vec<DocumentId>> {
        &self.entries
    }
}

/// The per-epoch publication state of one namespace.
#[derive(Debug, Default)]
struct EpochState {
    next_generation: u64,
    published: Option<IndexSegment>,
    /// (generation, covered sequence) of every publication, in publish order.
    history: Vec<(u64, u64)>,
}

/// The auto-index lifecycle state of one namespace: present only for
/// optimizer-created (auto) definitions. Explicit indexes carry none, which
/// is the structural rule that no automatic transition ever touches them.
#[derive(Debug)]
struct AutoLifecycle {
    state: IndexLifecycleState,
    idle_since: Option<Instant>,
    transitions: VecDeque<IndexTransition>,
}

impl Default for AutoLifecycle {
    fn default() -> Self {
        Self { state: IndexLifecycleState::Active, idle_since: None, transitions: VecDeque::new() }
    }
}

impl AutoLifecycle {
    /// Records one bounded transition history entry, newest last.
    fn record(&mut self, transition: IndexTransition) {
        self.transitions.push_back(transition);
        while self.transitions.len() > MAX_TRANSITIONS_PER_INDEX {
            self.transitions.pop_front();
        }
    }
}

/// The state of one namespace across its epochs.
#[derive(Debug, Default)]
struct NamespaceState {
    fields: Vec<String>,
    definition: Option<IndexDefinition>,
    last_queried: Option<Instant>,
    lifecycle: Option<AutoLifecycle>,
    epochs: BTreeMap<u64, EpochState>,
}

impl NamespaceState {
    /// Returns the newest epoch, or zero when the namespace is unregistered.
    fn current_epoch(&self) -> u64 {
        self.epochs.keys().next_back().copied().unwrap_or(0)
    }

    /// Returns the newest epoch that has published a segment.
    fn published_epoch(&self) -> Option<u64> {
        self.epochs.iter().rev().find_map(|(epoch, state)| state.published.as_ref().map(|_| *epoch))
    }

    /// Returns the covered storage sequence of the newest published segment.
    fn covered_sequence(&self) -> Option<u64> {
        let epoch = self.published_epoch()?;
        self.epochs
            .get(&epoch)
            .and_then(|state| state.published.as_ref().map(IndexSegment::covered_sequence))
    }
}

/// One cached selection of the bounded optimizer plan cache (AX-2, adapted).
#[derive(Debug)]
struct CachedSelection {
    shape: String,
    namespace: String,
    revision: u64,
}

/// One pending auto-index supersession: the replacement's coverage must
/// reach the target's before the drain wait starts, and the target is
/// deleted only after the drain has elapsed (AX-17).
#[derive(Clone, Debug)]
struct PendingSupersede {
    replacement: IndexNamespace,
    target: IndexNamespace,
    target_covered: Option<u64>,
    covered_since: Option<Instant>,
}

/// One retirement tombstone: the identity of a deleted auto index plus its
/// bounded transition history ending with the recorded retirement, kept for
/// a bounded window so retirements stay observable through the typed reads.
#[derive(Clone, Debug)]
pub(crate) struct RetiredRecord {
    tenant: TenantId,
    name: String,
    kind: IndexKind,
    collection: String,
    fields: Vec<crate::query::optimizer::IndexField>,
    transitions: Vec<IndexTransition>,
}

impl RetiredRecord {
    /// Returns the owning tenant.
    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the retired index's catalog name.
    pub(crate) fn name(&self) -> &str {
        &self.name
    }

    /// Returns the retired index's kind.
    pub(crate) const fn kind(&self) -> IndexKind {
        self.kind
    }

    /// Returns the retired index's collection.
    pub(crate) fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the retired index's field list.
    pub(crate) fn fields(&self) -> &[crate::query::optimizer::IndexField] {
        &self.fields
    }

    /// Returns the bounded transition history, oldest first, ending with
    /// the recorded retirement.
    pub(crate) fn transitions(&self) -> &[IndexTransition] {
        &self.transitions
    }
}

/// The optimizer-integration state of the catalog: the kill switch, the
/// per-tenant bounded selection-record stores, the bounded plan cache, the
/// catalog revision that invalidates cached selections when catalog truth
/// changes, and the IX-03 lifecycle state — thresholds, pending
/// supersessions, retirement tombstones, and the surpass pass's revision
/// guard.
///
/// The derived default is the engine default: automatic creation enabled,
/// empty stores, revision zero, and V1's carried lifecycle thresholds.
#[derive(Debug, Default)]
struct OptimizerState {
    auto_creation_disabled: bool,
    records: BTreeMap<String, VecDeque<IndexSelectionRecord>>,
    cache: VecDeque<CachedSelection>,
    revision: u64,
    thresholds: LifecycleThresholds,
    pending: Vec<PendingSupersede>,
    retired: BTreeMap<String, VecDeque<RetiredRecord>>,
    last_surpass_revision: Option<u64>,
}

/// The lifecycle clock: the one time source the lifecycle state machine
/// measures against. Production reads the monotonic instant; the test seam
/// advances a frozen override so thresholds and drains are deterministic.
#[derive(Debug, Default)]
struct LifecycleClock {
    frozen: Mutex<Option<Instant>>,
}

impl LifecycleClock {
    /// Returns the current lifecycle instant.
    fn now(&self) -> Instant {
        self.frozen.lock().expect("lifecycle clock").unwrap_or_else(Instant::now)
    }

    /// Advances the frozen override by the given duration. Test-only: the
    /// in-crate lifecycle tests drive the state machine through it.
    #[cfg(test)]
    fn advance(&self, by: std::time::Duration) {
        let mut frozen = self.frozen.lock().expect("lifecycle clock");
        let now = frozen.unwrap_or_else(Instant::now);
        *frozen = Some(now + by);
    }
}

/// The read-only view of one pending supersession the lifecycle pass acts
/// on.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct SupersedeView {
    replacement: IndexNamespace,
    target: IndexNamespace,
    target_covered: Option<u64>,
}

impl SupersedeView {
    /// Returns the replacing index.
    pub(crate) fn replacement(&self) -> &IndexNamespace {
        &self.replacement
    }

    /// Returns the superseded index.
    pub(crate) fn target(&self) -> &IndexNamespace {
        &self.target
    }
}

/// Reports whether one pending supersession's replacement has reached the
/// target's coverage under the guarded state.
fn parity_of(state: &CatalogState, pending: &SupersedeView) -> bool {
    let replacement_covered = state
        .namespaces
        .get(&pending.replacement.catalog_key())
        .and_then(NamespaceState::covered_sequence);
    match (replacement_covered, pending.target_covered) {
        (Some(replacement), Some(target)) => replacement >= target,
        (Some(_), None) => true,
        (None, _) => false,
    }
}

/// The catalog state guarded by the index lock.
#[derive(Debug, Default)]
struct CatalogState {
    namespaces: BTreeMap<String, NamespaceState>,
    delta: Option<IndexDelta>,
    optimizer: OptimizerState,
}

impl CatalogState {
    /// Returns the number of live auto-index namespaces one tenant owns:
    /// the bounded registry's measure.
    fn auto_count(&self, tenant: &TenantId) -> usize {
        let prefix = format!("{}\u{1f}", tenant.as_str());
        self.namespaces
            .iter()
            .filter(|(key, entry)| {
                key.starts_with(&prefix)
                    && entry
                        .definition
                        .as_ref()
                        .is_some_and(|definition| definition.kind() == IndexKind::Auto)
            })
            .count()
    }
}

/// The private index catalog and its index lock.
///
/// One catalog serves one Engine. Its mutex is the index lock: building a
/// segment happens outside it, and only the compare-and-publish step holds
/// it, so no storage access ever waits for the index lock and the index lock
/// is never held across an Engine call.
pub(crate) struct IndexCatalog {
    state: Mutex<CatalogState>,
    clock: LifecycleClock,
}

impl Default for IndexCatalog {
    fn default() -> Self {
        Self { state: Mutex::new(CatalogState::default()), clock: LifecycleClock::default() }
    }
}

impl IndexCatalog {
    /// Creates an empty catalog.
    pub(crate) fn new() -> Self {
        Self::default()
    }

    /// Returns a publication-event sink that derives deltas from immutable
    /// events and applies them to this catalog.
    ///
    /// The sink only takes the index lock; it never touches storage, so it
    /// can run inside the publication event after storage locks release. It
    /// is registered through the Engine's publication registry at open
    /// (integrator action W4-CA-3), so index maintenance is production-live.
    pub(crate) fn sink(self: &Arc<Self>) -> CommitPublicationSink {
        let catalog = Arc::clone(self);
        Arc::new(move |event: &CommitPublicationEvent| {
            catalog.apply(IndexDelta::from_event(event));
        })
    }

    /// Applies one derived delta, ignoring replays and reorderings.
    ///
    /// Returns whether the delta advanced the covered sequence.
    pub(crate) fn apply(&self, delta: IndexDelta) -> bool {
        let mut state = self.state.lock().expect("index lock");
        match &state.delta {
            Some(current) if delta.covered_sequence() <= current.covered_sequence() => false,
            _ => {
                state.delta = Some(delta);
                true
            }
        }
    }

    /// Returns the latest derived delta, when one was observed.
    #[allow(dead_code, reason = "observed by the index tests and the later wiring")]
    pub(crate) fn delta(&self) -> Option<IndexDelta> {
        self.state.lock().expect("index lock").delta.clone()
    }

    /// Registers a namespace with its indexed field, creating epoch one when
    /// the namespace is new, and returns the current epoch.
    ///
    /// This is the raw single-field registration path: the namespace carries
    /// no index definition, so the optimizer never considers it a candidate.
    #[allow(dead_code, reason = "driven by the index tests and the later wiring")]
    pub(crate) fn ensure_namespace(&self, namespace: &IndexNamespace, field: &str) -> u64 {
        let mut state = self.state.lock().expect("index lock");
        let entry = state.namespaces.entry(namespace.catalog_key()).or_default();
        if entry.epochs.is_empty() {
            entry.epochs.insert(1, EpochState { next_generation: 1, ..EpochState::default() });
            entry.fields = vec![field.to_owned()];
        }
        entry.current_epoch()
    }

    /// Recreates a namespace under a new persisted epoch.
    ///
    /// A new epoch is allowed only when it is strictly newer than every
    /// recorded epoch of the namespace, mirroring the rule that a namespace
    /// is recreated only explicitly under its migration contract. The
    /// generation sequence restarts at one inside the new epoch and is never
    /// compared with the previous epoch's generations. Recreation returns
    /// the namespace to raw form: its single field is replaced and any
    /// registered definition is dropped.
    #[allow(dead_code, reason = "driven by the index tests and the later wiring")]
    pub(crate) fn recreate_namespace(
        &self,
        namespace: &IndexNamespace,
        new_epoch: u64,
        field: &str,
    ) -> Result<(), IndexError> {
        let mut state = self.state.lock().expect("index lock");
        let entry = state
            .namespaces
            .get_mut(&namespace.catalog_key())
            .ok_or(IndexError::NamespaceUnknown)?;
        if new_epoch <= entry.current_epoch() {
            return Err(IndexError::EpochNotNewer);
        }
        entry.epochs.insert(new_epoch, EpochState { next_generation: 1, ..EpochState::default() });
        entry.fields = vec![field.to_owned()];
        entry.definition = None;
        entry.lifecycle = None;
        state.optimizer.revision += 1;
        Ok(())
    }

    /// Registers one explicit index definition under a caller-chosen name.
    ///
    /// The reserved `Auto/` namespace refuses explicit registration (AX-10),
    /// and the definition must be explicit. A re-registration of the same
    /// namespace updates the definition in place — the caller-chosen name is
    /// the identity — and returns the current epoch.
    #[allow(
        dead_code,
        reason = "driven by the optimizer tests; the typed explicit-index operation is a recorded follow-up"
    )]
    pub(crate) fn register_explicit(
        &self,
        namespace: &IndexNamespace,
        definition: &IndexDefinition,
    ) -> Result<u64, IndexError> {
        if namespace.name().starts_with(crate::query::optimizer::AUTO_NAMESPACE_PREFIX) {
            return Err(IndexError::AutoNamespaceReserved);
        }
        if definition.kind() != IndexKind::Explicit {
            return Err(IndexError::KindMismatch);
        }
        let mut state = self.state.lock().expect("index lock");
        let epoch = {
            let entry = state.namespaces.entry(namespace.catalog_key()).or_default();
            if entry.epochs.is_empty() {
                entry.epochs.insert(1, EpochState { next_generation: 1, ..EpochState::default() });
            }
            entry.fields =
                definition.fields().iter().map(|field| field.name().to_owned()).collect();
            entry.definition = Some(definition.clone());
            entry.current_epoch()
        };
        state.optimizer.revision += 1;
        Ok(epoch)
    }

    /// Registers one auto-index definition under its deterministic name.
    ///
    /// The name is derived from the definition, so an existing namespace
    /// under that name is acceptable only when its stored definition is the
    /// same; anything else fails closed as an identity conflict rather than
    /// reusing or overwriting the namespace. A registration that would grow
    /// the tenant's live auto registry past
    /// [`MAX_AUTO_INDEXES_PER_TENANT`](crate::query::optimizer::MAX_AUTO_INDEXES_PER_TENANT)
    /// is refused fail-closed: the registry is bounded engine state, and the
    /// retirement sweep of the same optimizer pass is what frees slots.
    pub(crate) fn register_auto(
        &self,
        tenant: &TenantId,
        definition: &IndexDefinition,
    ) -> Result<AutoRegistration, IndexError> {
        if definition.kind() != IndexKind::Auto {
            return Err(IndexError::KindMismatch);
        }
        let name = auto_index_name(definition);
        let namespace = IndexNamespace::try_new(tenant.clone(), &name)
            .map_err(|_| IndexError::AutoIdentityMismatch)?;
        let mut state = self.state.lock().expect("index lock");
        if !state.namespaces.contains_key(&namespace.catalog_key())
            && state.auto_count(tenant) >= MAX_AUTO_INDEXES_PER_TENANT
        {
            return Err(IndexError::AutoRegistryFull);
        }
        {
            let entry = state.namespaces.entry(namespace.catalog_key()).or_default();
            if entry.epochs.is_empty() {
                entry.epochs.insert(1, EpochState { next_generation: 1, ..EpochState::default() });
                entry.fields =
                    definition.fields().iter().map(|field| field.name().to_owned()).collect();
                entry.definition = Some(definition.clone());
                entry.lifecycle = Some(AutoLifecycle::default());
                state.optimizer.revision += 1;
                return Ok(AutoRegistration { namespace, created: true, epoch: 1 });
            }
            if entry.definition.as_ref() == Some(definition) {
                let epoch = entry.current_epoch();
                return Ok(AutoRegistration { namespace, created: false, epoch });
            }
        }
        Err(IndexError::AutoIdentityMismatch)
    }

    /// Returns every definition-carrying namespace of one tenant, in catalog
    /// order, with its published state, recency for ranking, and lifecycle
    /// facts for the IX-03 state machine.
    pub(crate) fn definitions(&self, tenant: &TenantId) -> Vec<DefinitionEntry> {
        let state = self.state.lock().expect("index lock");
        state
            .namespaces
            .iter()
            .filter_map(|(key, entry)| {
                let definition = entry.definition.as_ref()?;
                let (tenant_key, name) = key.split_once('\u{1f}')?;
                if tenant_key != tenant.as_str() {
                    return None;
                }
                let namespace = IndexNamespace::try_new(tenant.clone(), name).ok()?;
                let published = entry
                    .published_epoch()
                    .and_then(|epoch| entry.epochs.get(&epoch))
                    .and_then(|epoch_state| epoch_state.published.as_ref())
                    .map(|published| {
                        ObservedGeneration::new(published.epoch(), published.generation())
                    });
                let lifecycle = entry.lifecycle.as_ref();
                Some(DefinitionEntry {
                    namespace,
                    definition: definition.clone(),
                    published,
                    covered: entry.covered_sequence(),
                    queried: entry.last_queried.is_some(),
                    last_queried: entry.last_queried,
                    state: lifecycle.map_or(IndexLifecycleState::Active, |state| state.state),
                    idle_since: lifecycle.and_then(|state| state.idle_since),
                    transitions: lifecycle
                        .map(|state| state.transitions.iter().cloned().collect())
                        .unwrap_or_default(),
                })
            })
            .collect()
    }

    /// Marks one namespace queried by a served query (AX-20) — the usage
    /// report that completes an auto index's reporting state — and wakes an
    /// idle auto index back to active (AX-22): synchronous with selection,
    /// recorded as a transition with the measured idle period.
    pub(crate) fn mark_queried(&self, namespace: &IndexNamespace) {
        let now = self.clock.now();
        let mut state = self.state.lock().expect("index lock");
        let threshold = state.optimizer.thresholds.idle_after();
        let Some(entry) = state.namespaces.get_mut(&namespace.catalog_key()) else {
            return;
        };
        entry.last_queried = Some(now);
        let Some(lifecycle) = entry.lifecycle.as_mut() else {
            return;
        };
        if lifecycle.state != IndexLifecycleState::Idle {
            return;
        }
        let measured = lifecycle
            .idle_since
            .map(|since| now.saturating_duration_since(since))
            .unwrap_or_default();
        lifecycle.state = IndexLifecycleState::Active;
        lifecycle.idle_since = None;
        lifecycle.record(IndexTransition::new(
            namespace.name().to_owned(),
            IndexLifecycleState::Idle,
            IndexLifecycleState::Active,
            measured,
            threshold,
        ));
    }

    /// Sets the optimizer's automatic-creation kill switch. Failing closed
    /// (creation disabled) is the switch's active state; the engine default
    /// is enabled (charter PD-2026-08-26-09 D1).
    ///
    /// Test-driven at this seam: the production wiring from engine
    /// configuration is a recorded collision action (the engine module is not
    /// this packet's write scope).
    #[allow(
        dead_code,
        reason = "driven by the optimizer tests; the EngineConfig wiring is a recorded collision action"
    )]
    pub(crate) fn set_auto_index_creation(&self, enabled: bool) {
        self.state.lock().expect("index lock").optimizer.auto_creation_disabled = !enabled;
    }

    /// Reports whether automatic auto-index creation is enabled.
    pub(crate) fn auto_index_creation_enabled(&self) -> bool {
        !self.state.lock().expect("index lock").optimizer.auto_creation_disabled
    }

    /// Records one selection into the tenant's bounded record store.
    ///
    /// The store is scoped per tenant, each with the same
    /// [`MAX_SELECTION_RECORDS`] bound, so one tenant's selection flood can
    /// never evict another tenant's records: the observability of a
    /// tenant's selections belongs to that tenant alone.
    pub(crate) fn record_selection(&self, record: IndexSelectionRecord) {
        let mut state = self.state.lock().expect("index lock");
        let queue = state.optimizer.records.entry(record.tenant().as_str().to_owned()).or_default();
        queue.push_back(record);
        while queue.len() > MAX_SELECTION_RECORDS {
            queue.pop_front();
        }
    }

    /// Returns one tenant's selection records, oldest first.
    pub(crate) fn selection_records(&self, tenant: &TenantId) -> Vec<IndexSelectionRecord> {
        let state = self.state.lock().expect("index lock");
        state
            .optimizer
            .records
            .get(tenant.as_str())
            .map(|queue| queue.iter().cloned().collect())
            .unwrap_or_default()
    }

    /// Returns the cached selection of one query shape when the entry is
    /// still valid for the current catalog revision.
    pub(crate) fn cached_selection(&self, shape: &str) -> Option<String> {
        let mut state = self.state.lock().expect("index lock");
        let revision = state.optimizer.revision;
        if let Some(position) = state
            .optimizer
            .cache
            .iter()
            .position(|entry| entry.shape == shape && entry.revision == revision)
        {
            return Some(state.optimizer.cache[position].namespace.clone());
        }
        // A stale entry never serves again: catalog truth moved.
        state.optimizer.cache.retain(|entry| entry.shape != shape);
        None
    }

    /// Remembers one selection for a query shape at the current catalog
    /// revision, keeping the cache bounded.
    pub(crate) fn remember_selection(&self, shape: &str, namespace: &str) {
        let mut state = self.state.lock().expect("index lock");
        let revision = state.optimizer.revision;
        state.optimizer.cache.retain(|entry| entry.shape != shape);
        state.optimizer.cache.push_back(CachedSelection {
            shape: shape.to_owned(),
            namespace: namespace.to_owned(),
            revision,
        });
        while state.optimizer.cache.len() > MAX_CACHED_SELECTIONS {
            state.optimizer.cache.pop_front();
        }
    }

    // -----------------------------------------------------------------------
    // The IX-03 lifecycle surface (ADR 0010 D-4)
    // -----------------------------------------------------------------------

    /// Returns the current lifecycle instant, the one time source the state
    /// machine measures against.
    pub(crate) fn now(&self) -> Instant {
        self.clock.now()
    }

    /// Advances the lifecycle clock. Test-only: the in-crate lifecycle tests
    /// drive thresholds and drains deterministically through it.
    #[cfg(test)]
    pub(crate) fn advance_lifecycle_clock(&self, by: std::time::Duration) {
        self.clock.advance(by);
    }

    /// Sets the lifecycle thresholds.
    ///
    /// Test-driven at this seam, like the creation kill switch: the
    /// production wiring from engine configuration is a recorded collision
    /// action (the engine module is not this packet's write scope).
    #[allow(
        dead_code,
        reason = "driven by the lifecycle tests; the EngineConfig wiring is a recorded collision action"
    )]
    pub(crate) fn set_lifecycle_thresholds(&self, thresholds: LifecycleThresholds) {
        self.state.lock().expect("index lock").optimizer.thresholds = thresholds;
    }

    /// Returns the lifecycle thresholds.
    pub(crate) fn lifecycle_thresholds(&self) -> LifecycleThresholds {
        self.state.lock().expect("index lock").optimizer.thresholds
    }

    /// Schedules one auto-index supersession: the target is deleted only
    /// after the replacement's coverage reaches the target's and the drain
    /// wait has elapsed (AX-17). Auto indexes are the only legal targets —
    /// an explicit index is never scheduled — and one target carries at most
    /// one pending supersession.
    pub(crate) fn schedule_supersede(&self, replacement: &IndexNamespace, target: &IndexNamespace) {
        if replacement == target {
            return;
        }
        let mut state = self.state.lock().expect("index lock");
        let target_is_auto = state
            .namespaces
            .get(&target.catalog_key())
            .and_then(|entry| entry.definition.as_ref())
            .is_some_and(|definition| definition.kind() == IndexKind::Auto);
        if !target_is_auto
            || state.optimizer.pending.iter().any(|pending| pending.target == *target)
        {
            return;
        }
        let target_covered =
            state.namespaces.get(&target.catalog_key()).and_then(NamespaceState::covered_sequence);
        state.optimizer.pending.push(PendingSupersede {
            replacement: replacement.clone(),
            target: target.clone(),
            target_covered,
            covered_since: None,
        });
    }

    /// Returns every pending supersession, in scheduling order.
    pub(crate) fn pending_supersessions(&self) -> Vec<SupersedeView> {
        let state = self.state.lock().expect("index lock");
        state
            .optimizer
            .pending
            .iter()
            .map(|pending| SupersedeView {
                replacement: pending.replacement.clone(),
                target: pending.target.clone(),
                target_covered: pending.target_covered,
            })
            .collect()
    }

    /// Reports whether one pending supersession's replacement has reached
    /// the target's covered storage sequence (the catalog's coverage-parity
    /// form of V1's etag parity, AX-17).
    pub(crate) fn supersede_at_parity(&self, pending: &SupersedeView) -> bool {
        let state = self.state.lock().expect("index lock");
        parity_of(&state, pending)
    }

    /// Starts the drain clock of one pending supersession when coverage
    /// parity holds and no clock has started yet. Returns whether the drain
    /// started with this call, so the caller never treats a fresh drain as
    /// an elapsed one.
    pub(crate) fn note_supersede_parity(&self, pending: &SupersedeView) -> bool {
        let now = self.clock.now();
        let mut state = self.state.lock().expect("index lock");
        let parity = parity_of(&state, pending);
        let Some(record) =
            state.optimizer.pending.iter_mut().find(|record| record.target == pending.target)
        else {
            return false;
        };
        if record.covered_since.is_some() || !parity {
            return false;
        }
        record.covered_since = Some(now);
        true
    }

    /// Returns the drain time elapsed since coverage parity started, when it
    /// has.
    pub(crate) fn supersede_drain_elapsed(
        &self,
        pending: &SupersedeView,
    ) -> Option<std::time::Duration> {
        let now = self.clock.now();
        let state = self.state.lock().expect("index lock");
        state
            .optimizer
            .pending
            .iter()
            .find(|record| record.target == pending.target)
            .and_then(|record| record.covered_since)
            .map(|since| now.saturating_duration_since(since))
    }

    /// Demotes one auto index to idle: still selectable, demoted in
    /// preference (AX-21). The transition records the measured unqueried
    /// period and the idle threshold. Fails closed without effect on an
    /// explicit index, an unknown namespace, a non-active index, or a
    /// namespace whose usage report is still missing.
    pub(crate) fn demote_to_idle(
        &self,
        namespace: &IndexNamespace,
        measured: std::time::Duration,
        threshold: std::time::Duration,
    ) -> bool {
        let now = self.clock.now();
        let mut state = self.state.lock().expect("index lock");
        let Some(entry) = state.namespaces.get_mut(&namespace.catalog_key()) else {
            return false;
        };
        if entry.last_queried.is_none()
            || !entry
                .definition
                .as_ref()
                .is_some_and(|definition| definition.kind() == IndexKind::Auto)
        {
            return false;
        }
        let Some(lifecycle) = entry.lifecycle.as_mut() else {
            return false;
        };
        if lifecycle.state != IndexLifecycleState::Active {
            return false;
        }
        lifecycle.state = IndexLifecycleState::Idle;
        lifecycle.idle_since = Some(now);
        lifecycle.record(IndexTransition::new(
            namespace.name().to_owned(),
            IndexLifecycleState::Active,
            IndexLifecycleState::Idle,
            measured,
            threshold,
        ));
        true
    }

    /// Retires one auto index: the namespace is removed from the catalog, a
    /// bounded tombstone keeps the recorded transition observable through
    /// the typed reads, and every pending supersession the retirement
    /// closes is dropped. Explicit indexes are refused; returns the recorded
    /// transition.
    pub(crate) fn retire(
        &self,
        namespace: &IndexNamespace,
        measured: std::time::Duration,
        threshold: std::time::Duration,
    ) -> Option<IndexTransition> {
        let mut state = self.state.lock().expect("index lock");
        let entry = state.namespaces.get(&namespace.catalog_key())?;
        let definition = entry.definition.as_ref()?;
        if definition.kind() != IndexKind::Auto {
            return None;
        }
        let from = entry
            .lifecycle
            .as_ref()
            .map_or(IndexLifecycleState::Active, |lifecycle| lifecycle.state);
        let transition = IndexTransition::new(
            namespace.name().to_owned(),
            from,
            IndexLifecycleState::Retired,
            measured,
            threshold,
        );
        // The tombstone keeps the namespace's bounded history ending with
        // the retirement, so the whole observable lifecycle of a deleted
        // auto index survives it.
        let mut transitions = entry
            .lifecycle
            .as_ref()
            .map(|lifecycle| lifecycle.transitions.iter().cloned().collect::<Vec<_>>())
            .unwrap_or_default();
        transitions.push(transition.clone());
        let record = RetiredRecord {
            tenant: namespace.tenant().clone(),
            name: namespace.name().to_owned(),
            kind: definition.kind(),
            collection: definition.collection().as_str().to_owned(),
            fields: definition.fields().to_vec(),
            transitions,
        };
        state.namespaces.remove(&namespace.catalog_key());
        let queue =
            state.optimizer.retired.entry(namespace.tenant().as_str().to_owned()).or_default();
        queue.push_back(record);
        while queue.len() > MAX_RETAINED_RETIRED {
            queue.pop_front();
        }
        state
            .optimizer
            .pending
            .retain(|pending| pending.replacement != *namespace && pending.target != *namespace);
        state.optimizer.revision += 1;
        Some(transition)
    }

    /// Returns the bounded retirement tombstones of one tenant, oldest
    /// first: the typed view reports them so retirements stay observable
    /// after the namespace is gone.
    pub(crate) fn retired_records(&self, tenant: &TenantId) -> Vec<RetiredRecord> {
        let state = self.state.lock().expect("index lock");
        state
            .optimizer
            .retired
            .get(tenant.as_str())
            .map(|queue| queue.iter().cloned().collect())
            .unwrap_or_default()
    }

    /// Returns every tenant that owns at least one definition, in tenant
    /// order: the lifecycle pass iterates the tenants it must serve.
    pub(crate) fn tenants_with_definitions(&self) -> Vec<TenantId> {
        let state = self.state.lock().expect("index lock");
        let mut tenants: Vec<TenantId> = Vec::new();
        for key in state.namespaces.keys() {
            let entry = state.namespaces.get(key);
            if entry.and_then(|entry| entry.definition.as_ref()).is_none() {
                continue;
            }
            let Some((tenant_key, _)) = key.split_once('\u{1f}') else {
                continue;
            };
            if let Ok(tenant) = TenantId::try_new(tenant_key)
                && !tenants.contains(&tenant)
            {
                tenants.push(tenant);
            }
        }
        tenants.sort_by(|left, right| left.as_str().cmp(right.as_str()));
        tenants
    }

    /// Reports whether one namespace still carries a live definition: the
    /// bounded selection retry re-checks its subject before recording.
    pub(crate) fn definition_exists(&self, namespace: &IndexNamespace) -> bool {
        let state = self.state.lock().expect("index lock");
        state
            .namespaces
            .get(&namespace.catalog_key())
            .is_some_and(|entry| entry.definition.is_some())
    }

    /// Reports whether the surpass pass is due: the catalog revision moved
    /// since the pass last ran (V1's database-record etag guard, AX-18).
    pub(crate) fn surpass_pass_due(&self) -> bool {
        let state = self.state.lock().expect("index lock");
        state.optimizer.last_surpass_revision != Some(state.optimizer.revision)
    }

    /// Marks the surpass pass as run against the current catalog revision.
    pub(crate) fn mark_surpass_pass_done(&self) {
        let mut state = self.state.lock().expect("index lock");
        let revision = state.optimizer.revision;
        state.optimizer.last_surpass_revision = Some(revision);
    }

    /// Returns the published segment of a namespace's newest published epoch.
    #[allow(dead_code, reason = "observed by the index tests")]
    pub(crate) fn segment(&self, namespace: &IndexNamespace) -> Option<IndexSegment> {
        let state = self.state.lock().expect("index lock");
        let entry = state.namespaces.get(&namespace.catalog_key())?;
        let epoch = entry.published_epoch()?;
        entry.epochs.get(&epoch).and_then(|epoch_state| epoch_state.published.clone())
    }

    /// Holds the index lock while one test action runs.
    #[cfg(test)]
    pub(crate) fn with_state_lock_held<R>(&self, action: impl FnOnce() -> R) -> R {
        let _guard = self.state.lock().expect("index lock");
        action()
    }

    /// Publishes a privately built segment under the index lock.
    ///
    /// The compare-and-publish rule: a segment whose covered sequence does
    /// not strictly advance the namespace's published coverage is rejected,
    /// so a stale, replayed, or reordered build never lowers the visible
    /// generation.
    fn publish(
        &self,
        namespace: &IndexNamespace,
        mut segment: IndexSegment,
    ) -> Result<RebuildOutcome, IndexError> {
        let mut state = self.state.lock().expect("index lock");
        let entry = state
            .namespaces
            .get_mut(&namespace.catalog_key())
            .ok_or(IndexError::NamespaceUnknown)?;
        let epoch = entry.current_epoch();
        let Some(epoch_state) = entry.epochs.get_mut(&epoch) else {
            return Err(IndexError::NamespaceUnknown);
        };
        let visible = epoch_state
            .published
            .as_ref()
            .map(|published| ObservedGeneration::new(published.epoch(), published.generation()));
        if let Some(published) = &epoch_state.published
            && segment.covered_sequence() <= published.covered_sequence()
        {
            return Ok(RebuildOutcome::RejectedAsStale { visible });
        }
        let generation = epoch_state.next_generation;
        epoch_state.next_generation =
            epoch_state.next_generation.checked_add(1).ok_or(IndexError::GenerationExhausted)?;
        let covered_sequence = segment.covered_sequence();
        segment.epoch = epoch;
        segment.generation = generation;
        epoch_state.history.push((generation, covered_sequence));
        epoch_state.published = Some(segment);
        // A publication changes catalog truth, so cached selections of every
        // shape invalidate with this revision.
        state.optimizer.revision += 1;
        Ok(RebuildOutcome::Published { epoch, generation, covered_sequence })
    }
}

/// The outcome of one auto-index registration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct AutoRegistration {
    namespace: IndexNamespace,
    created: bool,
    epoch: u64,
}

impl AutoRegistration {
    /// Returns the namespace the deterministic name maps to.
    pub(crate) fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Reports whether this registration created the namespace.
    pub(crate) const fn created(&self) -> bool {
        self.created
    }

    /// Returns the namespace's current epoch.
    #[allow(dead_code, reason = "observed by the optimizer tests and the later wiring")]
    pub(crate) const fn epoch(&self) -> u64 {
        self.epoch
    }
}

/// One definition-carrying namespace as the optimizer sees it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct DefinitionEntry {
    namespace: IndexNamespace,
    definition: IndexDefinition,
    published: Option<ObservedGeneration>,
    covered: Option<u64>,
    queried: bool,
    last_queried: Option<Instant>,
    state: IndexLifecycleState,
    idle_since: Option<Instant>,
    transitions: Vec<IndexTransition>,
}

impl DefinitionEntry {
    /// Returns the entry's namespace.
    pub(crate) fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Returns the entry's definition.
    pub(crate) fn definition(&self) -> &IndexDefinition {
        &self.definition
    }

    /// Returns the newest published generation, when one exists.
    pub(crate) const fn published(&self) -> Option<ObservedGeneration> {
        self.published
    }

    /// Returns the covered storage sequence of the newest published
    /// segment — the recency input of ranking — when one exists.
    pub(crate) const fn covered_sequence(&self) -> Option<u64> {
        self.covered
    }

    /// Reports whether any query marked the index queried.
    pub(crate) const fn queried(&self) -> bool {
        self.queried
    }

    /// Returns the instant of the last usage report, when one arrived: the
    /// reporting state is complete exactly when this exists.
    pub(crate) const fn last_queried(&self) -> Option<Instant> {
        self.last_queried
    }

    /// Returns the lifecycle state. Explicit indexes report active and
    /// never transition.
    pub(crate) const fn state(&self) -> IndexLifecycleState {
        self.state
    }

    /// Returns when the index was demoted to idle, when it is idle.
    pub(crate) const fn idle_since(&self) -> Option<Instant> {
        self.idle_since
    }

    /// Returns the bounded transition history, oldest first.
    pub(crate) fn transitions(&self) -> &[IndexTransition] {
        &self.transitions
    }
}

/// The private index generation view over the catalog: the production
/// implementation of the seam queries evaluate staleness against.
impl GenerationView for IndexCatalog {
    fn observed_generation(
        &self,
        namespace: &IndexNamespace,
        at: StorageSequence,
    ) -> Option<ObservedGeneration> {
        let state = self.state.lock().expect("index lock");
        let entry = state.namespaces.get(&namespace.catalog_key())?;
        // The newest publication of any epoch that a snapshot at `at` could
        // have observed: generations from different epochs are returned with
        // their own epoch and never compared as one sequence.
        let sequence = at.get();
        entry
            .epochs
            .iter()
            .flat_map(|(epoch, epoch_state)| {
                epoch_state
                    .history
                    .iter()
                    .filter(|(_, covered)| *covered <= sequence)
                    .map(|(generation, covered)| (*epoch, *generation, *covered))
            })
            .max_by_key(|(_, _, covered)| *covered)
            .map(|(epoch, generation, _)| ObservedGeneration::new(epoch, generation))
    }

    fn available_generation(&self, namespace: &IndexNamespace) -> Option<ObservedGeneration> {
        let state = self.state.lock().expect("index lock");
        let entry = state.namespaces.get(&namespace.catalog_key())?;
        let epoch = entry.published_epoch()?;
        entry.epochs.get(&epoch).and_then(|epoch_state| {
            epoch_state
                .published
                .as_ref()
                .map(|published| ObservedGeneration::new(published.epoch(), published.generation()))
        })
    }
}

/// The outcome of one index build.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RebuildOutcome {
    /// The segment was published at a new monotonic generation.
    Published {
        /// The epoch the generation belongs to.
        epoch: u64,
        /// The generation published.
        generation: u64,
        /// The storage sequence the segment covers.
        covered_sequence: u64,
    },
    /// The build was rejected because it did not advance the published
    /// coverage; the visible generation is unchanged.
    RejectedAsStale {
        /// The unchanged visible generation, when one exists.
        visible: Option<ObservedGeneration>,
    },
}

/// Builds and publishes index segments for one Engine.
///
/// The builder is the only writer of segments. It reads documents through the
/// Engine's validated snapshot view with no lock held, builds the segment
/// privately, and publishes it under the index lock. It never asks Storage to
/// publish a data root and never holds the storage lock while building or
/// publishing.
pub(crate) struct IndexBuilder<'catalog, 'engine> {
    catalog: &'catalog IndexCatalog,
    engine: &'engine Engine,
}

impl<'catalog, 'engine> IndexBuilder<'catalog, 'engine> {
    /// Creates one builder over a catalog and an Engine.
    #[allow(dead_code, reason = "driven by the index tests and the later wiring")]
    pub(crate) fn new(catalog: &'catalog IndexCatalog, engine: &'engine Engine) -> Self {
        Self { catalog, engine }
    }

    /// Rebuilds a namespace from the latest observed publication event.
    #[allow(dead_code, reason = "driven by the index tests and the later wiring")]
    pub(crate) fn rebuild(
        &self,
        namespace: &IndexNamespace,
        engine_identity: u128,
    ) -> Result<RebuildOutcome, IndexError> {
        let delta = self.catalog.delta().ok_or(IndexError::NoPublicationObserved)?;
        self.rebuild_from(namespace, &delta, engine_identity)
    }

    /// Rebuilds a namespace from one explicit delta.
    ///
    /// Taking the delta explicitly keeps a replayed or reordered build
    /// observable: it is exactly the input the compare-and-publish rule
    /// rejects when it does not advance the published coverage.
    #[allow(dead_code, reason = "driven by the index tests and the later wiring")]
    pub(crate) fn rebuild_from(
        &self,
        namespace: &IndexNamespace,
        delta: &IndexDelta,
        engine_identity: u128,
    ) -> Result<RebuildOutcome, IndexError> {
        // Read through the Engine's validated snapshot view. The storage lock
        // is taken and released inside this call; no index lock is held.
        let snapshot = ReadSnapshot::new(
            engine_identity,
            namespace.tenant().clone(),
            StorageSequence::new(delta.covered_sequence()),
            IndexGenerationSet::default(),
        );
        let scope = TenantScope::from_parts(namespace.tenant().clone(), engine_identity);
        let documents = self
            .engine
            .snapshot_documents(&scope, &snapshot)
            .map_err(|_| IndexError::StorageFailure)?;
        // Build the segment privately, with no lock held.
        let fields = {
            let state = self.catalog.state.lock().expect("index lock");
            state
                .namespaces
                .get(&namespace.catalog_key())
                .map(|entry| entry.fields.clone())
                .ok_or(IndexError::NamespaceUnknown)?
        };
        let segment = build_segment(namespace, &fields, delta.covered_sequence(), &documents);
        // Publish under the index lock only.
        self.catalog.publish(namespace, segment)
    }
}

/// Builds one segment from the documents a publication covers.
fn build_segment(
    namespace: &IndexNamespace,
    fields: &[String],
    covered_sequence: u64,
    documents: &[Document],
) -> IndexSegment {
    let mut entries: BTreeMap<String, Vec<DocumentId>> = BTreeMap::new();
    for document in documents {
        let DocumentValue::Object(document_fields) = document.value() else {
            continue;
        };
        let mut keys: Vec<String> = Vec::with_capacity(fields.len());
        for field in fields {
            let Some(value) = document_fields.get(field) else {
                break;
            };
            let Some(key) = value_key(value) else {
                break;
            };
            keys.push(key);
        }
        if keys.len() == fields.len() {
            entries.entry(keys.join("\u{1f}")).or_default().push(document.id().clone());
        }
    }
    IndexSegment {
        namespace_key: namespace.catalog_key(),
        epoch: 0,
        generation: 0,
        covered_sequence,
        fields: fields.to_vec(),
        entries,
    }
}

/// Returns the canonical index key of one scalar value, or `None` for values
/// the W3-2 subset does not index.
fn value_key(value: &crate::types::DocumentValue) -> Option<String> {
    match value {
        crate::types::DocumentValue::Null => Some("null".to_owned()),
        crate::types::DocumentValue::Boolean(value) => Some(format!("boolean:{value}")),
        crate::types::DocumentValue::Integer(value) => Some(format!("integer:{value}")),
        crate::types::DocumentValue::Number(value) => Some(format!("number:{value}")),
        crate::types::DocumentValue::String(value) => Some(format!("string:{value}")),
        crate::types::DocumentValue::Array(_) | crate::types::DocumentValue::Object(_) => None,
    }
}
