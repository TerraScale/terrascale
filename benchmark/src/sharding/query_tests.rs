//! SH-04 contract tests: sharded query fan-out and reduction (PB-4036) and
//! sharded index definition and readers (PB-4060).
//!
//! Scope proved here: routing a query to the shards owning the relevant
//! bucket ranges (every seated shard by default, one shard when the predicate
//! equals every Shard Key field), per-shard execution through the existing
//! compile/execute query seam, merge and reduction with the seam's own
//! ordering and paging, result equivalence against a single-shard execution
//! over identical data (property rounds), paging and order under deliberately
//! unequal per-shard volumes, composed staleness that never relaxes below the
//! strictest shard's basis, sharded index definition registration and reads,
//! and TQL composition (a TQL query lowers to the specification the fan-out
//! executes, PD-2026-08-26-08).
//!
//! Non-claims: no failover qualification (SH-05), no cluster transaction, and
//! no performance fact appears in any test name or assertion — equivalence,
//! order, and staleness vocabulary only. The fixtures seat one Engine per
//! owning Member, which is the in-process shape of the serving endpoint; the
//! Platform owns which Member serves which shard in a deployment.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};
use std::time::Duration;

use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::index::{IndexBuilder, RebuildOutcome};
use crate::operations::QueryInput;
use crate::query::optimizer::{
    AUTO_NAMESPACE_PREFIX, IndexCapabilities, IndexDefinition, IndexField, IndexKind,
};
use crate::query::{
    IndexGenerationRef, IndexNamespace, MAX_PAGE_LIMIT, ObservedGeneration, OrderDirection,
    OrderingKey, QueryCondition, QueryError, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QueryResult, QueryRow, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
    tql,
};
use crate::replication::{MemberId, TopologyView};
use crate::sharding::{
    BUCKET_SPACE, BucketRange, BucketRangeTable, CollectionShardKey, MemberServing, RouteReason,
    RoutingRequest, ShardId, ShardIndexState, ShardKey, ShardKeyKind, ShardKeySet,
    ShardQueryOutcome, ShardServingError, ShardSnapshots, ShardStalenessObservation, ShardTopology,
    ShardedExecutionError, ShardedIndexDefinition, ShardedIndexRefusal, ShardedQueries,
    ShardedQueryResult,
};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, ReadSnapshot, TenantId, TenantScope,
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, AtomicOrdering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-sh04-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (Engine, PathBuf) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = EngineConfig::durable(&dir).expect("durable location");
    let engine = Engine::open(config).expect("engine opens");
    (engine, dir)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection")
}

fn scope_of(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

/// One `orders` document: the collection every fixture query reads.
fn order_document(id: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identity"),
        collection("orders"),
        object(fields),
    )
}

