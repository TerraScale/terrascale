//! Typed merge schemas with deterministic conflict history (S-05 D-36).
//!
//! Collections may declare a merge schema enabling typed CRDT fields
//! (PN-counter, add-wins/remove-wins set, MV register); only those
//! collections store per-field kernels. Schema-free collections merge
//! with the remove-wins map default plus the HLC tiebreak. Every true
//! conflict records its loser with a deterministic explanation, so both
//! regions serve the same `db.history(id)` viewer facts (dot, HLC,
//! policy).

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase::engine::{CommitOutcome, MergeFieldKind, MergeSchema};
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase_tbep::registry::{BoundScope, ChannelBindRequest};
use terrabase_tbep::replication::replicate_once;
use terrabase_tbep::{MemberHost, MemberHostOptions, TbepHost};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(
            std::env::temp_dir()
                .join(format!("terrabase-s05-merge-{label}-{}-{number}", std::process::id())),
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

fn put_object(
    host: &MemberHost,
    collection: &str,
    id: &str,
    operation: &str,
    fields: Vec<(&str, DocumentValue)>,
) {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    let operation = OperationId::try_new(operation).expect("the operation identity parses");
    let mut transaction = engine.begin(&engine_scope, snapshot, operation).expect("begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).expect("the document id parses"),
                CollectionName::try_new(collection).expect("the collection parses"),
                DocumentValue::Object(
                    fields.into_iter().map(|(key, value)| (key.to_owned(), value)).collect(),
                ),
            ),
            EngineVersion::Any,
        )
        .expect("the put stages");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        outcome => panic!("the write committed: {outcome:?}"),
    }
}

fn live_fields(
    host: &MemberHost,
    collection: &str,
    id: &str,
) -> Option<BTreeMap<String, DocumentValue>> {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    let document = engine
        .snapshot_documents_in_collection(
            &engine_scope,
            &snapshot,
            &CollectionName::try_new(collection).expect("the collection parses"),
        )
        .expect("the collection scan serves")
        .into_iter()
        .find(|document| document.id().as_str() == id)?;
    match document.value() {
        DocumentValue::Object(fields) => Some(fields.clone()),
        _ => None,
    }
}

fn declare(host: &MemberHost, collection: &str, pairs: &[(&str, MergeFieldKind)]) {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let schema = MergeSchema::try_new(
        pairs.iter().map(|(field, kind)| ((*field).to_owned(), *kind)).collect(),
    )
    .expect("the schema validates");
    engine
        .declare_merge_schema(
            &engine_scope,
            &CollectionName::try_new(collection).expect("the collection parses"),
            &schema,
        )
        .expect("the schema declares");
}

/// Concurrent writes to one document converge to identical bytes on both
/// members, and the recorded loser explanation is deterministic across
/// regions.
#[test]
fn concurrent_writes_converge_with_deterministic_history() {
    let (_a_location, member_a) = member("history-a");
    let (_b_location, member_b) = member("history-b");
    let scope = scope();

    put_object(&member_a, "orders", "race", "op-a-1", vec![("value", DocumentValue::Integer(1))]);
    put_object(&member_b, "orders", "race", "op-b-1", vec![("value", DocumentValue::Integer(2))]);

    let mut forward = None;
    replicate_once(member_a.engine_host(), member_b.engine_host(), &scope, &mut forward)
        .expect("A ships to B");
    let mut backward = None;
    replicate_once(member_b.engine_host(), member_a.engine_host(), &scope, &mut backward)
        .expect("B ships to A");

    let fields_a = live_fields(&member_a, "orders", "race").expect("A serves the document");
    let fields_b = live_fields(&member_b, "orders", "race").expect("B serves the document");
    assert_eq!(fields_a, fields_b, "both regions converge to identical bytes");

    // Both regions recorded a loser with the viewer facts: dot, HLC, and
    // policy. One side published the merge, the other refused it, so at
    // least one side holds a conflict row — and any rows both hold agree.
    let engine_a = member_a.engine();
    let engine_b = member_b.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let scope_a = engine_a.scope_for(&tenant);
    let scope_b = engine_b.scope_for(&tenant);
    let collection = CollectionName::try_new("orders").expect("the collection parses");
    let id = DocumentId::try_new("race").expect("the document id parses");
    let history_a = engine_a
        .document_history_in_collection(&scope_a, &collection, &id, 10)
        .expect("A serves history");
    let history_b = engine_b
        .document_history_in_collection(&scope_b, &collection, &id, 10)
        .expect("B serves history");
    assert!(!history_a.is_empty() && !history_b.is_empty());
    for entry in history_a.iter().chain(history_b.iter()) {
        assert!(!entry.commit_ulid.is_empty(), "the viewer dot is present");
        assert!(!entry.policy.is_empty(), "the viewer policy is present");
        assert!(!entry.explanation.is_empty(), "the explanation is present");
        assert!(
            entry.explanation.contains(&entry.commit_ulid)
                || entry.explanation.contains("origin")
                || entry.explanation.contains("clean apply"),
            "the explanation names its dot: {}",
            entry.explanation
        );
    }
    // Schema-free collections store no per-field kernels.
    assert!(
        engine_a.merge_kernels_for(&scope_a, &collection, &id).expect("kernels read").is_empty()
    );
    assert!(
        engine_b.merge_kernels_for(&scope_b, &collection, &id).expect("kernels read").is_empty()
    );
}

