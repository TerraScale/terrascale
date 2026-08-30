//! The gRPC serving contract rows (SE-14, ADR 0028): the tonic client
//! against the tonic server over a local Unix-domain socket.
//!
//! Coverage follows the retired UDS conformance family's core rows:
//! the identity discipline (unbound channels fail closed), the
//! idempotency pairing of mutations, the document round-trip, the
//! backbone stream lifecycle, the real sync-delivery session lifecycle,
//! fail-closed bounds, and the supervision shutdown — plus the
//! `TbepError` status-detail contract every client maps codes through.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::Mutex;

use prost::Message as _;
use terrabase_tbep::TbepHost;
use terrabase_tbep::grpc::TbepGrpcServer;
use terrabase_tbep::host::HostingError;
use terrabase_tbep::host::SyncDeliveryHost;
use terrabase_tbep::host::TbepNotification;
use terrabase_tbep::proto::terrabase::v1 as pb;
use terrabase_tbep::proto::terrabase::v1::backbone_client::BackboneClient;
use terrabase_tbep::proto::terrabase::v1::channel_client::ChannelClient;
use terrabase_tbep::proto::terrabase::v1::engine_client::EngineClient;
use terrabase_tbep::proto::terrabase::v1::maintenance_client::MaintenanceClient;
use terrabase_tbep::proto::terrabase::v1::sync_client::SyncClient;
use terrabase_tbep::registry;
use terrabase_tbep::value::TbepValue;
use tonic::Request;
use tonic::transport::Endpoint;

/// A minimal fixed-answer host: one document, one subscription feed.
struct FixedHost {
    feed: Mutex<Option<std::sync::mpsc::Sender<TbepNotification>>>,
}

fn bound_error(code: pb::ErrorCode) -> tonic::Status {
    let tbep_error = pb::TbepError { code: code as i32, detail: "fixture".into() };
    let mut value = Vec::new();
    tbep_error.encode(&mut value).expect("encodes");
    let rpc_status = pb::RpcStatus {
        code: 3,
        message: "fixture".into(),
        details: vec![pb::Any {
            type_url: "type.googleapis.com/terrabase.v1.TbepError".into(),
            value,
        }],
    };
    let mut details = Vec::new();
    rpc_status.encode(&mut details).expect("encodes");
    tonic::Status::with_details(tonic::Code::InvalidArgument, "fixture", details.into())
}

impl TbepHost for FixedHost {
    fn bind_channel(
        &self,
        request: &registry::ChannelBindRequest,
    ) -> Result<registry::ChannelBindResponse, HostingError> {
        Ok(registry::ChannelBindResponse {
            tenant: request.tenant.clone(),
            serving_instance: 7,
        })
    }

    fn document_read(
        &self,
        _tenant: &str,
        request: &registry::DocumentReadRequest,
    ) -> Result<registry::DocumentReadResponse, HostingError> {
        if request.collection != "orders" {
            return Ok(registry::DocumentReadResponse {
                found: false,
                document: None,
                position: None,
            });
        }
        Ok(registry::DocumentReadResponse {
            found: true,
            document: Some(registry::DocumentBody {
                id: request.document.clone(),
                collection: request.collection.clone(),
                version: 3,
                body: TbepValue::Object(vec![("total".into(), TbepValue::Integer(21))]),
            }),
            position: Some(9),
        })
    }

    fn document_write(
        &self,
        _tenant: &str,
        request: &registry::DocumentWriteRequest,
        idempotency_key: &str,
    ) -> Result<registry::DocumentWriteResponse, HostingError> {
        assert_eq!(idempotency_key, "op-1");
        Ok(registry::DocumentWriteResponse {
            outcome: registry::WriteOutcome::Committed(registry::CommitReceipt {
                sequence: 4,
                version: 1,
                operation_id: request.operation_id.clone(),
                acknowledgement: Some(registry::Acknowledgement::RegionalQuorum),
                backup: Some(registry::BackupOutcome::Full),
            }),
        })
    }

    fn document_delete(
        &self,
        _tenant: &str,
        _request: &registry::DocumentDeleteRequest,
        _idempotency_key: &str,
    ) -> Result<registry::DocumentDeleteResponse, HostingError> {
        Ok(registry::DocumentDeleteResponse {
            outcome: registry::WriteOutcome::Conflict,
        })
    }

    fn query_execute(
        &self,
        _tenant: &str,
        _request: &registry::QueryExecuteRequest,
    ) -> Result<registry::QueryExecuteResponse, HostingError> {
        Ok(registry::QueryExecuteResponse {
            documents: vec![],
            count: 0,
            terminal: registry::QueryTerminal::Complete,
            position: Some(1),
        })
    }