fn commit_documents(
    engine: &Engine,
    scope: &TenantScope,
    tag: &str,
    documents: Vec<Document>,
) -> ReadSnapshot {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let mut transaction = engine
        .begin(scope, snapshot.clone(), OperationId::try_new(tag).expect("valid operation"))
        .expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    assert!(
        matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
        "fixture commit succeeds"
    );
    snapshot
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn snapshot_of(engine: &Engine, name: &str) -> ReadSnapshot {
    engine.read_snapshot(&scope_of(engine, name)).expect("snapshot")
}

fn member(number: u32) -> MemberId {
    MemberId::try_new(&format!("m-{number}")).expect("valid member")
}

/// The engine-table index of one shard number.
fn shard_index(number: u32) -> usize {
    usize::try_from(number).expect("a table seats at most 128 shards")
}

/// One Region and one Member per shard, each Member owning `shard/<number>`
/// at `epoch`: the membership pair the fan-out fixtures seat tables against.
fn view_for(shards: u32, epoch: u64) -> TopologyView {
    let mut view = TopologyView::new();
    for number in 0..shards {
        let region = format!("r-{number}");
        view = view.with_region(&region).expect("valid region");
        view = view.with_member(member(number).as_str(), &region, &region).expect("valid member");
    }
    for number in 0..shards {
        view = view
            .with_owner_at_epoch(
                &ShardId::from_number(number).as_range(),
                member(number).as_str(),
                epoch,
            )
            .expect("valid owner");
    }
    view.validated().expect("servable view")
}

/// The table whose interior boundaries are `boundaries`: shard `i` owns
/// `[boundaries[i - 1], boundaries[i])`, shard 0 starts at bucket zero, and
/// the last shard ends at the bucket-space end.
fn table_over(boundaries: &[u32]) -> BucketRangeTable {
    let count = u32::try_from(boundaries.len()).expect("a table seats at most 128 shards");
    let mut entries = Vec::new();
    let mut start = 0_u32;
    for (index, boundary) in boundaries.iter().enumerate() {
        entries.push(
            BucketRange::try_new(
                start,
                *boundary,
                ShardId::from_number(
                    u32::try_from(index).expect("a table seats at most 128 shards"),
                ),
            )
            .expect("range"),
        );
        start = *boundary;
    }
    entries.push(
        BucketRange::try_new(start, BUCKET_SPACE, ShardId::from_number(count)).expect("range"),
    );
    BucketRangeTable::try_new(&entries).expect("table partitions the space")
}

fn topology_over(boundaries: &[u32]) -> ShardTopology {
    ShardTopology::try_new(
        table_over(boundaries),
        view_for(u32::try_from(boundaries.len()).expect("a table seats at most 128 shards") + 1, 1),
    )
    .expect("valid shard topology")
}

/// The one-shard (degenerate, ADR 0011 D6) topology over the whole space.
fn single_shard_topology() -> ShardTopology {
    ShardTopology::try_new(BucketRangeTable::single_shard(ShardId::from_number(0)), view_for(1, 1))
        .expect("valid shard topology")
}

/// The topology of the pin fixtures: `orders` routes by one field, `tier`.
fn tier_keyed_topology(boundaries: &[u32]) -> ShardTopology {
    let declaration =
        CollectionShardKey::try_new("orders", ShardKeyKind::try_fields(&["tier"]).expect("fields"))
            .expect("valid declaration");
    topology_over(boundaries)
        .with_shard_keys(ShardKeySet::try_new(&[declaration]).expect("distinct declarations"))
}

fn spec(
    predicate: QueryPredicate,
    ordering: QueryOrdering,
    projection: QueryProjection,
    paging: QueryPaging,
    staleness: StalenessPolicy,
) -> QuerySpec {
    QuerySpec::try_new(
        tenant("tenant-a"),
        QuerySource::try_new(collection("orders")).expect("user collection"),
        predicate,
        ordering,
        projection,
        paging,
        staleness,
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn index_free_staleness() -> StalenessPolicy {
    StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy")
}

fn namespace(name: &str) -> IndexNamespace {
    IndexNamespace::try_new(tenant("tenant-a"), name).expect("valid namespace")
}

/// The identity-keyed buckets of one corpus, sorted and deduplicated: the
/// boundary material the fan-out fixtures split the bucket space with.
fn sorted_buckets(documents: &[Document]) -> Vec<u32> {
    let mut buckets: Vec<u32> =
        documents.iter().map(|document| ShardKey::identity(document.id()).bucket().get()).collect();
    buckets.sort_unstable();
    buckets.dedup();
    buckets
}

/// Boundaries that split `documents` across `shards` shards as evenly as the
/// corpus's own buckets allow.
fn even_boundaries(documents: &[Document], shards: u32) -> Vec<u32> {
    let buckets = sorted_buckets(documents);
    let mut boundaries = Vec::new();
    for index in 1..shards {
        let position = buckets.len() * shard_index(index) / shard_index(shards);
        boundaries.push(buckets[position].max(1));
    }
    boundaries.sort_unstable();
    boundaries.dedup();
    boundaries
}

/// Boundaries that split `documents` by the given shares of the corpus: the
/// deliberately unequal per-shard volumes of the skew fixtures.
fn skewed_boundaries(documents: &[Document], shares: &[u64]) -> Vec<u32> {
    let buckets = sorted_buckets(documents);
    let total: u64 = shares.iter().sum();
    assert_eq!(total, 100, "shares partition the corpus");
    let last = buckets.len() - 1;
    let mut boundaries = Vec::new();
    let mut consumed = 0_u64;
    for share in &shares[..shares.len() - 1] {
        consumed += *share;
        let position =
            usize::try_from(u64::try_from(buckets.len()).expect("corpus fits") * consumed / total)
                .expect("a share of the corpus fits");
        boundaries.push(buckets[position.min(last)].max(1));
    }
    boundaries.sort_unstable();
    boundaries.dedup();
    boundaries
}

/// One fanned-out cluster: the engines of a sharded topology, each holding
/// exactly the documents its shard owns.
struct Cluster {
    topology: ShardTopology,
    engines: Vec<Engine>,
    directories: Vec<PathBuf>,
    snapshots: Vec<ReadSnapshot>,
}

impl Cluster {
    /// Builds a cluster over `documents` seated at the identity-keyed table
    /// `boundaries` names.
    fn over_boundaries(tag: &str, documents: &[Document], boundaries: &[u32]) -> Self {
        Self::build(tag, documents, topology_over(boundaries))
    }

    /// Builds a cluster over `documents` routed by exactly the topology the
    /// cluster's queries fan out over: each shard's engine holds exactly the
    /// documents that topology routes to it.
    fn build(tag: &str, documents: &[Document], topology: ShardTopology) -> Self {
        let shards = topology.table().shards();
        let mut routed: Vec<Vec<Document>> = vec![Vec::new(); shards.len()];
        for document in documents {
            let placement = topology
                .route(&RoutingRequest::of_document(&tenant("tenant-a"), document))
                .expect("routes");
            routed[shard_index(placement.shard().number())].push(document.clone());
        }
        let mut engines = Vec::new();
        let mut directories = Vec::new();
        for (index, owned) in routed.into_iter().enumerate() {
            let (engine, dir) = open_engine(&format!("{tag}-shard-{index}"));
            if !owned.is_empty() {
                let scope = scope_of(&engine, "tenant-a");
                commit_documents(&engine, &scope, &format!("op-{index}"), owned);
            }
            engines.push(engine);
            directories.push(dir);
        }
        let snapshots = Self::snapshots_of(&engines);
        Self { topology, engines, directories, snapshots }
    }

    /// Takes a fresh snapshot of every engine: the fan-out must observe the
    /// fixture's latest writes, including the index fixtures' commits.
    fn refresh_snapshots(&mut self) {
        self.snapshots = Self::snapshots_of(&self.engines);
    }

    fn snapshots_of(engines: &[Engine]) -> Vec<ReadSnapshot> {
        engines.iter().map(|engine| snapshot_of(engine, "tenant-a")).collect()
    }

    /// The member-seated serving set of this cluster.
    fn serving(&self) -> MemberServing<'_> {
        let endpoints: Vec<(MemberId, &Engine)> = self
            .engines
            .iter()
            .enumerate()
            .map(|(index, engine)| {
                (member(u32::try_from(index).expect("a cluster seats at most 128 shards")), engine)
            })
            .collect();
        MemberServing::try_new(endpoints).expect("serving set seats")
    }

    /// The per-shard snapshots of this cluster, in shard-number order.
    fn snapshots(&self) -> ShardSnapshots {
        let pairs: Vec<(ShardId, ReadSnapshot)> = self
            .snapshots
            .iter()
            .enumerate()
            .map(|(index, snapshot)| {
                (
                    ShardId::from_number(u32::try_from(index).expect("at most 128 shards")),
                    snapshot.clone(),
                )
            })
            .collect();
        ShardSnapshots::try_new(pairs).expect("distinct shard snapshots")
    }
}

/// The single-shard engine over the same documents: the equivalence
/// reference for every fan-out assertion.
struct Ground {
    engine: Engine,
    directory: PathBuf,
    snapshot: ReadSnapshot,
}

impl Ground {
    fn build(tag: &str, documents: &[Document]) -> Self {
        let (engine, directory) = open_engine(&format!("{tag}-ground"));
        if !documents.is_empty() {
            let scope = scope_of(&engine, "tenant-a");
            commit_documents(&engine, &scope, "op-ground", documents.to_vec());
        }
        let snapshot = snapshot_of(&engine, "tenant-a");
        Self { engine, directory, snapshot }
    }

    /// The direct seam execution of one specification.
    fn execute(&self, spec: &QuerySpec) -> QueryResult {
        let plan =
            self.engine.queries().compile(QueryInput::typed(spec.clone())).expect("compiles");
        self.engine.queries().execute(&plan, &self.snapshot).expect("executes")
    }

    fn clean(self) {
        drop(self.engine);
        std::fs::remove_dir_all(&self.directory).ok();
    }
}

fn row_ids(rows: &[QueryRow]) -> Vec<String> {
    rows.iter().map(|row| row.document().id().as_str().to_owned()).collect()
}

fn row_projections(rows: &[QueryRow]) -> Vec<Vec<(String, DocumentValue)>> {
    rows.iter().map(|row| row.projected().to_vec()).collect()
}

fn clean_up(cluster: Cluster, directories: Vec<PathBuf>) {
    drop(cluster);
    for directory in directories {
        std::fs::remove_dir_all(&directory).ok();
    }
}

/// A deterministic, dependency-free seed sequence: the property rounds must
/// be reproducible from the recorded seed alone.
struct Seed(u64);

impl Seed {
    fn next(&mut self) -> u64 {
        let mut value = self.0;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        self.0 = value;
        value.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn below(&mut self, bound: u64) -> u64 {
        self.next() % bound.max(1)
    }

    fn small(&mut self, bound: usize) -> usize {
        usize::try_from(self.below(u64::try_from(bound).expect("bound fits")))
            .expect("seed value fits")
    }

    fn flag(&mut self) -> bool {
        self.below(2) == 0
    }
}

/// The fixed corpus of the equivalence fixtures: tiers tie, labels tie, and
/// one document in three carries no note, so orderings meet equal keys and
/// missing fields on every round.
fn fixed_corpus(count: u64) -> Vec<Document> {
    (0..count)
        .map(|index| {
            let tier = DocumentValue::Integer(i64::try_from(index % 4).expect("small tier"));
            let label = DocumentValue::String(format!("label-{}", index % 5));
            let note = if index % 3 == 0 {
                Vec::new()
            } else {
                vec![("note", DocumentValue::String(format!("note-{}", index % 2)))]
            };
            let mut fields = vec![("tier", tier), ("label", label)];
            fields.extend(note);
            order_document(&format!("orders/{index:03}"), &fields)
        })
        .collect()
}

fn generated_corpus(seed: &mut Seed, count: usize) -> Vec<Document> {
    (0..count)
        .map(|index| {
            let tier = i64::try_from(seed.below(4)).expect("small tier");
            let label = format!("label-{}", seed.below(3));
            let mut fields = vec![
                ("tier", DocumentValue::Integer(tier)),
                ("label", DocumentValue::String(label)),
            ];
            if seed.flag() {
                fields.push(("note", DocumentValue::String(format!("note-{}", seed.below(2)))));
            }
            order_document(&format!("orders/{index:03}"), &fields)
        })
        .collect()
}

/// One generated specification: a bounded conjunction, a mixed-direction
/// ordering over distinct fields, a projection, and page bounds.
fn generated_spec(seed: &mut Seed, offset: u64, limit: Option<u32>) -> QuerySpec {
    let mut conditions = Vec::new();
    if seed.flag() {
        conditions.push(QueryCondition::Equals {
            field: "tier".to_owned(),
            value: DocumentValue::Integer(i64::try_from(seed.below(4)).expect("small tier")),
        });
    }
    if seed.flag() {
        conditions.push(QueryCondition::GreaterOrEqual {
            field: "tier".to_owned(),
            value: DocumentValue::Integer(0),
        });
    }
    if seed.flag() {
        conditions.push(QueryCondition::Equals {
            field: "label".to_owned(),
            value: DocumentValue::String(format!("label-{}", seed.below(3))),
        });
    }
    if seed.flag() {
        conditions.push(QueryCondition::Missing { field: "note".to_owned() });
    }
    let mut keys = Vec::new();
    if seed.flag() {
        keys.push(OrderingKey::try_new("tier", OrderDirection::Ascending).expect("valid key"));
    }
    if seed.flag() {
        keys.push(
            OrderingKey::try_new(
                "label",
                if seed.flag() { OrderDirection::Descending } else { OrderDirection::Ascending },
            )
            .expect("valid key"),
        );
    }
    if seed.flag() {
        keys.push(OrderingKey::try_new("note", OrderDirection::Ascending).expect("valid key"));
    }
    spec(
        QueryPredicate::all(conditions).expect("valid conjunction"),
        QueryOrdering::try_new(keys).expect("valid ordering"),
        if seed.flag() {
            QueryProjection::whole_document()
        } else {
            QueryProjection::fields(&["tier", "label"]).expect("valid projection")
        },
        QueryPaging::try_new(offset, limit).expect("valid paging"),
        index_free_staleness(),
    )
}

/// Asserts one fan-out result is row-for-row the direct single-shard answer.
fn assert_matches_ground(round: &str, result: &ShardedQueryResult, ground: &QueryResult) {
    assert_eq!(result.matched(), ground.matched(), "matched before paging differs on {round}");
    assert_eq!(row_ids(result.rows()), row_ids(ground.rows()), "row order differs on {round}");
    assert_eq!(
        row_projections(result.rows()),
        row_projections(ground.rows()),
        "projected fields differ on {round}"
    );
    assert_eq!(result.ordering(), ground.ordering(), "ordering differs on {round}");
    assert_eq!(result.rows(), ground.rows(), "whole rows differ on {round}");
}

fn whole_document_query(paging: QueryPaging, ordering: QueryOrdering) -> QuerySpec {
    spec(
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        ordering,
        QueryProjection::whole_document(),
        paging,
        index_free_staleness(),
    )
}

// ---------------------------------------------------------------------------
// Routing
// ---------------------------------------------------------------------------

#[test]
fn routing_fans_out_to_every_seated_shard_by_default() {
    let documents = fixed_corpus(12);
    let topology = topology_over(&even_boundaries(&documents, 3));
    let executor = ShardedQueries::new(topology.clone());
    let routing = executor.route(&generated_spec(&mut Seed(7), 0, None)).expect("routes");

    assert_eq!(routing.routes().len(), 3, "an unpinned query reaches every seated shard");
    assert_eq!(routing.pinned_bucket(), None, "no bucket is pinned without a Shard Key pin");
    for route in routing.routes() {
        assert_eq!(route.reason(), &RouteReason::FanOut);
        assert_eq!(topology.owner_of(route.shard()), Some(route.owner().clone()));
        assert_eq!(topology.epoch_of(route.shard()), Some(route.epoch()));
    }
}

#[test]
fn routing_pins_one_shard_when_the_predicate_equals_every_shard_key_field() {
    let documents = fixed_corpus(12);
    let topology = tier_keyed_topology(&even_boundaries(&documents, 2));
    let executor = ShardedQueries::new(topology.clone());

    let query = spec(
        QueryPredicate::all(vec![
            QueryCondition::Equals { field: "tier".to_owned(), value: DocumentValue::Integer(2) },
            QueryCondition::GreaterOrEqual {
                field: "label".to_owned(),
                value: DocumentValue::String("a".to_owned()),
            },
        ])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
    );
    let routing = executor.route(&query).expect("routes");
    assert_eq!(routing.routes().len(), 1, "the every-field equality pins one shard");
    let RouteReason::ShardKeyPinned { bucket } = routing.routes()[0].reason() else {
        panic!("an every-field equality predicate pins the Shard Key");
    };
    // The pinned bucket is the bucket of the pinned key itself — the same
    // rendering a document with `tier = 2` routes by — and it maps to exactly
    // the one shard the routing names.
    assert_eq!(*bucket, ShardKey::composite(&["i:2".to_owned()]).bucket());
    assert_eq!(routing.routes()[0].shard(), topology.table().shard_of(*bucket));
    assert_eq!(routing.pinned_bucket(), Some(*bucket));
}

#[test]
fn routing_does_not_pin_on_partial_shard_key_equality() {
    let documents = fixed_corpus(12);
    let declaration = CollectionShardKey::try_new(
        "orders",
        ShardKeyKind::try_fields(&["tier", "label"]).expect("fields"),
    )
    .expect("valid declaration");
    let topology = topology_over(&even_boundaries(&documents, 2))
        .with_shard_keys(ShardKeySet::try_new(&[declaration]).expect("distinct declarations"));
    let executor = ShardedQueries::new(topology);

    let partial = spec(
        QueryPredicate::all(vec![
            QueryCondition::Equals { field: "tier".to_owned(), value: DocumentValue::Integer(1) },
            QueryCondition::GreaterThan {
                field: "label".to_owned(),
                value: DocumentValue::String("b".to_owned()),
            },
        ])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
    );
    let routing = executor.route(&partial).expect("routes");
    assert_eq!(routing.routes().len(), 2, "an unequality'd field cannot pin a bucket");
    assert!(routing.routes().iter().all(|route| route.reason() == &RouteReason::FanOut));

    let non_equality = spec(
        QueryPredicate::all(vec![QueryCondition::NotEquals {
            field: "tier".to_owned(),
            value: DocumentValue::Integer(1),
        }])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
    );
    let routing = executor.route(&non_equality).expect("routes");
    assert_eq!(routing.routes().len(), 2, "a non-equality condition never pins a bucket");
    assert_eq!(routing.pinned_bucket(), None);
}

#[test]
fn routing_never_pins_on_unrenderable_shard_key_literals() {
    let documents = fixed_corpus(12);
    let topology = tier_keyed_topology(&even_boundaries(&documents, 2));
    let executor = ShardedQueries::new(topology);

    let unrenderable = spec(
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "tier".to_owned(),
            value: DocumentValue::Array(Vec::new()),
        }])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
    );
    let routing = executor.route(&unrenderable).expect("routes");
    assert_eq!(
        routing.routes().len(),
        2,
        "a literal the Shard Key cannot render must fan out, never prune to a wrong shard"
    );
    assert_eq!(routing.pinned_bucket(), None);
}

