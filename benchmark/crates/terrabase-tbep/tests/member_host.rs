//! Focused TB-1a integration coverage for the staged `MemberHost`.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase_sync_core::SyncCheckpoint;
use terrabase_tbep::host::TbepNotification;
use terrabase_tbep::registry::{
    Acknowledgement, BackboneSubscribeRequest, BatchWrite, BoundScope, ChannelBindRequest,
    DocumentBatchRequest, DocumentPut, DocumentReadRequest, ExpectedVersion, MutationOutcome,
    SubscriptionStart, SyncChange, SyncDeltaPullRequest, SyncField, SyncMutationSubmitRequest,
    SyncSessionSetupRequest, WriteOutcome,
};
use terrabase_tbep::{HostingError, MemberHost, MemberHostOptions, TbepHost, TbepValue};

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

fn object(value: i64) -> TbepValue {
    TbepValue::Object(vec![("value".into(), TbepValue::Integer(value))])
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
fn one_engine_journal_drives_sync_and_backbone_revisions() {
    let location = TestLocation::new("journal");
    let host = open(location.path());
    let scope = scope();
    bind(&host, &scope);

    let setup = host
        .sync_session_setup(
            &scope,
            &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
        )
        .expect("the sync session opens");
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

    let response = host
        .document_batch(
            &scope,
            &DocumentBatchRequest {
                items: vec![
                    BatchWrite {
                        document: DocumentPut {
                            id: "orders/1".into(),
                            collection: "orders".into(),
                            body: object(1),
                        },
                        expected: ExpectedVersion::Any,
                        operation_id: "batch-item-1".into(),
                    },
                    BatchWrite {
                        document: DocumentPut {
                            id: "orders/2".into(),
                            collection: "orders".into(),
                            body: object(2),
                        },
                        expected: ExpectedVersion::Any,
                        operation_id: "batch-item-2".into(),
                    },
                ],
                ack: Some(Acknowledgement::LocalSynced),
                usage: None,
            },
            "batch-commit",
        )
        .expect("the engine commits the batch");
    let receipt = match response.outcome {
        WriteOutcome::Committed(receipt) => receipt,
        outcome => panic!("the batch committed: {outcome:?}"),
    };

    let notification = feed.recv().expect("the durable batch is published");
    let TbepNotification::Batch(batch) = notification else {
        panic!("the journal batch precedes its trigger")
    };
    assert_eq!(batch.events.len(), 2, "one atomic commit publishes both writes");
    assert_eq!(batch.events[0].document, "orders/1");
    assert_eq!(batch.events[1].document, "orders/2");
    assert!(batch.events.iter().all(|event| event.sequence == receipt.sequence));

    let delta = host
        .sync_delta_pull(
            &scope,
            &SyncDeltaPullRequest { session_id: setup.session_id, from: setup.checkpoint },
        )
        .expect("the sync delta comes from the same journal");
    assert_eq!(delta.commits, 2);
    assert_ne!(delta.next, SyncCheckpoint::initial().to_bytes());
}

#[test]
fn sync_mutations_commit_to_the_engine_and_rebuild_after_reopen() {
    let location = TestLocation::new("reopen");
    let scope = scope();
    let checkpoint = {
        let host = open(location.path());
        bind(&host, &scope);
        let setup = host
            .sync_session_setup(
                &scope,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("the sync session opens");
        let response = host
            .sync_mutation_submit(
                &scope,
                &SyncMutationSubmitRequest {
                    session_id: setup.session_id,
                    document: "orders/9".into(),
                    change: SyncChange::Put(vec![SyncField {
                        name: "value".into(),
                        value: TbepValue::Integer(9),
                    }]),
                    usage: None,
                },
                "sync-durable-9",
            )
            .expect("the sync mutation commits through the engine");
        assert!(matches!(response.outcome, MutationOutcome::Applied(_)));

        let read = host
            .document_read(
                &scope,
                &DocumentReadRequest {
                    collection: "orders".into(),
                    document: "orders/9".into(),
                    level: None,
                },
            )
            .expect("the engine reads the sync mutation");
        assert!(read.found);
        let delta = host
            .sync_delta_pull(
                &scope,
                &SyncDeltaPullRequest { session_id: setup.session_id, from: setup.checkpoint },
            )
            .expect("the journal advances the live session");
        delta.next
    };

    let reopened = open(location.path());
    bind(&reopened, &scope);
    let rebuilt = reopened
        .sync_session_setup(
            &scope,
            &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
        )
        .expect("the reopened host rebuilds from the durable journal");
    assert_eq!(rebuilt.checkpoint, checkpoint);
    let read = reopened
        .document_read(
            &scope,
            &DocumentReadRequest {
                collection: "orders".into(),
                document: "orders/9".into(),
                level: None,
            },
        )
        .expect("the durable engine still owns the document");
    assert!(read.found);
}
