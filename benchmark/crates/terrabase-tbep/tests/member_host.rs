//! Focused TB-1a integration coverage for the staged `MemberHost`.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase::engine::CommitOutcome;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase_tbep::host::TbepNotification;
use terrabase_tbep::registry::{
    BackboneSubscribeRequest, BoundScope, ChannelBindRequest, SubscriptionStart,
};
use terrabase_tbep::{HostingError, MemberHost, MemberHostOptions, TbepHost};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(
            std::env::temp_dir()
                .join(format!("terrabase-tb1a-{label}-{}-{number}", std::process::id())),
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
        principal: "spiffe://local/tb1a-test".into(),
        tenant: "tenant-a".into(),
        project: "project-a".into(),
        database: "database-a".into(),
    }
}

fn open(location: &Path) -> MemberHost {
    MemberHost::open(location, 7, "tenant-a", "project-a", "database-a", MemberHostOptions::tb1a())
        .expect("the opted-in member host opens")
}

fn bind(host: &MemberHost, scope: &BoundScope) {
    host.bind_channel(&ChannelBindRequest { scope: scope.clone() })
        .expect("the database scope binds");
}

/// Writes one document through the member's engine (the same durable
/// storage the public API serves) and asserts the commit.
fn put(host: &MemberHost, id: &str, value: i64) {
    put_with_durability(host, id, value, LocalDurability::Synced);
}

fn put_with_durability(host: &MemberHost, id: &str, value: i64, durability: LocalDurability) {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    let operation =
        OperationId::try_new(&format!("put-{id}")).expect("the operation identity parses");
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
    match transaction.commit(durability) {
        CommitOutcome::Committed(_) => {}
        outcome => panic!("the write committed: {outcome:?}"),
    }
}

#[test]
fn construction_stays_off_without_the_tb1a_flag() {
    let location = TestLocation::new("disabled");
    let result = MemberHost::open(
        location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::default(),
    );
    assert!(matches!(result, Err(HostingError::Refused(_))));
    assert!(!location.path().exists(), "a disabled construction must not open storage");
}

#[test]
fn one_engine_journal_drives_backbone_revisions() {
    let location = TestLocation::new("journal");
    let host = open(location.path());
    let scope = scope();
    bind(&host, &scope);

    let (_, feed) = host
        .backbone_subscribe(
            &scope,
            &BackboneSubscribeRequest {
                subscription_id: 41,
                collections: Some(vec!["orders".into()]),
                from: SubscriptionStart::Live,
            },
        )
        .expect("the backbone subscription opens");

    put(&host, "orders/1", 1);
    put(&host, "orders/2", 2);

    // The next backbone call synchronizes the delivery plane with the
    // engine's publication journal before answering.
    host.backbone_acknowledge(
        &scope,
        &terrabase_tbep::registry::BackboneAcknowledgeRequest {
            subscription_id: 41,
            through_sequence: 0,
        },
    )
    .expect("the acknowledgement answers");

    let notification = feed.recv().expect("the durable writes are published");
    let TbepNotification::Batch(batch) = notification else {
        panic!("the journal batch precedes its trigger")
    };
    assert_eq!(batch.events.len(), 2, "both durable writes publish");
    assert_eq!(batch.events[0].document, "orders/1");
    assert_eq!(batch.events[1].document, "orders/2");
}

#[test]
fn repeated_backbone_sync_does_not_republish_durable_records() {
    let location = TestLocation::new("journal-repeat");
    let host = open(location.path());
    let scope = scope();
    bind(&host, &scope);
    let (_, feed) = host
        .backbone_subscribe(
            &scope,
            &BackboneSubscribeRequest {
                subscription_id: 42,
                collections: Some(vec!["orders".into()]),
                from: SubscriptionStart::Live,
            },
        )
        .expect("the backbone subscription opens");

    put(&host, "orders/1", 1);
    host.backbone_acknowledge(
        &scope,
        &terrabase_tbep::registry::BackboneAcknowledgeRequest {
            subscription_id: 42,
            through_sequence: 0,
        },
    )
    .expect("the first acknowledgement synchronizes the journal");
    let first = feed.recv().expect("the durable write is published");
    assert!(matches!(first, TbepNotification::Batch(_)));

    host.backbone_acknowledge(
        &scope,
        &terrabase_tbep::registry::BackboneAcknowledgeRequest {
            subscription_id: 42,
            through_sequence: 0,
        },
    )
    .expect("repeating the acknowledgement remains idempotent");
    assert!(
        matches!(feed.try_recv(), Ok(TbepNotification::Trigger(_))),
        "the journal trigger is delivered once per durable write"
    );
    assert!(
        matches!(feed.try_recv(), Err(std::sync::mpsc::TryRecvError::Empty)),
        "the journal cursor prevents republishing the same record"
    );
}

