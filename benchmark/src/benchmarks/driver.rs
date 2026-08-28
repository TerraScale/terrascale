//! The TerraBaseV2 side of the benchmark lane: an in-process driver that
//! runs the mapped workloads against a durable Engine and returns exact,
//! deterministic counters.
//!
//! This is the SE-07 lane discipline applied to the objective's workloads.
//! The counters are operations, durable bytes appended, and documents
//! returned — facts about an operation stream, reproducible on any machine,
//! not timings. Wall-clock samples are taken only by the commissioning
//! sampler, which exists to prove the gate machinery and renders its
//! outcome as `inconclusive` by construction.
//!
//! The driver lives in-crate and is test-only, exactly like the Engine's
//! own tests: `TenantScope` has no public constructor by design (Platform
//! creates it after authorization), so the in-process lane mirrors
//! Platform's role the way `engine::tests` does.

use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, AtomicUsize, Ordering},
    time::Instant,
};

use crate::{
    engine::{Engine, EngineConfig},
    operations::QueryInput,
    query::{
        OrderDirection, OrderingKey, QueryOrdering, QueryPaging, QueryPredicate, QueryProjection,
        QuerySpec, QueryStore, StalenessMode, StalenessPolicy,
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

use super::{
    lane::{LaneConfig, Workload},
    record::RunRecord,
    vocabulary::{ObjectiveStatus, Verdict},
};

/// The exact, deterministic counters one measured workload window produced.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct WorkloadCounters {
    /// Operations the window completed.
    pub operations: u64,
    /// Durable bytes the window appended to the engine location.
    pub durable_bytes_written: u64,
    /// Documents the window returned to the caller.
    pub documents_returned: u64,
}

/// The outcome of a commissioning run: real wall-clock samples that support
/// no verdict.
#[derive(Clone, Debug)]
pub(crate) struct Commissioning {
    /// Operations per second, one entry per measured round.
    throughput_samples: Vec<f64>,
    /// The honest objective status: `inconclusive`, because no reference ran.
    pub status: ObjectiveStatus,
    /// The honest verdict: `inconclusive`.
    pub verdict: Verdict,
    /// The rendered receipt block.
    pub rendered: String,
}

impl Commissioning {
    /// The measured throughput samples, one per round.
    pub(crate) fn samples(&self) -> &[f64] {
        &self.throughput_samples
    }
}

/// A unique suffix for operation identities and written document ids, so no
/// window collides with another.
static WINDOW: AtomicU64 = AtomicU64::new(0);

/// The in-process TerraBaseV2 side of one lane run.
pub(crate) struct V2Lane {
    engine: Engine,
    dir: PathBuf,
    scope: TenantScope,
    tenant: TenantId,
    seeded: AtomicUsize,
    operations: AtomicU64,
}

impl V2Lane {
    /// Opens a durable engine under a fresh system-temp location.
    pub(crate) fn open(tag: &str) -> Self {
        Self::open_with_lifecycle(tag, crate::query::optimizer::LifecycleThresholds::DEFAULT)
    }

    /// Opens a durable engine with explicit auto-index lifecycle thresholds,
    /// for the workloads whose supersessions must resolve inside the window
    /// (IX-04): a zero supersede drain is V1's documented test mode for that
    /// gate, and the lane states it rather than sleeping it out.
    pub(crate) fn open_with_lifecycle(
        tag: &str,
        thresholds: crate::query::optimizer::LifecycleThresholds,
    ) -> Self {
        let index = WINDOW.fetch_add(1, Ordering::Relaxed);
        let dir = std::env::temp_dir()
            .join(format!("tbv2-se15-bench-{tag}-{}-{index}", std::process::id()));
        std::fs::remove_dir_all(&dir).ok();
        let location = EngineConfig::durable(&dir).expect("durable location");
        let engine =
            Engine::open(location.with_lifecycle_thresholds(thresholds)).expect("engine opens");
        let tenant = TenantId::try_new("benchmark-tenant").expect("valid tenant");
        let scope = TenantScope::from_parts(tenant.clone(), engine.identity());
        Self {
            engine,
            dir,
            scope,
            tenant,
            seeded: AtomicUsize::new(0),
            operations: AtomicU64::new(0),
        }
    }

    /// The lane's engine, for the workload drivers that need the typed reads
    /// beside the counters (IX-04's optimizer workloads).
    pub(crate) fn engine(&self) -> &Engine {
        &self.engine
    }

    /// The lane's tenant scope.
    pub(crate) fn scope(&self) -> &TenantScope {
        &self.scope
    }

    /// The lane's tenant.
    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Seeds `documents` documents shaped like V1's benchmark document: a
    /// name, a group, and a 256-byte payload.
    pub(crate) fn seed(&self, documents: usize) {
        for index in 0..documents {
            self.commit_put(seeded_document(index), ExpectedVersion::Any);
        }
        self.seeded.store(documents, Ordering::Relaxed);
    }

    /// The number of seeded documents the read and query workloads address.
    fn seeded(&self) -> usize {
        self.seeded.load(Ordering::Relaxed)
    }

    /// Runs `operations` warm-up operations for one workload.
    ///
    /// Warm-up operations are real work that is excluded from every
    /// measured window, exactly as V1 excluded them: the measured window
    /// starts from the durable-byte baseline this leaves behind.
    pub(crate) fn warm_up(&self, workload: Workload, operations: usize) -> WorkloadCounters {
        self.run(workload, operations)
    }

    /// Runs one measured window of `operations` operations and returns the
    /// exact counters.
    ///
    /// # Panics
    ///
    /// Panics for a workload with an open adapter slot rather than silently
    /// skipping it: the slot is stated in the receipt, not worked around
    /// here.
    pub(crate) fn run(&self, workload: Workload, operations: usize) -> WorkloadCounters {
        if let Some((reason, owner)) = workload.driver_status() {
            panic!(
                "the {} workload has no in-process driver: {reason} (owner: {owner})",
                workload.as_str()
            );
        }
        let baseline = self.durable_bytes();
        let mut returned = 0_u64;
        for index in 0..operations {
            let selected = workload.select_mixed(index);
            returned += u64::from(self.one_operation(selected, index));
        }
        WorkloadCounters {
            operations: operations as u64,
            durable_bytes_written: self.durable_bytes().saturating_sub(baseline),
            documents_returned: returned,
        }
    }

    /// Executes one operation of a workload, returning whether it returned a
    /// document.
    fn one_operation(&self, workload: Workload, index: usize) -> bool {
        match workload {
            Workload::DocumentRead => {
                self.read_document(&format!("seed-{}", index % self.seeded().max(1))).is_some()
            }
            Workload::DocumentWrite => {
                self.commit_put(measured_document(index), ExpectedVersion::Any);
                false
            }
            Workload::IndexedQuery => {
                let rows = self.query_name(&format!("user-{}", index % self.seeded().max(1)));
                !rows.is_empty()
            }
            _ => unreachable!("select_mixed only yields the three driven workloads"),
        }
    }

    /// The commissioning sampler: warm-up, then `samples` measured rounds of
    /// `operations_per_sample`, each timed.
    ///
    /// The samples prove the gate machinery on real work; they are not
    /// evidence about the objective, and the returned record says so by
    /// construction.
    pub(crate) fn commission(
        &self,
        workload: Workload,
        samples: usize,
        operations_per_sample: usize,
    ) -> Commissioning {
        let config = LaneConfig::COMMISSIONING_TRIAL;
        self.warm_up(workload, config.warmup_operations);
        let mut throughput_samples = Vec::with_capacity(samples);
        for _ in 0..samples {
            let started = Instant::now();
            self.run(workload, operations_per_sample);
            let elapsed = started.elapsed().as_secs_f64();
            throughput_samples.push(
                f64::from(u32::try_from(operations_per_sample).unwrap_or(u32::MAX))
                    / elapsed.max(f64::EPSILON),
            );
        }
        let record = RunRecord::commissioning(workload);
        Commissioning {
            throughput_samples,
            status: record.status,
            verdict: record.verdict,
            rendered: record.render(),
        }
    }

    /// One point read through a fresh snapshot and read transaction.
    fn read_document(&self, id: &str) -> Option<Document> {
        let snapshot = self.engine.read_snapshot(&self.scope).expect("snapshot");
        let transaction =
            self.engine.begin(&self.scope, snapshot, self.operation("bench-read")).expect("begins");
        transaction.read(&document_id(id)).expect("reads")
    }

    /// Commits one document put at the engine's acknowledged durability
    /// level, mirroring the PostgreSQL side's `synchronous_commit = on`.
    pub(crate) fn commit_put(&self, document: Document, expected: ExpectedVersion) {
        let snapshot = self.engine.read_snapshot(&self.scope).expect("snapshot");
        let mut transaction = self
            .engine
            .begin(&self.scope, snapshot, self.operation("bench-write"))
            .expect("begins");
        transaction.put(document, expected).expect("stages");
        match transaction.commit(LocalDurability::Synced) {
            crate::engine::CommitOutcome::Committed(_) => {}
            other => panic!("expected a committed receipt, got {other:?}"),
        }
    }

    /// Executes the mapped indexed equality query and returns its rows.
    fn query_name(&self, name: &str) -> Vec<crate::query::QueryRow> {
        let snapshot = self.engine.read_snapshot(&self.scope).expect("snapshot");
        let store: QueryStore<'_> = self.engine.queries();
        let plan = store.compile(QueryInput::typed(self.query_spec(name))).expect("compiles");
        store.execute(&plan, &snapshot).expect("executes").rows().to_vec()
    }

    /// Builds the mapped equality query: `name = $name`, projected to the
    /// name field, first row only — the same shape as V1's benchmark query
    /// and the PostgreSQL `SELECT id, name ... LIMIT 1`.
    fn query_spec(&self, name: &str) -> QuerySpec {
        QuerySpec::try_new(
            self.tenant.clone(),
            crate::query::QuerySource::try_new(
                CollectionName::try_new("users").expect("valid identifier"),
            )
            .expect("user collection"),
            QueryPredicate::all(vec![crate::query::QueryCondition::Equals {
                field: "name".to_owned(),
                value: DocumentValue::String(name.to_owned()),
            }])
            .expect("valid conjunction"),
            QueryOrdering::try_new(vec![
                OrderingKey::try_new("name", OrderDirection::Ascending).expect("valid key"),
            ])
            .expect("valid ordering"),
            QueryProjection::fields(&["name"]).expect("valid projection"),
            QueryPaging::try_new(0, Some(1)).expect("valid paging"),
            StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
            Deadline::after(std::time::Duration::from_secs(30)).expect("valid deadline"),
            Cancellation::new(),
        )
        .expect("valid specification")
    }

    /// A fresh operation identity.
    fn operation(&self, name: &str) -> OperationId {
        OperationId::try_new(&format!("{name}-{}", self.next_operation_tag()))
            .expect("valid identifier")
    }

    /// The next unique operation suffix.
    fn next_operation_tag(&self) -> u64 {
        self.operations.fetch_add(1, Ordering::Relaxed)
    }

    /// The total size of every file under the engine's durable location.
    ///
    /// This is the deterministic write counter: the same operation stream
    /// appends the same bytes, on any machine, at any time.
    pub(crate) fn durable_bytes(&self) -> u64 {
        directory_bytes(&self.dir)
    }

    /// Shuts the engine down and removes its location.
    pub(crate) fn remove(self) {
        self.engine.shutdown().ok();
        std::fs::remove_dir_all(&self.dir).ok();
    }
}

/// Sums the length of every regular file under `root`, recursively.
fn directory_bytes(root: &Path) -> u64 {
    let Ok(entries) = std::fs::read_dir(root) else {
        return 0;
    };
    let mut total = 0;
    for entry in entries.flatten() {
        let Ok(file_type) = entry.file_type() else { continue };
        if file_type.is_dir() {
            total += directory_bytes(&entry.path());
        } else if file_type.is_file() {
            total += entry.metadata().map_or(0, |meta| meta.len());
        }
    }
    total
}

/// A validated document identifier.
fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

/// The seeded document shaped like V1's benchmark document.
fn seeded_document(index: usize) -> Document {
    document(
        &format!("seed-{index}"),
        BTreeMap::from([
            ("name".to_owned(), DocumentValue::String(format!("user-{index}"))),
            ("group".to_owned(), DocumentValue::Integer(i64::try_from(index % 100).unwrap_or(0))),
            ("payload".to_owned(), DocumentValue::String("x".repeat(256))),
        ]),
    )
}

/// The written document shaped like V1's measured write.
fn measured_document(index: usize) -> Document {
    document(
        &format!("measured-{index}"),
        BTreeMap::from([
            ("name".to_owned(), DocumentValue::String("measured".to_owned())),
            ("group".to_owned(), DocumentValue::Integer(42)),
            ("payload".to_owned(), DocumentValue::String("x".repeat(256))),
        ]),
    )
}

/// Wraps fields into a document of the benchmark collection.
fn document(id: &str, fields: BTreeMap<String, DocumentValue>) -> Document {
    Document::new(
        document_id(id),
        CollectionName::try_new("users").expect("valid identifier"),
        DocumentValue::Object(fields),
    )
}