    fn query_cancel(
        &self,
        _tenant: &str,
        _request: &registry::QueryCancelRequest,
    ) -> Result<registry::QueryCancelResponse, HostingError> {
        Ok(registry::QueryCancelResponse {
            outcome: registry::CancelOutcome::Cancelled,
        })
    }

    fn family_read(
        &self,
        _tenant: &str,
        _request: &registry::FamilyReadRequest,
    ) -> Result<registry::FamilyReadResponse, HostingError> {
        Err(HostingError::Refused("families are not served by the fixture".into()))
    }

    fn family_write(
        &self,
        _tenant: &str,
        _request: &registry::FamilyWriteRequest,
        _idempotency_key: &str,
    ) -> Result<registry::FamilyWriteResponse, HostingError> {
        Err(HostingError::Refused("families are not served by the fixture".into()))
    }

    fn backbone_subscribe(
        &self,
        _tenant: &str,
        request: &registry::BackboneSubscribeRequest,
    ) -> Result<
        (
            registry::BackboneSubscribeResponse,
            std::sync::mpsc::Receiver<TbepNotification>,
        ),
        HostingError,
    > {
        let (sender, receiver) = std::sync::mpsc::channel();
        *self.feed.lock().expect("the fixture lock is sound") = Some(sender);
        Ok((
            registry::BackboneSubscribeResponse {
                subscription_id: request.subscription_id,
                start_sequence: 5,
            },
            receiver,
        ))
    }

    fn backbone_acknowledge(
        &self,
        _tenant: &str,
        request: &registry::BackboneAcknowledgeRequest,
    ) -> Result<registry::BackboneAcknowledgeResponse, HostingError> {
        Ok(registry::BackboneAcknowledgeResponse {
            subscription_id: request.subscription_id,
            acknowledged_through: request.through_sequence,
        })
    }

    fn backbone_unsubscribe(
        &self,
        _tenant: &str,
        request: &registry::BackboneUnsubscribeRequest,
    ) -> Result<registry::BackboneUnsubscribeResponse, HostingError> {
        Ok(registry::BackboneUnsubscribeResponse {
            subscription_id: request.subscription_id,
        })
    }

    fn sync_session_setup(
        &self,
        _tenant: &str,
        _request: &registry::SyncSessionSetupRequest,
    ) -> Result<registry::SyncSessionSetupResponse, HostingError> {
        Err(HostingError::SessionRefused("the fixture serves no sync sessions".into()))
    }

    fn sync_delta_pull(
        &self,
        _tenant: &str,
        _request: &registry::SyncDeltaPullRequest,
    ) -> Result<registry::SyncDeltaPullResponse, HostingError> {
        Err(HostingError::SessionRefused("the fixture serves no sync sessions".into()))
    }

    fn sync_snapshot_pull(
        &self,
        _tenant: &str,
        _request: &registry::SyncSnapshotPullRequest,
    ) -> Result<registry::SyncSnapshotPullResponse, HostingError> {
        Err(HostingError::SessionRefused("the fixture serves no sync sessions".into()))
    }

    fn sync_checkpoint_ack(
        &self,
        _tenant: &str,
        _request: &registry::SyncCheckpointAckRequest,
    ) -> Result<registry::SyncCheckpointAckResponse, HostingError> {
        Err(HostingError::SessionRefused("the fixture serves no sync sessions".into()))
    }

    fn sync_mutation_submit(
        &self,
        _tenant: &str,
        _request: &registry::SyncMutationSubmitRequest,
        _idempotency_key: &str,
    ) -> Result<registry::SyncMutationSubmitResponse, HostingError> {
        Err(HostingError::SessionRefused("the fixture serves no sync sessions".into()))
    }

    fn sync_session_close(
        &self,
        _tenant: &str,
        _request: &registry::SyncSessionCloseRequest,
    ) -> Result<registry::SyncSessionCloseResponse, HostingError> {
        Err(HostingError::SessionRefused("the fixture serves no sync sessions".into()))
    }

    fn topology_read(
        &self,
        _tenant: &str,
    ) -> Result<registry::TopologyReadResponse, HostingError> {
        Ok(registry::TopologyReadResponse {
            binding: registry::BindingKind::LocalUds,
            subsystems: vec!["engine".into()],
            engine_topology_available: false,
            cluster_mode: None,
        })
    }