#[test]
fn routing_fans_out_for_identity_keyed_collections_under_predicates() {
    let documents = fixed_corpus(12);
    let topology = topology_over(&even_boundaries(&documents, 2));
    let executor = ShardedQueries::new(topology);
    let by_other_field = spec(
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "tier".to_owned(),
            value: DocumentValue::Integer(3),
        }])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
    );
    // The collection is identity-keyed by default: `tier` is an ordinary
    // field, not the Shard Key, so the query still reaches every shard.
    let routing = executor.route(&by_other_field).expect("routes");
    assert_eq!(routing.routes().len(), 2);
    assert_eq!(routing.pinned_bucket(), None);
}

#[test]
fn a_pinned_query_executes_against_one_shard_and_matches_the_fan_out() {
    let documents = fixed_corpus(20);
    let boundaries = even_boundaries(&documents, 2);
    let topology = tier_keyed_topology(&boundaries);
    let ground = Ground::build("pin-execute", &documents);
    let cluster = Cluster::build("pin-execute", &documents, topology.clone());
    let executor = ShardedQueries::new(topology.clone());
    let serving = cluster.serving();
    let full_snapshots = cluster.snapshots();

    let query = spec(
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "tier".to_owned(),
            value: DocumentValue::Integer(2),
        }])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
    );

    // Only the pinned shard's snapshot is supplied: the pin means the other
    // shards are not reached at all.
    let pinned_shard = executor.route(&query).expect("routes").routes()[0].shard();
    let pinned_snapshot = cluster.snapshots[shard_index(pinned_shard.number())].clone();
    let pinned_snapshots =
        ShardSnapshots::try_new(vec![(pinned_shard, pinned_snapshot)]).expect("distinct snapshots");
    let pinned =
        executor.execute(&serving, &query, &pinned_snapshots).expect("the pinned fan-out executes");
    let full =
        executor.execute(&serving, &query, &full_snapshots).expect("the full fan-out executes");
    let direct = ground.execute(&query);
    assert_matches_ground("pinned", &pinned, &direct);
    assert_matches_ground("full", &full, &direct);
    assert_eq!(pinned.per_shard().len(), 1, "only the pinned shard was reached");

    ground.clean();
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

