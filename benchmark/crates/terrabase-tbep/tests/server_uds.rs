//! The UDS binding end-to-end suite (SE-14): the engine server serves
//! every frame family it owns over a real Unix-domain socket — channel
//! binding, the sync-delivery family end to end (session, mutations with
//! idempotent retry, delta and snapshot pulls, checkpoint acks), the
//! backbone subscription streams with content-free trigger
//! notifications (PD-2026-08-26-13 D3), the topology/admin reads, the
//! supervision contract, and the query-cancellation live signal
//! (GAP-Q12) — each through the typed wire frames a C# host sends.
//!
//! The socket-file security model (owner-only `0600`) is asserted on the
//! bound path. The QUIC binding is deferred per PD-2026-08-26-11 D1;
//! this suite is the local link class's evidence.

use std::io::Read as _;
use std::os::unix::fs::PermissionsExt as _;
use std::os::unix::net::UnixStream;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use terrabase_tbep::IdempotencyKey;
use terrabase_tbep::SyncDeliveryHost;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::TbepServer;
use terrabase_tbep::generated::AdminStatusRequest;
use terrabase_tbep::generated::BackboneSubscribeRequest;
use terrabase_tbep::generated::ChannelBindRequest;
use terrabase_tbep::generated::DocumentReadRequest;
use terrabase_tbep::generated::MutationOutcome;
use terrabase_tbep::generated::QueryCancelRequest;
use terrabase_tbep::generated::ShutdownReason;
use terrabase_tbep::generated::SubscriptionStart;
use terrabase_tbep::generated::SupervisionHealthRequest;
use terrabase_tbep::generated::SupervisionShutdownRequest;
use terrabase_tbep::generated::SyncChange;
use terrabase_tbep::generated::SyncCheckpointAckRequest;
use terrabase_tbep::generated::SyncDeltaPullRequest;
use terrabase_tbep::generated::SyncField;
use terrabase_tbep::generated::SyncMutationSubmitRequest;
use terrabase_tbep::generated::SyncSessionSetupRequest;
use terrabase_tbep::generated::SyncSnapshotPullRequest;
use terrabase_tbep::generated::TbepBody;
use terrabase_tbep::generated::TopologyReadRequest;

/// One bound server plus one connected client stream.
struct Fixture {
    #[allow(dead_code)]
    server: terrabase_tbep::TbepServerHandle,
    socket: PathBuf,
    stream: UnixStream,
    correlation: u64,
}

impl Fixture {
    fn bind(tag: &str) -> Self {
        let socket =
            std::env::temp_dir().join(format!("tbep-uds-{tag}-{}.sock", std::process::id()));
        let server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(9)))
            .expect("the UDS listener binds");
        let stream = UnixStream::connect(&socket).expect("the client connects");
        Self { server, socket, stream, correlation: 0 }
    }

    fn send(&mut self, frame: &TbepFrame) {
        frame.write_stream(&mut self.stream).expect("the client writes");
    }

    fn next_correlation(&mut self) -> u64 {
        self.correlation += 1;
        self.correlation
    }

    fn receive(&mut self) -> TbepFrame {
        TbepFrame::read_stream(&mut self.stream)
            .expect("the stream stays healthy")
            .expect("a frame arrives")
    }

    /// Sends one request and returns the answering frame (causation =
    /// the request's correlation).
    fn round_trip(&mut self, frame: &TbepFrame) -> TbepFrame {
        let causation = frame.correlation();
        self.send(frame);
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        loop {
            let answer = self.receive();
            if answer.causation() == causation {
                return answer;
            }
            // Unsolicited notifications (batches, triggers) may interleave
            // before the answer; the caller's subscriptions own those.
            assert!(
                answer.correlation() == 0,
                "an unexpected solicited frame arrived: {:?}",
                answer.kind()
            );
            assert!(std::time::Instant::now() < deadline, "the answer never arrived");
        }
    }

    /// Seals one body with the next correlation and returns the answer.
    fn call(
        &mut self,
        body: impl Into<TbepBody>,
        idempotency: Option<&IdempotencyKey>,
    ) -> TbepFrame {
        let correlation = self.next_correlation();
        let frame = TbepFrame::seal(body, correlation, 0, idempotency).expect("the body seals");
        self.round_trip(&frame)
    }

    fn bind_channel(&mut self, tenant: &str) {
        let answer = self
            .call(ChannelBindRequest { principal: "backend".into(), tenant: tenant.into() }, None);
        assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = self.stream.shutdown(std::net::Shutdown::Both);
    }
}

#[test]
fn the_socket_file_is_owner_only() {
    let fixture = Fixture::bind("permissions");
    let mode =
        std::fs::metadata(&fixture.socket).expect("the socket file exists").permissions().mode();
    assert_eq!(mode & 0o777, 0o600, "the UDS binding is owner-only");
    drop(fixture);
}

