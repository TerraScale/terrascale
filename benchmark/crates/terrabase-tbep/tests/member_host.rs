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
    match transaction.commit(LocalDurability::Synced) {
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
    let (_id, feed) = host.reactive_subscribe(&scope, &spec).expect("the subscription opens");
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