// ---------------------------------------------------------------------------
// Result equivalence
// ---------------------------------------------------------------------------

#[test]
fn fan_out_over_the_degenerate_one_shard_topology_matches_direct_execution() {
    let documents = fixed_corpus(18);
    let ground = Ground::build("one-shard", &documents);
    let executor = ShardedQueries::new(single_shard_topology());
    let endpoints: Vec<(MemberId, &Engine)> = vec![(member(0), &ground.engine)];
    let serving = MemberServing::try_new(endpoints).expect("serving set seats");
    let snapshots =
        ShardSnapshots::try_new(vec![(ShardId::from_number(0), ground.snapshot.clone())])
            .expect("distinct snapshots");
    let ordering = QueryOrdering::try_new(vec![
        OrderingKey::try_new("tier", OrderDirection::Ascending).expect("valid key"),
        OrderingKey::try_new("label", OrderDirection::Descending).expect("valid key"),
    ])
    .expect("valid ordering");

    for (offset, limit) in [(0, None), (0, Some(3)), (4, Some(6)), (17, Some(9)), (40, Some(5))] {
        let query = spec(
            QueryPredicate::all(Vec::new()).expect("valid conjunction"),
            ordering.clone(),
            QueryProjection::fields(&["tier"]).expect("valid projection"),
            QueryPaging::try_new(offset, limit).expect("valid paging"),
            index_free_staleness(),
        );
        let result =
            executor.execute(&serving, &query, &snapshots).expect("the one-shard fan-out executes");
        let direct = ground.execute(&query);
        assert_matches_ground("one-shard", &result, &direct);
        // The composed staleness of one shard is that shard's own report.
        assert_eq!(result.staleness(), direct.staleness());
        assert_eq!(result.index_generation(), direct.index_generation());
        assert_eq!(result.per_shard().len(), 1);
        assert_eq!(result.per_shard()[0].matched(), direct.matched());
    }
    ground.clean();
}