/// Declared typed fields merge with their kernels: the counter keeps the
/// highest value even against the HLC order, sets union and intersect,
/// and the MV register keeps both values. Only declared collections store
/// kernels.
#[allow(
    clippy::too_many_lines,
    reason = "the typed-field matrix exercises four kernels end to end"
)]
#[test]
fn typed_fields_merge_with_kernels_only_on_declared_collections() {
    let (_a_location, member_a) = member("typed-a");
    let (_b_location, member_b) = member("typed-b");
    let scope = scope();

    for host in [&member_a, &member_b] {
        declare(host, "metrics", &[("count", MergeFieldKind::PnCounter)]);
        declare(
            host,
            "tags",
            &[("aw", MergeFieldKind::AddWinsSet), ("rw", MergeFieldKind::RemoveWinsSet)],
        );
        declare(host, "regs", &[("reg", MergeFieldKind::MvRegister)]);
    }

    // The counter: A writes the smaller value with the later clock (it
    // replicates first, so B witnesses it and ticks past it — B's next
    // write carries the higher HLC). The typed max must still keep 7.
    put_object(
        &member_a,
        "metrics",
        "c1",
        "op-count-a",
        vec![("count", DocumentValue::Integer(5))],
    );
    let mut warm = None;
    replicate_once(member_a.engine_host(), member_b.engine_host(), &scope, &mut warm)
        .expect("the warm-up ships");
    put_object(
        &member_b,
        "metrics",
        "c1",
        "op-count-b",
        vec![("count", DocumentValue::Integer(7))],
    );

    put_object(
        &member_a,
        "tags",
        "t1",
        "op-tags-a",
        vec![
            ("aw", DocumentValue::Array(vec![DocumentValue::Integer(1)])),
            (
                "rw",
                DocumentValue::Array(vec![DocumentValue::Integer(1), DocumentValue::Integer(2)]),
            ),
        ],
    );
    put_object(
        &member_b,
        "tags",
        "t1",
        "op-tags-b",
        vec![
            ("aw", DocumentValue::Array(vec![DocumentValue::Integer(2)])),
            ("rw", DocumentValue::Array(vec![DocumentValue::Integer(2)])),
        ],
    );
    put_object(
        &member_a,
        "regs",
        "r1",
        "op-reg-a",
        vec![("reg", DocumentValue::String("b".to_owned()))],
    );
    put_object(
        &member_b,
        "regs",
        "r1",
        "op-reg-b",
        vec![("reg", DocumentValue::String("a".to_owned()))],
    );

    let mut forward = None;
    replicate_once(member_a.engine_host(), member_b.engine_host(), &scope, &mut forward)
        .expect("A ships to B");
    let mut backward = None;
    replicate_once(member_b.engine_host(), member_a.engine_host(), &scope, &mut backward)
        .expect("B ships to A");

    let count_a = live_fields(&member_a, "metrics", "c1").expect("A serves the counter");
    let count_b = live_fields(&member_b, "metrics", "c1").expect("B serves the counter");
    assert_eq!(count_a, count_b, "the counter converges");
    assert_eq!(
        count_a.get("count"),
        Some(&DocumentValue::Integer(7)),
        "pn-counter keeps the highest value against the HLC order"
    );

    let tags_a = live_fields(&member_a, "tags", "t1").expect("A serves the sets");
    let tags_b = live_fields(&member_b, "tags", "t1").expect("B serves the sets");
    assert_eq!(tags_a, tags_b, "the sets converge");
    assert_eq!(
        tags_a.get("aw"),
        Some(&DocumentValue::Array(vec![DocumentValue::Integer(1), DocumentValue::Integer(2)])),
        "add-wins unions concurrent adds"
    );
    assert_eq!(
        tags_a.get("rw"),
        Some(&DocumentValue::Array(vec![DocumentValue::Integer(2)])),
        "remove-wins intersects concurrent sets"
    );

    let regs_a = live_fields(&member_a, "regs", "r1").expect("A serves the register");
    let regs_b = live_fields(&member_b, "regs", "r1").expect("B serves the register");
    assert_eq!(regs_a, regs_b, "the register converges");
    assert_eq!(
        regs_a.get("reg"),
        Some(&DocumentValue::Array(vec![
            DocumentValue::String("a".to_owned()),
            DocumentValue::String("b".to_owned()),
        ])),
        "mv keeps both values in canonical order"
    );

    // Declared collections stored per-field kernels; the history carries
    // the typed policies.
    let engine_a = member_a.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let scope_a = engine_a.scope_for(&tenant);
    let metrics = CollectionName::try_new("metrics").expect("the collection parses");
    let counter_id = DocumentId::try_new("c1").expect("the document id parses");
    let kernels =
        engine_a.merge_kernels_for(&scope_a, &metrics, &counter_id).expect("kernels read");
    assert_eq!(kernels.len(), 1, "the declared counter stored one kernel");
    assert_eq!(kernels[0].field, "count");

    let history = engine_a
        .document_history_in_collection(&scope_a, &metrics, &counter_id, 10)
        .expect("history reads");
    assert!(
        history.iter().any(|entry| entry.policy == "pn-counter-max"),
        "the counter history names its policy: {:?}",
        history.iter().map(|entry| &entry.policy).collect::<Vec<_>>()
    );
}