/// One reactive subscription: the Seed frame carries the current state,
/// and a later commit pumps through to a delta (`S-04`).
#[test]
fn reactive_subscriptions_seed_then_delta_on_commit() {
    use terrabase_sync_core::server::{Comparison, Condition, FieldValue, SubscriptionSpec};
    use terrabase_tbep::reactive::ReactiveFrame;

    let location = TestLocation::new("reactive");
    let host = open(location.path());
    let scope = scope();
    bind(&host, &scope);
    put(&host, "orders/1", 1);

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
        host.reactive_subscribe(&scope, &spec, None).expect("the subscription opens");
    let ReactiveFrame::Seed { members, .. } = feed.recv().expect("the seed arrives") else {
        panic!("the first frame is the seed");
    };
    assert_eq!(members.len(), 1, "the committed document is a member");
    assert_eq!(members[0].0, "orders/1");

    // A new commit pumps through to one Insert delta.
    put(&host, "orders/2", 2);
    host.reactive_pump(&scope).expect("the pump advances");
    let ReactiveFrame::Delta { delta, .. } = feed.recv().expect("the delta arrives") else {
        panic!("the second frame is a delta");
    };
    assert!(matches!(
        delta.operation,
        terrabase_sync_core::server::DeltaOp::Insert { ref document, .. } if document == "orders/2"
    ));
    assert_eq!(delta.revision, 1, "the subscription's first revision");
}

/// The local reactive visibility gate (`S-04` done-when): a committed
/// write is visible to a subscribed result view within 50 ms p99 on the
/// same member. The M1 pump is pull-per-tick; the clock covers commit →
/// pump → delta arrival.
#[test]
fn reactive_local_visibility_stays_under_50ms_p99() {
    use std::time::Instant;
    use terrabase_sync_core::server::{Comparison, Condition, FieldValue, SubscriptionSpec};
    use terrabase_tbep::reactive::ReactiveFrame;

    let location = TestLocation::new("reactive-latency");
    let host = open(location.path());
    let scope = scope();
    bind(&host, &scope);

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
        host.reactive_subscribe(&scope, &spec, None).expect("the subscription opens");
    let _ = feed.recv().expect("the seed arrives");

    let mut samples = Vec::with_capacity(200);
    for round in 0..200_u64 {
        let started = Instant::now();
        put_with_durability(
            &host,
            &format!("orders/{round}"),
            i64::try_from(round).unwrap_or(0),
            LocalDurability::Buffered,
        );
        host.reactive_pump(&scope).expect("the pump advances");
        let ReactiveFrame::Delta { .. } = feed.recv().expect("the delta arrives") else {
            panic!("round {round}: the write is visible as a delta");
        };
        samples.push(started.elapsed());
    }
    samples.sort();
    let p99 = samples[(samples.len() * 99) / 100];
    assert!(p99 < std::time::Duration::from_millis(50), "p99 was {p99:?}");
}

/// Checkpoint resume (`S-04` done-when): a detached subscription's
/// missed revisions replay exactly; a resume the ring cannot cover
/// seeds fresh (Reset semantics).
#[test]
fn reactive_resume_replays_exactly_the_missed_revisions() {
    use terrabase_sync_core::server::{Comparison, Condition, FieldValue, SubscriptionSpec};
    use terrabase_tbep::reactive::ReactiveFrame;

    let location = TestLocation::new("reactive-resume");
    let host = open(location.path());
    let scope = scope();
    bind(&host, &scope);
    put(&host, "orders/1", 1);

    let spec = SubscriptionSpec {
        collection: "orders".into(),
        conditions: vec![Condition {
            field: "value".into(),
            op: Comparison::GreaterOrEqual,
            value: FieldValue::Number(0.0),
        }],
        projection: vec!["value".into()],
    };
    let (first, feed, _missed) =
        host.reactive_subscribe(&scope, &spec, None).expect("the subscription opens");
    let ReactiveFrame::Seed { .. } = feed.recv().expect("the seed") else {
        panic!("the first frame seeds");
    };

    // Two writes the client never sees (it detached after revision 0).
    put(&host, "orders/2", 2);
    put(&host, "orders/3", 3);
    host.reactive_pump(&scope).expect("the pump advances");
    let _first_delta = feed.recv().expect("rev 1");
    let _second_delta = feed.recv().expect("rev 2");

    // The transport closed: the subscription detaches, retaining state.
    host.reactive_detach(first);

    // Resume from revision 1: exactly revision 2 replays, in order.
    let (resumed, feed, missed) =
        host.reactive_subscribe(&scope, &spec, Some((first, 1))).expect("the resume opens");
    assert_eq!(missed.as_deref().map(<[_]>::len), Some(1), "one missed revision");
    assert!(
        matches!(
            &missed.as_deref().map(|d| &d[0]),
            Some(delta) if delta.revision == 2
        ),
        "the replay is revision 2"
    );
    drop(feed);
    host.reactive_unsubscribe(resumed);

    // A resume naming a subscription whose ring no longer covers the
    // checkpoint (an unknown or reaped id) seeds fresh: the Seed frame
    // answers, never a guess.
    let (_fresh, feed, missed) = host
        .reactive_subscribe(&scope, &spec, Some((999_999, 0)))
        .expect("the fresh subscription opens");
    assert!(missed.is_none(), "an uncoverable checkpoint resets");
    assert!(
        matches!(feed.recv().expect("the seed"), ReactiveFrame::Seed { .. }),
        "the reset path seeds"
    );
}