    fn admin_status(&self) -> Result<registry::AdminStatusResponse, HostingError> {
        Ok(registry::AdminStatusResponse {
            registry_version: registry::REGISTRY_VERSION.to_string(),
            frames_served: 0,
            sessions: 0,
            subscriptions: 0,
            scopes: 0,
        })
    }

    fn health(&self) -> Result<registry::SupervisionHealthResponse, HostingError> {
        Ok(registry::SupervisionHealthResponse {
            state: registry::HealthState::Serving,
            sessions: 0,
            subscriptions: 1,
            frames_served: 1,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &registry::SupervisionShutdownRequest,
    ) -> Result<registry::SupervisionShutdownResponse, HostingError> {
        Ok(registry::SupervisionShutdownResponse {
            state: registry::HealthState::Draining,
            in_flight: 2,
        })
    }
}

/// Decodes the `TbepError` code out of a status's detail envelope.
fn tbep_code(status: &tonic::Status) -> Option<pb::ErrorCode> {
    let rpc_status = pb::RpcStatus::decode(status.details()).ok()?;
    let any = rpc_status.details.first()?;
    if any.type_url != "type.googleapis.com/terrabase.v1.TbepError" {
        return None;
    }
    let error = pb::TbepError::decode(any.value.as_slice()).ok()?;
    pb::ErrorCode::try_from(error.code).ok()
}

async fn connect(socket: PathBuf) -> tonic::transport::Channel {
    async fn bridge(socket: PathBuf) -> std::io::Result<hyper_util::rt::TokioIo<tokio::net::UnixStream>> {
        let stream = tokio::net::UnixStream::connect(socket).await?;
        Ok(hyper_util::rt::TokioIo::new(stream))
    }
    Endpoint::from_shared("http://localhost".to_string())
        .expect("the endpoint parses")
        .connect_with_connector(tower::service_fn(move |_uri| bridge(socket.clone())))
        .await
        .expect("the client connects over the local socket")
}

fn identified<T>(message: T) -> Request<T> {
    let mut request = Request::new(message);
    request.metadata_mut().insert("x-tb-principal", "operator".parse().unwrap());
    request.metadata_mut().insert("x-tb-tenant", "tenant-a".parse().unwrap());
    request
}

fn mutation<T>(message: T) -> Request<T> {
    let mut request = identified(message);
    request.metadata_mut().insert("x-tb-idempotency-key", "op-1".parse().unwrap());
    request
}

fn socket_path(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbep-grpc-{name}-{}.sock", std::process::id()))
}

#[tokio::test]
async fn unbound_calls_fail_closed_with_the_channel_code() {
    let socket = socket_path("unbound");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut engine = EngineClient::new(channel);

    let status = engine
        .document_read(Request::new(pb::DocumentReadRequest {
            collection: "orders".into(),
            document: "d-1".into(),
            level: 0,
        }))
        .await
        .expect_err("an unbound call fails");
    assert_eq!(status.code(), tonic::Code::Unauthenticated);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::UnboundChannel));
    listener.shutdown();
}

#[tokio::test]
async fn mutations_require_their_idempotency_key() {
    let socket = socket_path("idempotency");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut engine = EngineClient::new(channel);

    let status = engine
        .document_write(identified(pb::DocumentWriteRequest {
            document: Some(pb::DocumentPut {
                id: "d-1".into(),
                collection: "orders".into(),
                body: Some(value_pb()),
            }),
            expected: Some(pb::ExpectedVersion {
                discipline: Some(pb::expected_version::Discipline::Any(pb::ExpectedAny {})),
            }),
            operation_id: "op-1".into(),
            ack: 0,
        }))
        .await
        .expect_err("a keyless mutation fails");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::IdempotencyPairing));
    listener.shutdown();
}

fn value_pb() -> pb::Value {
    pb::Value {
        kind: Some(pb::value::Kind::Map(pb::ValueMap {
            entries: vec![pb::ValueEntry {
                name: "total".into(),
                value: Some(pb::Value {
                    kind: Some(pb::value::Kind::Integer(21)),
                }),
            }],
        })),
    }
}