#[test]
fn property_fan_out_matches_single_shard_results_on_identical_data() {
    for round in 0..8_u64 {
        let mut seed = Seed(0x0705_99D3_9E37_7C15 ^ round);
        let count = 12 + seed.small(20);
        let documents = generated_corpus(&mut seed, count);
        let shards = 2 + seed.small(2);
        let boundaries = even_boundaries(&documents, u32::try_from(shards).expect("shards fit"));
        if boundaries.len() + 1 != shards {
            // The corpus's own buckets cannot seat this many shards; the
            // round still runs over the shards they can seat.
            continue;
        }
        let page_limit = 1 + seed.small(6);

        let ground = Ground::build("property", &documents);
        let cluster = Cluster::over_boundaries("property", &documents, &boundaries);
        let executor = ShardedQueries::new(cluster.topology.clone())
            .try_with_page_limit(u32::try_from(page_limit).expect("page bound fits"))
            .expect("valid page bound");
        let serving = cluster.serving();
        let snapshots = cluster.snapshots();

        for _ in 0..3 {
            let offset = seed.below(12);
            let limit = if seed.flag() {
                None
            } else {
                Some(u32::try_from(1 + seed.below(9)).expect("limit fits"))
            };
            let query = generated_spec(&mut seed, offset, limit);
            let result =
                executor.execute(&serving, &query, &snapshots).expect("the fan-out executes");
            let direct = ground.execute(&query);
            assert_matches_ground(&format!("round {round}"), &result, &direct);
        }
        ground.clean();
        let directories = cluster.directories.clone();
        clean_up(cluster, directories);
    }
}

/// One generated specification over a tier-keyed collection whose predicate
/// always equals the Shard Key field, so every round pins one shard.
fn generated_pinned_spec(seed: &mut Seed, offset: u64, limit: Option<u32>) -> QuerySpec {
    let mut conditions = vec![QueryCondition::Equals {
        field: "tier".to_owned(),
        value: DocumentValue::Integer(i64::try_from(seed.below(4)).expect("small tier")),
    }];
    if seed.flag() {
        conditions.push(QueryCondition::Equals {
            field: "label".to_owned(),
            value: DocumentValue::String(format!("label-{}", seed.below(3))),
        });
    }
    let mut keys = Vec::new();
    if seed.flag() {
        keys.push(OrderingKey::try_new("label", OrderDirection::Ascending).expect("valid key"));
    }
    if seed.flag() {
        keys.push(OrderingKey::try_new("note", OrderDirection::Descending).expect("valid key"));
    }
    spec(
        QueryPredicate::all(conditions).expect("valid conjunction"),
        QueryOrdering::try_new(keys).expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(offset, limit).expect("valid paging"),
        index_free_staleness(),
    )
}

#[test]
fn property_pinned_routing_matches_single_shard_results_on_identical_data() {
    for round in 0..6_u64 {
        let mut seed = Seed(0x0B45_1DEA_5EED_0001 ^ round);
        let count = 10 + seed.small(14);
        let documents = generated_corpus(&mut seed, count);
        let shards = 2 + seed.small(2);
        let boundaries = even_boundaries(&documents, u32::try_from(shards).expect("shards fit"));
        if boundaries.len() + 1 != shards {
            continue;
        }
        let topology = tier_keyed_topology(&boundaries);
        let ground = Ground::build("pinned-property", &documents);
        let cluster = Cluster::build("pinned-property", &documents, topology.clone());
        let executor = ShardedQueries::new(topology.clone());
        let serving = cluster.serving();
        let snapshots = cluster.snapshots();

        for _ in 0..3 {
            let offset = seed.below(6);
            let limit = if seed.flag() {
                None
            } else {
                Some(u32::try_from(1 + seed.below(5)).expect("limit fits"))
            };
            let query = generated_pinned_spec(&mut seed, offset, limit);
            let routing = executor.route(&query).expect("routes");
            assert_eq!(
                routing.routes().len(),
                1,
                "round {round}: a full Shard Key equality pins exactly one shard"
            );
            let result =
                executor.execute(&serving, &query, &snapshots).expect("the fan-out executes");
            let direct = ground.execute(&query);
            assert_matches_ground(&format!("pinned round {round}"), &result, &direct);
            assert_eq!(result.per_shard().len(), 1);
            assert_eq!(result.per_shard()[0].matched(), direct.matched());
        }
        ground.clean();
        let directories = cluster.directories.clone();
        clean_up(cluster, directories);
    }
}

// ---------------------------------------------------------------------------
// Paging and order under shard skew
// ---------------------------------------------------------------------------

