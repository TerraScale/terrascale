//! Cross-region replication, in-process form (S-05 slice 2a, D-35/D-36):
//! the journal tailer's pull/apply path proven against two members of the
//! same tenant on separate durable locations — the D-45 cross-region
//! subscriber invariant, the idempotency binding, and loop prevention.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase::engine::CommitOutcome;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase_tbep::registry::{BoundScope, ChannelBindRequest};
use terrabase_tbep::replication::{apply_publications, pull_origin_publications, replicate_once};
use terrabase_tbep::{MemberHost, MemberHostOptions, TbepHost};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(
            std::env::temp_dir()
                .join(format!("terrabase-s05-repl-{label}-{}-{number}", std::process::id())),
        )
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestLocation {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn scope() -> BoundScope {
    BoundScope {
        principal: "spiffe://local/s05-test".into(),
        tenant: "tenant-a".into(),
        project: "project-a".into(),
        database: "database-a".into(),
    }
}

/// One region's member: its own durable location, the same tenant.
fn member(label: &str) -> (TestLocation, MemberHost) {
    let location = TestLocation::new(label);
    let host = MemberHost::open(
        location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the member host opens");
    host.bind_channel(&ChannelBindRequest { scope: scope() }).expect("the database scope binds");
    (location, host)
}

/// Writes one origin document through the member's engine.
fn put(host: &MemberHost, id: &str, value: i64) {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    // Operation identities are unique per call: a replicated row's
    // identity (the source's operation id) shares the target's ledger
    // binding space, so a colliding local id would read as reuse.
    let operation =
        OperationId::try_new(&format!("put-{id}-{value}")).expect("the operation identity parses");
    let mut transaction =
        engine.begin(&engine_scope, snapshot, operation).expect("the write begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).expect("the document id parses"),
                CollectionName::try_new("orders").expect("the collection parses"),
                DocumentValue::Object(BTreeMap::from([(
                    "value".to_owned(),
                    DocumentValue::Integer(value),
                )])),
            ),
            EngineVersion::Any,
        )
        .expect("the put stages");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        outcome => panic!("the write committed: {outcome:?}"),
    }
}

/// The member's live documents of the collection, at a fresh snapshot.
fn orders(host: &MemberHost) -> Vec<Document> {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    engine
        .snapshot_documents_in_collection(
            &engine_scope,
            &snapshot,
            &CollectionName::try_new("orders").expect("the collection parses"),
        )
        .expect("the collection scan serves")
}

/// D-45's pinned cross-region subscriber invariant, in-process: a reactive
/// subscriber on the TARGET member receives the replicated write's delta —
/// `kind='replicated'` rows fan out to local subscribers exactly like
/// origin rows.
#[test]
fn replicated_rows_fan_out_to_the_target_member_subscribers() {
    use terrabase_sync_core::server::{Comparison, Condition, FieldValue, SubscriptionSpec};
    use terrabase_tbep::reactive::ReactiveFrame;

    let (_source_location, source) = member("subscriber-source");
    let (_target_location, target) = member("subscriber-target");
    let scope = scope();

    let spec = SubscriptionSpec {
        collection: "orders".into(),
        conditions: vec![Condition {
            field: "value".into(),
            op: Comparison::GreaterOrEqual,
            value: FieldValue::Number(0.0),
        }],
        projection: vec!["value".into()],
    };
    let (_id, feed, _missed) =
        target.reactive_subscribe(&scope, &spec, None).expect("the subscription opens");
    let ReactiveFrame::Seed { members, .. } = feed.recv().expect("the seed arrives") else {
        panic!("the first frame is the seed");
    };
    assert!(members.is_empty(), "the target starts empty");

    put(&source, "orders/1", 41);
    let mut cursor = None;
    let applied = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut cursor)
        .expect("the tailer ships the origin row");
    assert_eq!(applied, 1, "the write applies on the target");

    target.reactive_pump(&scope).expect("the pump advances");
    let ReactiveFrame::Delta { delta, .. } = feed.recv().expect("the delta arrives") else {
        panic!("the second frame is a delta");
    };
    assert!(
        matches!(
            delta.operation,
            terrabase_sync_core::server::DeltaOp::Insert { ref document, .. } if document == "orders/1"
        ),
        "the replicated write fans out to the local subscriber: {:?}",
        delta.operation
    );
    assert_eq!(delta.revision, 1, "the subscription's first revision");
}

/// Idempotency: an operation identity derived from the source origin, commit,
/// collection, and document binds each record, so re-pulled records replay the recorded
/// publication instead of double-writing.
#[test]
fn a_re_pulled_record_replays_instead_of_double_writing() {
    let (_source_location, source) = member("idempotent-source");
    let (_target_location, target) = member("idempotent-target");
    let scope = scope();

    put(&source, "orders/1", 7);
    let mut cursor = None;
    let applied = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut cursor)
        .expect("the first pass applies");
    assert_eq!(applied, 1);

    // A re-pull from the very beginning of the journal: every record the
    // first pass applied resolves to its recorded publication.
    let mut replay_cursor = None;
    let reapplied =
        replicate_once(source.engine_host(), target.engine_host(), &scope, &mut replay_cursor)
            .expect("the replay pass resolves");
    assert_eq!(reapplied, 0, "no record publishes a second time");
    assert_eq!(orders(&target).len(), 1, "the document count stays one");
    assert_eq!(
        orders(&target)[0].value(),
        &DocumentValue::Object(BTreeMap::from([("value".to_owned(), DocumentValue::Integer(7))])),
        "the surviving document is the replicated body"
    );

    // The same record applied directly — not through the tailer — also
    // resolves to the replay.
    let (records, _) = pull_origin_publications(source.engine_host(), &scope, None)
        .expect("the source journal pulls");
    assert_eq!(records.len(), 1);
    let applied = apply_publications(target.engine_host(), &scope, &records)
        .expect("the direct apply resolves");
    assert_eq!(applied, 0, "the direct re-apply replays too");
    assert_eq!(orders(&target).len(), 1);
}