#[test]
fn tenant_scoped_operations_require_the_channel_binding() {
    let mut fixture = Fixture::bind("unbound");
    let answer = fixture
        .call(DocumentReadRequest { collection: "orders".into(), document: "d-01".into() }, None);
    match answer.body() {
        TbepBody::Error(error) => {
            assert_eq!(error.code, terrabase_tbep::generated::ErrorCode::UnboundChannel);
        }
        other => panic!("expected the unbound-channel refusal, got {other:?}"),
    }
    // A second bind on the same connection is refused with already_bound.
    fixture.bind_channel("tenant-a");
    let answer = fixture
        .call(ChannelBindRequest { principal: "backend".into(), tenant: "tenant-a".into() }, None);
    match answer.body() {
        TbepBody::Error(error) => {
            assert_eq!(error.code, terrabase_tbep::generated::ErrorCode::AlreadyBound);
        }
        other => panic!("expected the already-bound refusal, got {other:?}"),
    }
}

#[test]
fn sync_delivery_flows_end_to_end_over_the_socket() {
    let mut fixture = Fixture::bind("sync-delivery");
    fixture.bind_channel("tenant-a");

    // Session setup.
    let setup_answer =
        fixture.call(SyncSessionSetupRequest { collection: "orders".into(), resume: None }, None);
    let session_id = match setup_answer.body() {
        TbepBody::SyncSessionSetupResponse(response) => response.session_id,
        other => panic!("expected a session, got {other:?}"),
    };

    // A server-ack mutation (PD-2026-08-26-12 D2): the idempotency key is
    // the retry identity.
    let key = IdempotencyKey::new("op-01").expect("key");
    let mutation = SyncMutationSubmitRequest {
        session_id,
        document: "d-01".into(),
        change: SyncChange::Put(vec![SyncField {
            name: "qty".into(),
            value: terrabase_tbep::TbepValue::Integer(3),
        }]),
    };
    let mutation_answer = fixture.call(mutation.clone(), Some(&key));
    let receipt = match mutation_answer.body() {
        TbepBody::SyncMutationSubmitResponse(response) => match &response.outcome {
            MutationOutcome::Applied(receipt) => receipt.clone(),
            MutationOutcome::Rejected(reason) => {
                panic!("the mutation was rejected: {reason:?}")
            }
        },
        other => panic!("expected a mutation outcome, got {other:?}"),
    };
    assert_eq!(receipt.operation_id, "op-01");

    // The retry with the same key answers the recorded receipt.
    let retry_answer = fixture.call(mutation, Some(&key));
    match retry_answer.body() {
        TbepBody::SyncMutationSubmitResponse(response) => match &response.outcome {
            MutationOutcome::Applied(retried) => assert_eq!(retried.sequence, receipt.sequence),
            MutationOutcome::Rejected(reason) => panic!("the retry was rejected: {reason:?}"),
        },
        other => panic!("expected a retry outcome, got {other:?}"),
    }

    // Delta pull from the setup checkpoint and a snapshot pull.
    let setup_checkpoint = match setup_answer.body() {
        TbepBody::SyncSessionSetupResponse(response) => response.checkpoint.clone(),
        _ => unreachable!("checked above"),
    };
    let delta_answer =
        fixture.call(SyncDeltaPullRequest { session_id, from: setup_checkpoint }, None);
    let next = match delta_answer.body() {
        TbepBody::SyncDeltaPullResponse(response) => {
            assert_eq!(response.payload_codec, "loro-delta/1");
            assert!(!response.payload.is_empty());
            response.next.clone()
        }
        other => panic!("expected a delta batch, got {other:?}"),
    };
    let snapshot_answer = fixture.call(SyncSnapshotPullRequest { session_id }, None);
    match snapshot_answer.body() {
        TbepBody::SyncSnapshotPullResponse(response) => {
            assert_eq!(response.payload_codec, "loro-snapshot/1");
            assert!(!response.payload.is_empty());
        }
        other => panic!("expected a snapshot, got {other:?}"),
    }

    // Checkpoint acknowledgement.
    let ack_answer = fixture.call(SyncCheckpointAckRequest { session_id, checkpoint: next }, None);
    assert!(matches!(ack_answer.body(), TbepBody::SyncCheckpointAckResponse(_)));
}

