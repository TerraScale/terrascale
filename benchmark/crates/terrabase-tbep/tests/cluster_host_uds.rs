//! The cluster-backed host end to end over a real UDS socket (P5): a
//! multi-member cluster serves the protocol's document plane — quorum
//! writes with acknowledgement receipts, repairing reads — and a
//! BACKUP cluster answers every client mutation with the typed
//! `backup_cluster_read_only` error, its replication position in the
//! detail.

use std::os::unix::net::UnixStream;
use std::sync::Arc;
use std::time::Duration;

use terrabase_cluster::{ClusterHost, ClusterMode, MemberSpec};
use terrabase_tbep::cluster_host::ClusterTbepHost;
use terrabase_tbep::codec::TbepValue;
use terrabase_tbep::generated::ErrorCode;
use terrabase_tbep::generated::{
    Acknowledgement, ChannelBindRequest, ClusterMode as WireMode, DocumentPut, DocumentReadRequest,
    DocumentWriteRequest, ExpectedVersion, TopologyReadRequest, WriteOutcome,
};
use terrabase_tbep::{IdempotencyKey, TbepBody, TbepFrame, TbepServer};

/// One bound cluster-backed server plus one connected client stream.
struct Fixture {
    server: terrabase_tbep::TbepServerHandle,
    stream: UnixStream,
    correlation: u64,
}

impl Fixture {
    fn bind(tag: &str, mode: ClusterMode) -> Self {
        let root = std::env::temp_dir().join(format!(
            "tbep-cluster-uds-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        let spec = vec![
            MemberSpec { name: "member-a".into(), region: "eu-1".into() },
            MemberSpec { name: "member-b".into(), region: "eu-1".into() },
            MemberSpec { name: "member-c".into(), region: "eu-1".into() },
        ];
        let cluster = ClusterHost::create(&root, "cell-a", mode, "created", &spec, "tenant-a")
            .expect("the cluster creates");
        let host = ClusterTbepHost::new(cluster, "member-a");
        let socket =
            std::env::temp_dir().join(format!("tbep-cluster-{tag}-{}.sock", std::process::id()));
        let server = TbepServer::bind(&socket, Arc::new(host)).expect("the listener binds");
        let stream = UnixStream::connect(&socket).expect("the client connects");
        Self { server, stream, correlation: 0 }
    }

    fn call(
        &mut self,
        body: impl Into<TbepBody>,
        idempotency: Option<&IdempotencyKey>,
    ) -> TbepFrame {
        self.correlation += 1;
        let frame = TbepFrame::seal(body, self.correlation, 0, idempotency).expect("seals");
        frame.write_stream(&mut self.stream).expect("writes");
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        loop {
            let answer =
                TbepFrame::read_stream(&mut self.stream).expect("healthy").expect("arrives");
            if answer.causation() == self.correlation {
                return answer;
            }
            assert!(std::time::Instant::now() < deadline, "the answer never arrived");
        }
    }

    fn bind_channel(&mut self) {
        let answer = self.call(
            ChannelBindRequest { principal: "backend".into(), tenant: "tenant-a".into() },
            None,
        );
        assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = self.stream.shutdown(std::net::Shutdown::Both);
        self.server.shutdown();
    }
}

fn order_body() -> TbepValue {
    TbepValue::Object(vec![
        ("item".to_owned(), TbepValue::String("bookshelf".to_owned())),
        ("quantity".to_owned(), TbepValue::Integer(2)),
    ])
}

#[test]
fn an_active_cluster_serves_quorum_writes_and_reads_over_uds() {
    let mut fixture = Fixture::bind("active", ClusterMode::Active);
    fixture.bind_channel();

    // The write coordinates through the cluster: the receipt names the
    // achieved acknowledgement level.
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: order_body(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-cluster-1".into(),
            ack: Some(Acknowledgement::RegionalQuorum),
        },
        Some(&IdempotencyKey::new("op-cluster-1").expect("valid key")),
    );
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the cluster write answered, got {:?}", answer.kind());
    };
    let WriteOutcome::Committed(receipt) = &response.outcome else {
        panic!("the coordinated write commits, got {:?}", response.outcome);
    };
    assert_eq!(receipt.acknowledgement, Some(Acknowledgement::RegionalQuorum));
    assert_eq!(receipt.operation_id, "op-cluster-1");

    // The read serves the converged body.
    let answer = fixture.call(
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-1".into(),
            level: None,
        },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the cluster read answered");
    };
    assert!(response.found);
    assert_eq!(response.document.as_ref().expect("served").body, order_body());

    // The topology answers the cluster's REAL mode.
    let answer = fixture.call(TopologyReadRequest {}, None);
    let TbepBody::TopologyReadResponse(response) = answer.body() else {
        panic!("the topology answered");
    };
    assert_eq!(response.cluster_mode, Some(WireMode::Active));
    assert!(response.engine_topology_available);
}

#[test]
fn a_backup_cluster_answers_every_client_mutation_with_the_typed_read_only_error() {
    let mut fixture = Fixture::bind("backup", ClusterMode::Backup);
    fixture.bind_channel();

    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: order_body(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-backup-1".into(),
            ack: None,
        },
        Some(&IdempotencyKey::new("op-backup-1").expect("valid key")),
    );
    let TbepBody::Error(error) = answer.body() else {
        panic!("the backup write answers the typed error, got {:?}", answer.kind());
    };
    assert_eq!(error.code, ErrorCode::BackupClusterReadOnly);
    assert!(
        error.detail.contains("position="),
        "the replication position rides the machine-readable detail: {}",
        error.detail
    );

    // The topology names the backup mode honestly.
    let answer = fixture.call(TopologyReadRequest {}, None);
    let TbepBody::TopologyReadResponse(response) = answer.body() else {
        panic!("the backup topology answered");
    };
    assert_eq!(response.cluster_mode, Some(WireMode::Backup));
}

#[test]
fn a_foreign_tenant_never_binds_to_the_cluster() {
    let mut fixture = Fixture::bind("tenant", ClusterMode::Active);
    let answer = fixture
        .call(ChannelBindRequest { principal: "backend".into(), tenant: "tenant-b".into() }, None);
    assert!(
        matches!(answer.body(), TbepBody::Error(_)),
        "the cluster serves its own tenant only, got {:?}",
        answer.kind()
    );
}