#[tokio::test]
async fn bind_write_read_and_delete_round_trip() {
    let socket = socket_path("roundtrip");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;

    let mut channels = ChannelClient::new(channel.clone());
    let bound = channels
        .bind(Request::new(pb::ChannelBindRequest {
            principal: "operator".into(),
            tenant: "tenant-a".into(),
        }))
        .await
        .expect("the bind answers");
    assert_eq!(bound.into_inner().serving_instance, 7);

    let mut engine = EngineClient::new(channel.clone());
    let written = engine
        .document_write(mutation(pb::DocumentWriteRequest {
            document: Some(pb::DocumentPut {
                id: "d-1".into(),
                collection: "orders".into(),
                body: Some(value_pb()),
            }),
            expected: Some(pb::ExpectedVersion {
                discipline: Some(pb::expected_version::Discipline::Any(pb::ExpectedAny {})),
            }),
            operation_id: "op-1".into(),
            ack: 0,
        }))
        .await
        .expect("the write commits");
    match written.into_inner().outcome.expect("the outcome rides").outcome {
        Some(pb::write_outcome::Outcome::Committed(receipt)) => {
            assert_eq!(receipt.sequence, 4);
            assert_eq!(receipt.acknowledgement, pb::Acknowledgement::AckRegionalQuorum as i32);
            assert_eq!(receipt.backup, pb::BackupOutcome::BackupFull as i32);
        }
        other => panic!("unexpected write outcome {other:?}"),
    }

    let read = engine
        .document_read(identified(pb::DocumentReadRequest {
            collection: "orders".into(),
            document: "d-1".into(),
            level: 0,
        }))
        .await
        .expect("the read answers");
    let read = read.into_inner();
    assert!(read.found);
    let document = read.document.expect("the document rides");
    assert_eq!(document.version, 3);
    assert_eq!(document.body.expect("the body rides").kind, value_pb().kind);

    let deleted = engine
        .document_delete(mutation(pb::DocumentDeleteRequest {
            collection: "orders".into(),
            document: "d-1".into(),
            expected: Some(pb::ExpectedVersion {
                discipline: Some(pb::expected_version::Discipline::Exact(3)),
            }),
            operation_id: "op-2".into(),
            ack: 0,
        }))
        .await
        .expect("the delete answers");
    match deleted.into_inner().outcome.expect("the outcome rides").outcome {
        Some(pb::write_outcome::Outcome::Conflict(_)) => {}
        other => panic!("unexpected delete outcome {other:?}"),
    }
    listener.shutdown();
}

#[tokio::test]
async fn bounds_fail_closed_before_the_host_runs() {
    let socket = socket_path("bounds");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut engine = EngineClient::new(channel);

    let status = engine
        .document_read(identified(pb::DocumentReadRequest {
            collection: "c".repeat(65),
            document: "d-1".into(),
            level: 0,
        }))
        .await
        .expect_err("an over-bound collection fails");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::InvalidRequest));
    listener.shutdown();
}