#[test]
fn backbone_subscription_streams_batches_and_triggers() {
    let mut fixture = Fixture::bind("backbone");
    fixture.bind_channel("tenant-a");

    // A session to feed the journal.
    let setup_answer =
        fixture.call(SyncSessionSetupRequest { collection: "orders".into(), resume: None }, None);
    let session_id = match setup_answer.body() {
        TbepBody::SyncSessionSetupResponse(response) => response.session_id,
        other => panic!("expected a session, got {other:?}"),
    };

    // The subscription arrives before the commit.
    let subscribe_answer = fixture.call(
        BackboneSubscribeRequest {
            subscription_id: 42,
            collections: Some(vec!["orders".into()]),
            from: SubscriptionStart::Live,
        },
        None,
    );
    assert!(matches!(subscribe_answer.body(), TbepBody::BackboneSubscribeResponse(_)));

    // One commit publication: the consumer receives the ordered batch and
    // then the content-free trigger (push the hint, pull the delta).
    let key = IdempotencyKey::new("op-42").expect("key");
    let mutation_answer = fixture.call(
        SyncMutationSubmitRequest {
            session_id,
            document: "d-42".into(),
            change: SyncChange::Put(vec![SyncField {
                name: "qty".into(),
                value: terrabase_tbep::TbepValue::Integer(1),
            }]),
        },
        Some(&key),
    );
    assert!(matches!(mutation_answer.body(), TbepBody::SyncMutationSubmitResponse(_)));

    let deadline = std::time::Instant::now() + Duration::from_secs(10);
    let mut batch = None;
    let mut trigger = None;
    while (batch.is_none() || trigger.is_none()) && std::time::Instant::now() < deadline {
        let frame = fixture.receive();
        match frame.body() {
            TbepBody::SubscriptionBatch(body) => {
                assert_eq!(body.subscription_id, 42);
                assert_eq!(frame.causation(), 42);
                assert_eq!(frame.correlation(), 0);
                batch = Some(body.events.len());
            }
            TbepBody::TriggerNotification(body) => {
                // The trigger carries no document data: identifiers only.
                assert_eq!(body.subscription_id, 42);
                assert_eq!(body.collection, "orders");
                trigger = Some(body.sequence);
            }
            other => panic!("unexpected frame on the subscription: {other:?}"),
        }
    }
    assert_eq!(batch, Some(1), "the batch carries exactly the committed event");
    assert!(trigger.is_some(), "the content-free trigger arrived");
}

#[test]
fn topology_admin_and_supervision_flow_over_the_socket() {
    let mut fixture = Fixture::bind("supervision");
    fixture.bind_channel("tenant-a");

    let topology_answer = fixture.call(TopologyReadRequest {}, None);
    match topology_answer.body() {
        TbepBody::TopologyReadResponse(response) => {
            assert_eq!(response.binding, terrabase_tbep::generated::BindingKind::LocalUds);
        }
        other => panic!("expected the topology read, got {other:?}"),
    }

    let status_answer = fixture.call(AdminStatusRequest {}, None);
    match status_answer.body() {
        TbepBody::AdminStatusResponse(response) => {
            assert!(!response.registry_version.is_empty());
        }
        other => panic!("expected the admin status, got {other:?}"),
    }

    let health_answer = fixture.call(SupervisionHealthRequest {}, None);
    assert!(matches!(health_answer.body(), TbepBody::SupervisionHealthResponse(_)));

    // Graceful shutdown answers, then the connection closes.
    let shutdown_answer =
        fixture.call(SupervisionShutdownRequest { reason: ShutdownReason::Operator }, None);
    match shutdown_answer.body() {
        TbepBody::SupervisionShutdownResponse(response) => {
            assert_eq!(response.state, terrabase_tbep::generated::HealthState::Draining);
        }
        other => panic!("expected the draining answer, got {other:?}"),
    }
    let mut rest = Vec::new();
    let _ = fixture.stream.read_to_end(&mut rest);
    assert!(rest.is_empty(), "the connection closed cleanly after shutdown");
}

#[test]
fn the_query_cancellation_live_signal_is_a_typed_operation() {
    // GAP-Q12 (PD-2026-08-26-14 D3): the wire-side cancellation lifecycle
    // handoff is the query.cancel typed operation. The engine-backed
    // query host is the named integrator wiring; the sync-delivery host
    // answers the typed refusal — the live signal itself rides the same
    // frame the engine host will serve.
    let mut fixture = Fixture::bind("query-cancel");
    fixture.bind_channel("tenant-a");
    let answer = fixture.call(QueryCancelRequest { query_id: 7 }, None);
    match answer.body() {
        TbepBody::Error(error) => {
            assert_eq!(error.code, terrabase_tbep::generated::ErrorCode::OperationRefused);
            assert!(error.detail.contains("engine-backed host"));
        }
        other => panic!("expected the typed refusal, got {other:?}"),
    }
}

#[test]
fn two_connections_bind_independent_channels() {
    let socket = std::env::temp_dir().join(format!("tbep-uds-two-{}.sock", std::process::id()));
    let mut server =
        TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(3))).expect("the listener binds");
    let mut first = UnixStream::connect(&socket).expect("first connects");
    let mut second = UnixStream::connect(&socket).expect("second connects");
    for (stream, tenant) in [(&mut first, "tenant-a"), (&mut second, "tenant-b")] {
        let frame = TbepFrame::seal(
            ChannelBindRequest { principal: "backend".into(), tenant: tenant.into() },
            1,
            0,
            None,
        )
        .expect("seals");
        frame.write_stream(stream).expect("writes");
        let answer = TbepFrame::read_stream(stream).expect("reads").expect("the bind answer");
        assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));
    }
    let _ = first.shutdown(std::net::Shutdown::Both);
    let _ = second.shutdown(std::net::Shutdown::Both);
    server.shutdown();
}