#[test]
fn paging_and_order_hold_under_deliberate_shard_skew() {
    let documents = fixed_corpus(30);
    let boundaries = skewed_boundaries(&documents, &[80, 15, 5]);
    let ground = Ground::build("skew", &documents);
    let cluster = Cluster::over_boundaries("skew", &documents, &boundaries);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();

    let query_for = |paging: QueryPaging| {
        spec(
            QueryPredicate::all(vec![QueryCondition::GreaterOrEqual {
                field: "tier".to_owned(),
                value: DocumentValue::Integer(1),
            }])
            .expect("valid conjunction"),
            QueryOrdering::try_new(vec![
                OrderingKey::try_new("tier", OrderDirection::Ascending).expect("valid key"),
                OrderingKey::try_new("label", OrderDirection::Descending).expect("valid key"),
                OrderingKey::try_new("note", OrderDirection::Ascending).expect("valid key"),
            ])
            .expect("valid ordering"),
            QueryProjection::whole_document(),
            paging,
            index_free_staleness(),
        )
    };

    for (offset, limit) in
        [(0, None), (0, Some(1)), (5, Some(4)), (11, Some(25)), (28, Some(5)), (60, Some(5))]
    {
        let query = query_for(QueryPaging::try_new(offset, limit).expect("valid paging"));
        let result = executor.execute(&serving, &query, &snapshots).expect("fan-out executes");
        let direct = ground.execute(&query);
        assert_matches_ground(&format!("skew page {offset}/{limit:?}"), &result, &direct);
    }

    // The fixture really is skewed, and the per-shard matched counts still
    // sum to the single-shard matched count.
    let query = query_for(QueryPaging::try_new(0, None).expect("valid paging"));
    let result = executor.execute(&serving, &query, &snapshots).expect("fan-out executes");
    let direct = ground.execute(&query);
    let matched: Vec<u64> = result.per_shard().iter().map(ShardQueryOutcome::matched).collect();
    assert_eq!(matched.len(), 3);
    assert!(
        matched[0] > matched[1] && matched[1] > matched[2],
        "the fixture seats deliberately unequal volumes: {matched:?}"
    );
    assert_eq!(matched.iter().sum::<u64>(), direct.matched());
    ground.clean();
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn fan_out_pages_each_shard_in_bounded_windows() {
    let documents = fixed_corpus(24);
    let boundaries = even_boundaries(&documents, 2);
    let ground = Ground::build("windows", &documents);
    let cluster = Cluster::over_boundaries("windows", &documents, &boundaries);

    // A per-shard page bound of three rows forces every shard through
    // several bounded pages before the merge, well below the matched count.
    let executor = ShardedQueries::new(cluster.topology.clone())
        .try_with_page_limit(3)
        .expect("valid page bound");
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();
    let ordering = QueryOrdering::try_new(vec![
        OrderingKey::try_new("label", OrderDirection::Ascending).expect("valid key"),
    ])
    .expect("valid ordering");

    for (offset, limit) in [(0, None), (0, Some(7)), (4, Some(3)), (19, Some(9))] {
        let query = whole_document_query(
            QueryPaging::try_new(offset, limit).expect("valid paging"),
            ordering.clone(),
        );
        let result = executor.execute(&serving, &query, &snapshots).expect("fan-out executes");
        let direct = ground.execute(&query);
        assert_matches_ground(&format!("window page {offset}/{limit:?}"), &result, &direct);
    }

    // The bound itself is validated, so no fan-out page can ask the seam for
    // an out-of-bound page.
    let refused = |bound: u32| {
        ShardedQueries::new(cluster.topology.clone())
            .try_with_page_limit(bound)
            .expect_err("no per-shard page may ask the seam for an out-of-bound page")
    };
    assert_eq!(
        refused(0),
        ShardedExecutionError::InvalidPage(crate::operations::RequestValidationError::InvalidQuery)
    );
    assert_eq!(
        refused(MAX_PAGE_LIMIT + 1),
        ShardedExecutionError::InvalidPage(crate::operations::RequestValidationError::InvalidQuery)
    );
    ground.clean();
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn paging_past_the_last_row_returns_no_rows_and_the_true_matched_count() {
    let documents = fixed_corpus(10);
    let boundaries = even_boundaries(&documents, 2);
    let ground = Ground::build("past-end", &documents);
    let cluster = Cluster::over_boundaries("past-end", &documents, &boundaries);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();

    let query = whole_document_query(
        QueryPaging::try_new(500, Some(10)).expect("valid paging"),
        QueryOrdering::default(),
    );
    let result = executor.execute(&serving, &query, &snapshots).expect("fan-out executes");
    assert_eq!(result.rows(), &[]);
    assert_eq!(result.matched(), 10, "the matched count stays truthful past the last row");
    assert_eq!(result.matched(), ground.execute(&query).matched());
    ground.clean();
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn shards_without_documents_stay_in_the_reported_outcomes() {
    let documents = fixed_corpus(16);
    // Every document's bucket sits below the boundary, so shard/1 owns
    // buckets but holds no documents.
    let buckets = sorted_buckets(&documents);
    let boundary = (buckets[buckets.len() - 1] + 1).min(BUCKET_SPACE - 1);
    let ground = Ground::build("empty-shard", &documents);
    let cluster = Cluster::over_boundaries("empty-shard", &documents, &[boundary]);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();

    let query = whole_document_query(
        QueryPaging::try_new(0, None).expect("valid paging"),
        QueryOrdering::default(),
    );
    let result = executor.execute(&serving, &query, &snapshots).expect("fan-out executes");
    let direct = ground.execute(&query);
    assert_matches_ground("empty shard", &result, &direct);
    assert_eq!(result.per_shard().len(), 2, "a shard holding no documents is still reached");
    let empty = result
        .per_shard()
        .iter()
        .find(|outcome| outcome.shard() == ShardId::from_number(1))
        .expect("the empty shard is reported");
    assert_eq!(empty.matched(), 0);
    assert_eq!(empty.rows(), 0);
    ground.clean();
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

// ---------------------------------------------------------------------------
// Staleness across shards
// ---------------------------------------------------------------------------

/// Registers `name` on one shard's engine and publishes one segment per
/// commit round, returning the published generations.
fn publish_index(engine: &Engine, name: &str, rounds: usize) -> Vec<ObservedGeneration> {
    let namespace = namespace(name);
    let definition = IndexDefinition::try_new(
        collection("orders"),
        vec![IndexField::try_new("tier", IndexCapabilities::FULL).expect("valid field")],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let catalog = engine.index_catalog();
    catalog.register_explicit(&namespace, &definition).expect("registers");
    let builder = IndexBuilder::new(&catalog, engine);
    let mut published = Vec::new();
    for round in 0..rounds {
        let scope = scope_of(engine, "tenant-a");
        commit_documents(
            engine,
            &scope,
            &format!("index-op-{round}"),
            vec![order_document(
                &format!("orders/index-{round}"),
                &[("tier", DocumentValue::Integer(1))],
            )],
        );
        let RebuildOutcome::Published { epoch, generation, .. } =
            builder.rebuild(&namespace, engine.identity()).expect("rebuild publishes")
        else {
            panic!("the rebuild publishes");
        };
        published.push(ObservedGeneration::new(epoch, generation));
    }
    published
}

fn staleness_spec(mode: StalenessMode, namespace: &IndexNamespace) -> QuerySpec {
    spec(
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(mode, vec![namespace.clone()]).expect("valid policy"),
    )
}

#[test]
fn composed_staleness_reports_the_strictest_shard_basis() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let mut cluster = Cluster::over_boundaries("staleness-mixed", &documents, &boundaries);
    // Shard/0's index advances one generation further than shard/1's, so
    // neither shard is individually stale while the composed answer is.
    let first = publish_index(&cluster.engines[0], "by-tier", 2);
    let second = publish_index(&cluster.engines[1], "by-tier", 1);
    assert_eq!(first.last(), Some(&ObservedGeneration::new(1, 2)));
    assert_eq!(second.last(), Some(&ObservedGeneration::new(1, 1)));
    cluster.refresh_snapshots();

    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();
    let namespace = namespace("by-tier");
    let query = staleness_spec(StalenessMode::AllowStale, &namespace);
    let result = executor.execute(&serving, &query, &snapshots).expect("allow-stale answers");

    let detail = result.namespace_staleness(&namespace).expect("the namespace is detailed");
    assert_eq!(detail.per_shard().len(), 2);
    for observation in detail.per_shard() {
        assert!(!observation.stale(), "each shard meets its own observation");
        assert_eq!(observation.used(), observation.observed());
    }
    // The composed observation names the strictest shard's requirement and
    // the least advanced shard's basis, and reports the answer stale.
    assert_eq!(detail.composed().observed(), Some(ObservedGeneration::new(1, 2)));
    assert_eq!(detail.composed().used(), Some(ObservedGeneration::new(1, 1)));
    assert!(detail.composed().stale());
    assert_eq!(result.staleness().observation_for(&namespace), Some(detail.composed()));
    assert_eq!(
        result.index_generation().map(IndexGenerationRef::generation),
        Some(ObservedGeneration::new(1, 1))
    );
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn composed_staleness_is_stale_when_any_shard_supplies_no_basis() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let mut cluster = Cluster::over_boundaries("staleness-absent", &documents, &boundaries);
    publish_index(&cluster.engines[0], "by-tier", 1);
    cluster.refresh_snapshots();

    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();
    let namespace = namespace("by-tier");
    let query = staleness_spec(StalenessMode::AllowStale, &namespace);
    let result = executor.execute(&serving, &query, &snapshots).expect("allow-stale answers");

    let detail = result.namespace_staleness(&namespace).expect("the namespace is detailed");
    let absent = detail
        .per_shard()
        .iter()
        .find(|observation| observation.shard() == ShardId::from_number(1))
        .expect("the shard without the index is reported");
    assert_eq!(absent.observed(), None);
    assert_eq!(absent.used(), None);
    assert!(absent.stale());
    assert!(detail.composed().stale());
    assert_eq!(detail.composed().used(), None, "an absent basis is never composed away");
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn a_strict_policy_fails_closed_when_any_shard_cannot_meet_it() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let mut cluster = Cluster::over_boundaries("staleness-strict", &documents, &boundaries);
    publish_index(&cluster.engines[0], "by-tier", 1);
    cluster.refresh_snapshots();

    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();
    let namespace = namespace("by-tier");
    let query = staleness_spec(StalenessMode::Strict, &namespace);
    let error = executor
        .execute(&serving, &query, &snapshots)
        .expect_err("one shard cannot meet the strict policy, so the answer fails closed");
    assert_eq!(
        error,
        ShardedExecutionError::QueryShard {
            shard: ShardId::from_number(1),
            error: ShardServingError::Query(QueryError::IndexNotReady),
        },
        "the failure names the shard that could not meet the policy"
    );
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn staleness_detail_names_every_reached_shard() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 3);
    let mut cluster = Cluster::over_boundaries("stalenames", &documents, &boundaries);
    for engine in &cluster.engines {
        publish_index(engine, "by-tier", 1);
    }
    cluster.refresh_snapshots();

    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();
    let namespace = namespace("by-tier");
    let query = staleness_spec(StalenessMode::AllowStale, &namespace);
    let result = executor.execute(&serving, &query, &snapshots).expect("allow-stale answers");

    let detail = result.namespace_staleness(&namespace).expect("the namespace is detailed");
    let reached: Vec<ShardId> =
        detail.per_shard().iter().map(ShardStalenessObservation::shard).collect();
    assert_eq!(reached, cluster.topology.table().shards(), "every reached shard is named");
    for observation in detail.per_shard() {
        assert_eq!(observation.observed(), Some(ObservedGeneration::new(1, 1)));
        assert!(!observation.stale());
    }
    assert!(!detail.composed().stale(), "equal bases compose to a current answer");
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

// ---------------------------------------------------------------------------
// Sharded index definition and readers
// ---------------------------------------------------------------------------

fn sharded_definition(name: &str) -> ShardedIndexDefinition {
    let definition = IndexDefinition::try_new(
        collection("orders"),
        vec![IndexField::try_new("tier", IndexCapabilities::FULL).expect("valid field")],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    ShardedIndexDefinition::try_new(namespace(name), definition).expect("valid sharded definition")
}

#[test]
fn sharded_index_definitions_register_on_every_seated_shard() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("register", &documents, &boundaries);
    let serving = cluster.serving();

    let registration = sharded_definition("by-tier")
        .register(&cluster.topology, &serving)
        .expect("the registration fans out");
    assert!(registration.is_uniform(), "both shards accepted the definition");
    assert_eq!(
        registration.registered_epochs(),
        vec![(ShardId::from_number(0), 1), (ShardId::from_number(1), 1)]
    );
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn sharded_index_definitions_refuse_auto_namespaces_and_auto_kinds() {
    let definition = IndexDefinition::try_new(
        collection("orders"),
        vec![IndexField::try_new("tier", IndexCapabilities::FULL).expect("valid field")],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let auto_name = IndexNamespace::try_new(
        tenant("tenant-a"),
        &format!("{AUTO_NAMESPACE_PREFIX}orders/ByTier"),
    )
    .expect("the namespace text itself is valid");
    assert_eq!(
        ShardedIndexDefinition::try_new(auto_name, definition),
        Err(ShardedIndexRefusal::AutoNamespaceReserved {
            name: format!("{AUTO_NAMESPACE_PREFIX}orders/ByTier")
        })
    );

    let auto_kind = IndexDefinition::try_new(
        collection("orders"),
        vec![IndexField::try_new("tier", IndexCapabilities::FULL).expect("valid field")],
        IndexKind::Auto,
    )
    .expect("valid definition");
    assert_eq!(
        ShardedIndexDefinition::try_new(namespace("by-tier"), auto_kind),
        Err(ShardedIndexRefusal::AutoKind)
    );
}

#[test]
fn sharded_index_readers_report_each_shards_publication_and_the_least_advanced_basis() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("reader", &documents, &boundaries);
    let definition = sharded_definition("by-tier");
    {
        let serving = cluster.serving();
        definition.register(&cluster.topology, &serving).expect("the registration fans out");
    }
    publish_index(&cluster.engines[0], "by-tier", 2);
    publish_index(&cluster.engines[1], "by-tier", 1);

    let serving = cluster.serving();
    let view = definition.read(&cluster.topology, &serving).expect("the reader fans out");
    assert!(view.is_uniformly_registered());
    assert!(view.is_uniformly_published());
    assert_eq!(view.least_advanced(), Some(ObservedGeneration::new(1, 1)));
    let published: Vec<Option<ObservedGeneration>> = view
        .per_shard()
        .iter()
        .filter_map(|state| match state {
            ShardIndexState::Registered { published, .. } => Some(*published),
            ShardIndexState::Unregistered { .. } => None,
        })
        .collect();
    assert_eq!(published.len(), 2, "both shards report their publication");
    assert!(published.iter().all(Option::is_some));
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn a_shard_registered_but_unpublished_leaves_no_uniform_basis() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("unpublished", &documents, &boundaries);
    let serving = cluster.serving();
    let definition = sharded_definition("by-tier");
    definition.register(&cluster.topology, &serving).expect("the registration fans out");
    // Only shard/0 ever publishes: the table holds the definition everywhere
    // but has published it nowhere uniformly.
    publish_index(&cluster.engines[0], "by-tier", 1);

    let view = definition.read(&cluster.topology, &serving).expect("the reader fans out");
    assert!(view.is_uniformly_registered(), "both shards hold the definition");
    assert!(!view.is_uniformly_published(), "one shard published nothing");
    assert_eq!(
        view.least_advanced(),
        None,
        "a table with one unpublished shard reports no uniform basis"
    );
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn partial_registration_is_reported_not_hidden() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("partial", &documents, &boundaries);
    // A serving set without shard/1's Member cannot register there.
    let partial: Vec<(MemberId, &Engine)> = vec![(member(0), &cluster.engines[0])];
    let partial_serving = MemberServing::try_new(partial).expect("serving set seats");
    let registration = sharded_definition("by-tier")
        .register(&cluster.topology, &partial_serving)
        .expect("the registration fans out over the shards it can reach");
    assert!(!registration.is_uniform());
    assert_eq!(registration.registered_epochs(), vec![(ShardId::from_number(0), 1)]);

    // The reader reports the shard the definition never reached.
    let full_serving = cluster.serving();
    let view = sharded_definition("by-tier")
        .read(&cluster.topology, &full_serving)
        .expect("the reader fans out");
    assert!(!view.is_uniformly_registered());
    assert!(!view.is_uniformly_published());
    assert!(view.unregistered_shards().contains(&ShardId::from_number(1)));
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn the_reader_least_advanced_basis_matches_the_composed_query_staleness() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let mut cluster = Cluster::over_boundaries("reader-staleness", &documents, &boundaries);
    let definition = sharded_definition("by-tier");
    {
        let serving = cluster.serving();
        definition.register(&cluster.topology, &serving).expect("the registration fans out");
    }
    publish_index(&cluster.engines[0], "by-tier", 2);
    publish_index(&cluster.engines[1], "by-tier", 1);
    cluster.refresh_snapshots();
    let serving = cluster.serving();

    let view = definition.read(&cluster.topology, &serving).expect("the reader fans out");
    let executor = ShardedQueries::new(cluster.topology.clone());
    let snapshots = cluster.snapshots();
    let namespace = namespace("by-tier");
    let result = executor
        .execute(&serving, &staleness_spec(StalenessMode::AllowStale, &namespace), &snapshots)
        .expect("allow-stale answers");
    let detail = result.namespace_staleness(&namespace).expect("the namespace is detailed");
    assert_eq!(detail.composed().used(), view.least_advanced());
    assert!(detail.composed().stale(), "the least advanced basis is not the strictest basis");
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

// ---------------------------------------------------------------------------
// TQL composition
// ---------------------------------------------------------------------------

#[test]
fn a_tql_query_lowers_to_the_plan_the_fan_out_executes() {
    let documents = fixed_corpus(20);
    let boundaries = even_boundaries(&documents, 2);
    let ground = Ground::build("tql", &documents);
    let cluster = Cluster::over_boundaries("tql", &documents, &boundaries);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots = cluster.snapshots();

    let text =
        "select tier, label from orders where tier >= 1 order by label desc limit 6 offset 3";
    let parsed = tql::parse(tql::GrammarVersion::CURRENT, text, &tql::TqlParameters::new())
        .expect("the TQL text parses");
    let lowered = tql::lower_to_spec(
        &parsed,
        &tql::TqlParameters::new(),
        tenant("tenant-a"),
        deadline(),
        Cancellation::new(),
    )
    .expect("the TQL query lowers");

    let result = executor.execute(&serving, &lowered, &snapshots).expect("the fan-out executes");
    let direct = ground.execute(&lowered);
    assert_matches_ground("tql", &result, &direct);
    ground.clean();
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

// ---------------------------------------------------------------------------
// Fail-closed behavior
// ---------------------------------------------------------------------------

#[test]
fn fan_out_fails_closed_when_a_routed_shard_has_no_snapshot() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("no-snapshot", &documents, &boundaries);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let snapshots =
        ShardSnapshots::try_new(vec![(ShardId::from_number(0), cluster.snapshots[0].clone())])
            .expect("distinct snapshots");
    let query = whole_document_query(
        QueryPaging::try_new(0, None).expect("valid paging"),
        QueryOrdering::default(),
    );
    let error = executor
        .execute(&serving, &query, &snapshots)
        .expect_err("a routed shard without a snapshot never answers partially");
    assert_eq!(error, ShardedExecutionError::SnapshotMissing { shard: ShardId::from_number(1) });
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn fan_out_fails_closed_when_a_snapshot_names_another_tenant() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("other-tenant", &documents, &boundaries);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let serving = cluster.serving();
    let foreign = snapshot_of(&cluster.engines[1], "tenant-b");
    let snapshots = ShardSnapshots::try_new(vec![
        (ShardId::from_number(0), cluster.snapshots[0].clone()),
        (ShardId::from_number(1), foreign),
    ])
    .expect("distinct snapshots");
    let query = whole_document_query(
        QueryPaging::try_new(0, None).expect("valid paging"),
        QueryOrdering::default(),
    );
    let error = executor
        .execute(&serving, &query, &snapshots)
        .expect_err("a snapshot of another tenant never serves this query's claim");
    assert_eq!(
        error,
        ShardedExecutionError::SnapshotTenantMismatch { shard: ShardId::from_number(1) }
    );
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn fan_out_fails_closed_when_a_members_endpoint_is_missing() {
    let documents = fixed_corpus(12);
    let boundaries = even_boundaries(&documents, 2);
    let cluster = Cluster::over_boundaries("unserved", &documents, &boundaries);
    let executor = ShardedQueries::new(cluster.topology.clone());
    let partial: Vec<(MemberId, &Engine)> = vec![(member(0), &cluster.engines[0])];
    let serving = MemberServing::try_new(partial).expect("serving set seats");
    let snapshots = cluster.snapshots();
    let query = whole_document_query(
        QueryPaging::try_new(0, None).expect("valid paging"),
        QueryOrdering::default(),
    );
    let error = executor
        .execute(&serving, &query, &snapshots)
        .expect_err("a shard whose Member has no endpoint never answers partially");
    assert_eq!(
        error,
        ShardedExecutionError::QueryShard {
            shard: ShardId::from_number(1),
            error: ShardServingError::MemberUnserved { owner: member(1) },
        }
    );
    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}

#[test]
fn a_family_source_query_refuses_sharded_execution_before_routing() {
    // GAP-Q19 D5: a family source refuses the fan-out before routing —
    // the merge and paging are document-row keyed, and silently dropping
    // typed family rows is the failure this refusal prevents.
    let topology = single_shard_topology();
    let cluster = Cluster::build("family-refuse", &[], topology.clone());
    let executor = ShardedQueries::new(topology);

    let family = QuerySpec::try_new(
        tenant("tenant-a"),
        QuerySource::counters(),
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("likes".to_owned()),
        }])
        .expect("valid conjunction"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("name", OrderDirection::Ascending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        index_free_staleness(),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid family specification");

    let err = executor
        .execute(&cluster.serving(), &family, &cluster.snapshots())
        .expect_err("a family source must refuse the fan-out");
    assert_eq!(err, ShardedExecutionError::FamilySourceUnsupported);

    let directories = cluster.directories.clone();
    clean_up(cluster, directories);
}