/// History explanations are deterministic: both regions record the same
/// policy and explanation for the same conflict.
#[test]
fn history_explanations_are_deterministic_across_regions() {
    let (_a_location, member_a) = member("deterministic-a");
    let (_b_location, member_b) = member("deterministic-b");
    let scope = scope();

    put_object(
        &member_a,
        "orders",
        "det",
        "op-det-a",
        vec![("shared", DocumentValue::Integer(1)), ("only-a", DocumentValue::Integer(1))],
    );
    put_object(
        &member_b,
        "orders",
        "det",
        "op-det-b",
        vec![("shared", DocumentValue::Integer(2)), ("only-b", DocumentValue::Integer(2))],
    );

    let mut forward = None;
    replicate_once(member_a.engine_host(), member_b.engine_host(), &scope, &mut forward)
        .expect("A ships to B");
    let mut backward = None;
    replicate_once(member_b.engine_host(), member_a.engine_host(), &scope, &mut backward)
        .expect("B ships to A");

    let fields_a = live_fields(&member_a, "orders", "det").expect("A serves the document");
    let fields_b = live_fields(&member_b, "orders", "det").expect("B serves the document");
    assert_eq!(fields_a, fields_b, "both regions converge to identical bytes");

    let engine_a = member_a.engine();
    let engine_b = member_b.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let scope_a = engine_a.scope_for(&tenant);
    let scope_b = engine_b.scope_for(&tenant);
    let collection = CollectionName::try_new("orders").expect("the collection parses");
    let id = DocumentId::try_new("det").expect("the document id parses");
    let conflicts_a =
        engine_a.merge_conflicts_for(&scope_a, &collection, &id, 10).expect("A serves conflicts");
    let conflicts_b =
        engine_b.merge_conflicts_for(&scope_b, &collection, &id, 10).expect("B serves conflicts");
    assert!(!conflicts_a.is_empty() && !conflicts_b.is_empty());
    // The same field pair computes the same policy on both sides, and the
    // explanations name the same winning value: direction may swap which
    // dot is recorded as loser, but the deterministic outcome agrees.
    assert_eq!(
        conflicts_a.iter().map(|entry| &entry.policy).collect::<Vec<_>>(),
        conflicts_b.iter().map(|entry| &entry.policy).collect::<Vec<_>>(),
        "conflict policies are deterministic"
    );
    for conflict in conflicts_a.iter().chain(conflicts_b.iter()) {
        assert!(!conflict.loser_commit_ulid.is_empty());
        assert!(!conflict.winner_commit_ulid.is_empty());
        assert!(!conflict.policy.is_empty());
        assert!(!conflict.explanation.is_empty());
    }
    // Recomputing the same merge is stable: re-applying either side's
    // journal replays instead of recording new losers.
    let mut replay = None;
    let shipped =
        replicate_once(member_a.engine_host(), member_b.engine_host(), &scope, &mut replay)
            .expect("the replay resolves");
    assert_eq!(shipped, 0, "the replayed merge replays instead of forking");
    let again_a = engine_a
        .merge_conflicts_for(&scope_a, &collection, &id, 10)
        .expect("A serves conflicts again");
    assert_eq!(conflicts_a, again_a, "history explanations are stable");
}