#[tokio::test]
async fn the_backbone_stream_delivers_confirmation_batches_and_close() {
    let socket = socket_path("backbone");
    let host = Arc::new(FixedHost { feed: Mutex::new(None) });
    let server = Arc::new(TbepGrpcServer::new(
        Arc::clone(&host) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut backbone = BackboneClient::new(channel);

    let mut stream = backbone
        .subscribe(identified(pb::BackboneSubscribeRequest {
            subscription_id: 11,
            collections: vec!["orders".into()],
            from: Some(pb::SubscriptionStart {
                start: Some(pb::subscription_start::Start::Live(pb::SubscriptionLive {})),
            }),
        }))
        .await
        .expect("the subscription opens")
        .into_inner();

    let first = stream.message().await.expect("the confirmation arrives").expect("the stream is open");
    match first.event.expect("the event rides") {
        pb::backbone_stream_event::Event::Subscribed(response) => {
            assert_eq!(response.subscription_id, 11);
            assert_eq!(response.start_sequence, 5);
        }
        other => panic!("unexpected first event {other:?}"),
    }

    let feed = host.feed.lock().expect("the fixture lock is sound").take();
    let feed = feed.expect("the host holds the feed");
    feed.send(TbepNotification::Batch(registry::SubscriptionBatch {
        subscription_id: 11,
        from_sequence: 5,
        events: vec![registry::ChangeEvent {
            sequence: 5,
            operation_id: "op-9".into(),
            kind: registry::ChangeKind::Put,
            collection: "orders".into(),
            document: "d-1".into(),
        }],
        next_sequence: 6,
    }))
    .expect("the batch delivers");
    feed.send(TbepNotification::Trigger(registry::TriggerNotification {
        subscription_id: 11,
        collection: "orders".into(),
        sequence: 6,
        operation_id: "op-10".into(),
    }))
    .expect("the trigger delivers");
    feed.send(TbepNotification::Closed(registry::SubscriptionClosed {
        subscription_id: 11,
        reason: registry::SubscriptionCloseReason::Replaced,
    }))
    .expect("the close delivers");

    match stream.message().await.expect("the batch arrives").expect("the stream is open")
        .event
        .expect("the event rides")
    {
        pb::backbone_stream_event::Event::Batch(batch) => {
            assert_eq!(batch.events.len(), 1);
            assert_eq!(batch.next_sequence, 6);
        }
        other => panic!("unexpected batch event {other:?}"),
    }
    match stream.message().await.expect("the trigger arrives").expect("the stream is open")
        .event
        .expect("the event rides")
    {
        pb::backbone_stream_event::Event::Trigger(trigger) => {
            assert_eq!(trigger.sequence, 6);
        }
        other => panic!("unexpected trigger event {other:?}"),
    }
    match stream.message().await.expect("the close arrives").expect("the stream is open")
        .event
        .expect("the event rides")
    {
        pb::backbone_stream_event::Event::Closed(closed) => {
            assert_eq!(closed.reason, pb::SubscriptionCloseReason::Replaced as i32);
        }
        other => panic!("unexpected close event {other:?}"),
    }
    assert!(
        stream.message().await.expect("the stream ends cleanly").is_none(),
        "the close frame ends the stream"
    );
    listener.shutdown();
}

#[tokio::test]
async fn the_real_sync_delivery_core_serves_one_session_over_grpc() {
    let socket = socket_path("sync");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(SyncDeliveryHost::new(1)) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut sync = SyncClient::new(channel);

    let setup = sync
        .session_setup(identified(pb::SyncSessionSetupRequest {
            collection: "orders".into(),
            resume: vec![],
        }))
        .await
        .expect("the session opens")
        .into_inner();
    let session_id = setup.session_id;
    assert!(!setup.checkpoint.is_empty());

    let mutation = sync
        .mutation_submit(mutation(pb::SyncMutationSubmitRequest {
            session_id,
            document: "d-1".into(),
            change: Some(pb::SyncChange {
                change: Some(pb::sync_change::Change::Put(pb::SyncPut {
                    fields: vec![pb::SyncField {
                        name: "total".into(),
                        value: Some(pb::Value {
                            kind: Some(pb::value::Kind::Integer(21)),
                        }),
                    }],
                })),
            }),
        }))
        .await
        .expect("the mutation applies")
        .into_inner();
    match mutation.outcome.expect("the outcome rides").outcome {
        Some(pb::mutation_outcome::Outcome::Applied(receipt)) => {
            assert_eq!(receipt.sequence, 1);
        }
        other => panic!("unexpected mutation outcome {other:?}"),
    }

    let delta = sync
        .delta_pull(identified(pb::SyncDeltaPullRequest {
            session_id,
            from: setup.checkpoint.clone(),
        }))
        .await
        .expect("the delta answers")
        .into_inner();
    assert_eq!(delta.commits, 1);
    assert!(!delta.next.is_empty());

    sync.checkpoint_ack(identified(pb::SyncCheckpointAckRequest {
        session_id,
        checkpoint: delta.next.clone(),
    }))
    .await
    .expect("the checkpoint acknowledges");

    sync.session_close(identified(pb::SyncSessionCloseRequest { session_id }))
        .await
        .expect("the session closes");

    let status = sync
        .delta_pull(identified(pb::SyncDeltaPullRequest { session_id, from: delta.next }))
        .await
        .expect_err("a closed session refuses");
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::SessionRefused));
    listener.shutdown();
}

#[tokio::test]
async fn maintenance_answers_topology_health_and_shutdown() {
    let socket = socket_path("maintenance");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::NetworkMtls,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut maintenance = MaintenanceClient::new(channel);

    let topology = maintenance
        .topology_read(identified(pb::TopologyReadRequest {}))
        .await
        .expect("the topology answers")
        .into_inner();
    assert_eq!(topology.binding, pb::BindingKind::NetworkMtls as i32);

    let health = maintenance
        .supervision_health(identified(pb::SupervisionHealthRequest {}))
        .await
        .expect("the health answers")
        .into_inner();
    assert_eq!(health.state, pb::HealthState::Serving as i32);

    let shutdown = maintenance
        .supervision_shutdown(identified(pb::SupervisionShutdownRequest {
            reason: pb::ShutdownReason::Operator as i32,
        }))
        .await
        .expect("the shutdown answers")
        .into_inner();
    assert_eq!(shutdown.state, pb::HealthState::Draining as i32);
    assert_eq!(shutdown.in_flight, 2);
    listener.shutdown();
}