/// Loop prevention: after replication, the TARGET's journal holds only
/// `kind='replicated'` rows, so a tailer pointing back at it ships
/// nothing.
#[test]
fn the_target_never_re_ships_its_replicated_rows() {
    let (_source_location, source) = member("loop-source");
    let (_target_location, target) = member("loop-target");
    let scope = scope();

    put(&source, "orders/1", 11);
    put(&source, "orders/2", 12);
    let mut cursor = None;
    let applied = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut cursor)
        .expect("both writes apply");
    assert_eq!(applied, 2);
    assert_eq!(orders(&target).len(), 2);

    let (records, _next) = pull_origin_publications(target.engine_host(), &scope, None)
        .expect("the target journal pulls");
    assert!(records.is_empty(), "replicated rows never re-ship: {records:?}");

    // And the drained source ships nothing new either.
    let shipped = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut cursor)
        .expect("the drained tail is a no-op");
    assert_eq!(shipped, 0);
}

/// The deterministic merge (D-36): concurrent writes to the same
/// document in two members converge to identical bytes in both, the
/// `(hlc, origin)` total order picks the same winner everywhere, and a
/// superseded record never overwrites the winner.
#[test]
fn concurrent_writes_converge_deterministically() {
    let (source_location, source) = member("converge-a");
    let (target_location, target) = member("converge-b");
    let scope = scope();

    // Region A writes v1; it replicates to B (B's clock witnesses A's
    // stamp, so B's next origin tick is strictly greater).
    put(&source, "orders/race", 1);
    let mut cursor = None;
    let shipped = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut cursor)
        .expect("the first replication runs");
    assert_eq!(shipped, 1);

    // Region B — its clock now ahead of the witnessed stamp — writes v2.
    put(&target, "orders/race", 2);
    let mut back = None;
    let shipped = replicate_once(target.engine_host(), source.engine_host(), &scope, &mut back)
        .expect("the reverse replication runs");
    assert_eq!(shipped, 1);

    // Both members serve v2: the same winner, identical bytes.
    for (label, host) in [("A", &source), ("B", &target)] {
        let documents = orders(host);
        assert_eq!(documents.len(), 1, "one live document in {label}");
        let DocumentValue::Object(fields) = documents[0].value() else {
            panic!("an object body in {label}");
        };
        let value = fields.get("value").expect("the field survives");
        assert_eq!(value, &DocumentValue::Integer(2), "{label} serves the winner");
    }

    // The superseded v1 cannot come back: re-shipping A's journal from
    // the start replays the already-applied v1 (identity replay) and
    // the merge would refuse it anyway.
    let mut replay = None;
    let shipped = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut replay)
        .expect("the replay replication runs");
    assert!(shipped <= 1, "the replayed record replays or is superseded: {shipped}");
    let documents = orders(&target);
    let DocumentValue::Object(fields) = documents[0].value() else {
        panic!("an object body after the replay");
    };
    assert_eq!(
        fields.get("value").expect("the field survives"),
        &DocumentValue::Integer(2),
        "the winner survives the loser's replay"
    );

    drop(source);
    drop(target);
    let _ = (source_location, target_location);
}

/// Catch-up after a partition (the journey's fault step, in-process):
/// the link is simply absent while writes land; healing replays every
/// missed record from the held-back cursor with zero acknowledged-write
/// loss, and both members converge.
#[test]
fn a_partitioned_member_catches_up_from_its_cursor() {
    let (source_location, source) = member("heal-a");
    let (target_location, target) = member("heal-b");
    let scope = scope();

    // Before the partition: one replicated write establishes the cursor.
    put(&source, "orders/pre", 1);
    let mut cursor = None;
    let shipped = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut cursor)
        .expect("the pre-partition replication runs");
    assert_eq!(shipped, 1);
    let settled = cursor;

    // The partition: writes land with no link running.
    for round in 0..25 {
        put(&source, &format!("orders/down-{round}"), round);
    }

    // Healing: replication resumes from the held-back cursor and every
    // missed record arrives exactly once.
    let mut healed = settled;
    let shipped = replicate_once(source.engine_host(), target.engine_host(), &scope, &mut healed)
        .expect("the healed replication runs");
    assert_eq!(shipped, 25, "every missed record ships, none lost");

    let documents = orders(&target);
    assert_eq!(documents.len(), 26, "the pre-partition write plus the missed twenty-five");
    // And the source's own state matches — convergence with zero loss.
    assert_eq!(orders(&source).len(), 26);

    drop(source);
    drop(target);
    let _ = (source_location, target_location);
}
